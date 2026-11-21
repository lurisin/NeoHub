using System.Text.Json;
using DSC.TLink.ITv2.Enumerations;
using DSC.TLink.ITv2.MediatR;
using DSC.TLink.ITv2.Messages;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NeoHub.Services;
using NeoHub.Tests.Infrastructure;

namespace NeoHub.Tests;

/// <summary>
/// A panel that reconnects before NeoHub noticed its previous connection was gone (two connections with the same
/// session id, overlapping): the newest connection takes the session over and the old one's teardown removes nothing.
/// </summary>
public class SessionTakeoverTests
{
    private const string Id = TestHost.SessionId;

    private static string ArmHome(string requestId) =>
        $$"""{"type":"arm_home","session_id":"{{Id}}","partition_number":1,"code":null,"request_id":"{{requestId}}"}""";

    private static async Task<(FakeSession Session, Task Loop)> ConnectAndPullAsync(TestHost host, FakePanel panel, string name)
    {
        var session = host.NewSession(panel, name);
        var loop = host.Connect(session);
        Assert.True(await TestHost.WaitUntil(() => host.Manager.IsCurrentSession(Id, session)), $"{name} registered");
        await host.Manager.LifecycleIdleAsync(Id).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(host.State.GetSession(Id)?.IsInitialized == true, $"{name}: connect pull finished");
        return (session, loop);
    }

    private static int Count(TestHost host, string template) =>
        host.Logs.Entries.Count(e => e.Template == template);

    private static bool IsEmptyFullState(string frame) =>
        frame.Contains("\"type\":\"full_state\"") && frame.Contains("\"sessions\":[]");

    [Fact]
    public async Task Overlapping_reconnect_keeps_the_new_session_registered_with_its_state()
    {
        await using var host = await TestHost.StartAsync();
        var panel = new FakePanel();
        var (a, loopA) = await ConnectAndPullAsync(host, panel, "A");
        var state = host.State.GetSession(Id)!;
        Assert.Equal(1, host.RefreshWorkers());

        // The old connection goes stale and the panel reconnects with the same id.
        a.Dead = true;
        var framesBefore = host.Client.Count;
        var sentBeforeB = panel.Received.Count;
        var (b, loopB) = await ConnectAndPullAsync(host, panel, "B");

        // B owns the session; A was closed without END_SESSION and its loop ended.
        Assert.Same(b, host.Manager.GetSession(Id));
        Assert.Equal(1, a.AbandonCount);
        await loopA.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Single(host.Logs.Entries, e => e.Level == LogLevel.Warning && e.Text.StartsWith("Session replaced by a newer connection"));

        // The old connection's teardown removed nothing: same registration, same state object, one refresh worker.
        Assert.True(host.HasSession);
        Assert.Same(state, host.State.GetSession(Id));
        Assert.Equal(1, host.RefreshWorkers());
        Assert.Equal(0, Count(host, "Removed session state for {Session}"));
        Assert.Equal(0, Count(host, "Session unregistered. Active sessions: {Count}"));

        // The connect pull ran again, on B.
        Assert.Contains(b.Sent, m => m is CommandRequestMessage { Request: ConnectionSystemCapabilities });
        Assert.DoesNotContain(a.Sent.Skip(0), m => m is CommandRequestMessage { Request: ConnectionSystemCapabilities } && a.Sent.Count(x => x is CommandRequestMessage { Request: ConnectionSystemCapabilities }) > 1);
        Assert.True(panel.Received.Count > sentBeforeB);

        // Clients never saw the session disappear.
        var frames = host.Client.FramesSince(framesBefore);
        Assert.DoesNotContain(frames, IsEmptyFullState);
        Assert.Contains(frames, f => f.Contains("\"type\":\"full_state\"") && f.Contains($"\"session_id\":\"{Id}\""));

        // Commands go to the new connection.
        var n = host.Client.Count;
        await host.FromClient(ArmHome("r1"));
        Assert.Contains(b.Sent, m => m is PartitionArm);
        Assert.DoesNotContain(a.Sent, m => m is PartitionArm);
        Assert.Contains("""{"type":"command_result","request_id":"r1","success":true""", host.Client.FramesSince(n).Single());

        // Notifications of the new connection update the state.
        n = host.Client.Count;
        b.Notify(new NotificationLifestyleZoneStatus { ZoneNumber = 2, Status = NotificationLifestyleZoneStatus.LifeStyleZoneStatusCode.Open });
        Assert.True(await TestHost.WaitUntil(() => host.State.GetZone(Id, 2)?.IsOpen == true));
        Assert.Contains(host.Client.FramesSince(n), f => f.Contains("\"zone_update\"") && f.Contains("\"zone_number\":2,\"open\":true"));

        // A later normal disconnect removes the state once.
        n = host.Client.Count;
        b.EndConnection();
        await loopB.WaitAsync(TimeSpan.FromSeconds(5));
        await host.Manager.LifecycleIdleAsync(Id).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(host.HasSession);
        Assert.Null(host.State.GetSession(Id));
        Assert.Equal(1, Count(host, "Removed session state for {Session}"));
        Assert.True(await TestHost.WaitUntil(() => host.RefreshWorkers() == 0));
        Assert.Contains(host.Client.FramesSince(n), IsEmptyFullState);
        Assert.True(await TestHost.WaitUntil(() => host.Manager.PendingLifecycleCount == 0));
    }

    [Fact]
    public async Task Notifications_from_a_replaced_connection_are_dropped()
    {
        await using var host = await TestHost.StartAsync();
        var panel = new FakePanel();
        var a = new FakeSession(Id, panel, "A") { KeepStreamOpenOnClose = true };
        var loopA = host.Connect(a);
        await TestHost.WaitUntil(() => host.Manager.IsCurrentSession(Id, a));
        await host.Manager.LifecycleIdleAsync(Id);
        var (b, loopB) = await ConnectAndPullAsync(host, panel, "B");

        // A's stream is still delivering (its pump had read this before it was closed): not published.
        a.Notify(new NotificationLifestyleZoneStatus { ZoneNumber = 3, Status = NotificationLifestyleZoneStatus.LifeStyleZoneStatusCode.Open });
        a.EndConnection();
        await loopA.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(host.State.GetZone(Id, 3)!.IsOpen);
        Assert.Contains(host.Logs.Entries, e => e.Template == "Dropping {MessageType} from a connection that no longer owns the session");
        Assert.Same(b, host.Manager.GetSession(Id));

        b.EndConnection();
        await loopB;
    }

    [Fact]
    public async Task Takeover_during_the_connect_pull_ends_with_one_complete_state_and_one_refresh_worker()
    {
        await using var host = await TestHost.StartAsync(troubleRefreshIntervalSeconds: 60);
        var panel = new FakePanel();
        panel.HoldZoneStatus = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var a = host.NewSession(panel, "A");
        var loopA = host.Connect(a);
        Assert.True(await TestHost.WaitUntil(() => panel.Received.Any(m => m is CommandRequestMessage { Request: ModuleZoneStatus })));

        // B takes over while A's pull waits for its last answer; A's request fails when A is closed.
        var b = host.NewSession(panel, "B");
        var loopB = host.Connect(b);
        Assert.True(await TestHost.WaitUntil(() => a.AbandonCount == 1));
        var hold = panel.HoldZoneStatus!;
        panel.HoldZoneStatus = null;
        hold.SetResult();   // B's pull may already be waiting on it
        await host.Manager.LifecycleIdleAsync(Id).WaitAsync(TimeSpan.FromSeconds(10));

        var session = host.State.GetSession(Id)!;
        Assert.True(session.IsInitialized);
        Assert.Equal(2, host.State.GetPartitions(Id).Count);
        Assert.Equal(3, host.State.GetZones(Id).Count);
        Assert.Contains(b.Sent, m => m is CommandRequestMessage { Request: ModuleZoneStatus });
        Assert.True(await TestHost.WaitUntil(() => host.RefreshWorkers() == 1));
        Assert.DoesNotContain(host.Logs.Entries, e => e.Level >= LogLevel.Error);
        Assert.Contains(host.Logs.Entries, e => e.Text == "Command ended: the session was closed while it waited for the panel");

        b.EndConnection();
        await Task.WhenAll(loopA, loopB).WaitAsync(TimeSpan.FromSeconds(5));
        await host.Manager.LifecycleIdleAsync(Id);
        Assert.Null(host.State.GetSession(Id));
    }

    [Fact]
    public async Task Takeover_resets_the_programming_mode_flag_and_keeps_the_state()
    {
        await using var host = await TestHost.StartAsync();
        var panel = new FakePanel();
        var (a, loopA) = await ConnectAndPullAsync(host, panel, "A");
        host.State.UpdateSession(Id, s => s.IsInProgrammingMode = true);
        var state = host.State.GetSession(Id);

        var (b, loopB) = await ConnectAndPullAsync(host, panel, "B");

        Assert.Same(state, host.State.GetSession(Id));
        Assert.False(host.State.GetSession(Id)!.IsInProgrammingMode);
        Assert.Contains(host.Logs.Entries, e => e.Text == "Session taken over by a newer connection; keeping its state until the connect pull refreshes it");
        b.EndConnection();
        await Task.WhenAll(loopA, loopB).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_trouble_read_in_flight_at_takeover_is_not_reported_as_a_failure()
    {
        var floor = PartitionTroubleRefreshService.MinimumIntervalSeconds;
        PartitionTroubleRefreshService.MinimumIntervalSeconds = 1;
        try
        {
            await using var host = await TestHost.StartAsync(troubleRefreshIntervalSeconds: 1);
            var panel = new FakePanel();
            var (a, loopA) = await ConnectAndPullAsync(host, panel, "A");

            // A stops answering, so the next periodic read waits on it; then B takes over and A is closed under it.
            a.Dead = true;
            var readsBefore = a.Sent.Count(m => m is CommandRequestMessage { Request: ModulePartitionStatus });
            Assert.True(await TestHost.WaitUntil(() => a.Sent.Count(m => m is CommandRequestMessage { Request: ModulePartitionStatus }) > readsBefore, 5));
            var (b, loopB) = await ConnectAndPullAsync(host, panel, "B");

            // The refresh carries on with B.
            var readsOnB = b.Sent.Count(m => m is CommandRequestMessage { Request: ModulePartitionStatus });
            Assert.True(await TestHost.WaitUntil(() => b.Sent.Count(m => m is CommandRequestMessage { Request: ModulePartitionStatus }) > readsOnB, 5));
            Assert.Equal(1, host.RefreshWorkers());
            Assert.DoesNotContain(host.Logs.Entries, e => e.Category.EndsWith("PartitionTroubleRefreshService") && e.Level >= LogLevel.Warning);
            Assert.Contains(host.Logs.Entries, e => e.Template.StartsWith("Trouble status read of partition {Partition} ended by the disconnect"));

            b.EndConnection();
            await Task.WhenAll(loopA, loopB).WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            PartitionTroubleRefreshService.MinimumIntervalSeconds = floor;
        }
    }

    [Fact]
    public async Task Ui_disconnect_still_works_and_removes_the_state_once()
    {
        await using var host = await TestHost.StartAsync();
        var panel = new FakePanel();
        var (a, loopA) = await ConnectAndPullAsync(host, panel, "A");

        await host.Manager.DisconnectSessionAsync(Id);
        await loopA.WaitAsync(TimeSpan.FromSeconds(5));
        await host.Manager.LifecycleIdleAsync(Id);

        Assert.True(a.DisposeCount >= 1);
        Assert.Equal(0, a.AbandonCount);
        Assert.False(host.HasSession);
        Assert.Null(host.State.GetSession(Id));
        Assert.Equal(1, Count(host, "Removed session state for {Session}"));
        Assert.Equal(1, Count(host, "Session disconnected"));
    }

    [Fact]
    public async Task A_reconnect_while_a_ui_disconnect_is_still_closing_keeps_the_new_state()
    {
        await using var host = await TestHost.StartAsync();
        var panel = new FakePanel();
        var a = new FakeSession(Id, panel, "A") { DisposeDelayMs = 700 };   // END_SESSION takes a while
        var loopA = host.Connect(a);
        await TestHost.WaitUntil(() => host.Manager.IsCurrentSession(Id, a));
        await host.Manager.LifecycleIdleAsync(Id);

        var disconnect = host.Manager.DisconnectSessionAsync(Id);
        var (b, loopB) = await ConnectAndPullAsync(host, panel, "B");
        await disconnect;
        await host.Manager.LifecycleIdleAsync(Id);

        Assert.Same(b, host.Manager.GetSession(Id));
        Assert.True(host.State.GetSession(Id)?.IsInitialized == true);
        Assert.Equal(2, host.State.GetPartitions(Id).Count);
        b.EndConnection();
        await Task.WhenAll(loopA, loopB).WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Lifecycle_notifications_of_one_session_id_run_one_after_another_in_order()
    {
        var recorder = new LifecycleRecorder();
        await using var host = await TestHost.StartAsync(configure: s =>
        {
            s.AddSingleton(recorder);
            s.AddTransient<INotificationHandler<SessionConnectedNotification>, SlowLifecycleHandler>();
            s.AddTransient<INotificationHandler<SessionDisconnectedNotification>, SlowLifecycleHandler>();
        });
        var panel = new FakePanel();
        var (a, loopA) = await ConnectAndPullAsync(host, panel, "A");

        // A ends and B connects right away; A's (slow) Disconnected handlers finish before B's Connected start.
        a.EndConnection();
        await loopA;
        var (b, loopB) = await ConnectAndPullAsync(host, panel, "B");

        Assert.Equal(new[] { "start connected", "end connected", "start disconnected", "end disconnected", "start connected", "end connected" },
            recorder.Events());
        Assert.True(host.State.GetSession(Id)?.IsInitialized == true);
        Assert.Equal(2, host.State.GetPartitions(Id).Count);
        b.EndConnection();
        await loopB;
    }

    [Fact]
    public async Task Forty_rapid_connect_takeover_disconnect_cycles_leave_nothing_behind()
    {
        var unobserved = 0;
        void OnUnobserved(object? s, UnobservedTaskExceptionEventArgs e) => Interlocked.Increment(ref unobserved);
        TaskScheduler.UnobservedTaskException += OnUnobserved;
        try
        {
            await using var host = await TestHost.StartAsync(troubleRefreshIntervalSeconds: 60);
            var panel = new FakePanel();
            var sessions = new List<FakeSession>();
            var loops = new List<Task>();
            for (var i = 0; i < 40; i++)
            {
                var a = host.NewSession(panel, $"A{i}");
                var b = host.NewSession(panel, $"B{i}");
                sessions.Add(a); sessions.Add(b);
                loops.Add(host.Connect(a));
                if (i % 3 == 0)
                    await TestHost.WaitUntil(() => host.Manager.IsCurrentSession(Id, a));   // sometimes let A register first
                loops.Add(host.Connect(b));                                                // takeover (or B first, then A takes over)
                Assert.True(await TestHost.WaitUntil(() => a.AbandonCount + b.AbandonCount == 1), $"cycle {i}: one connection replaced the other");
                switch (i % 4)
                {
                    case 0: await host.Manager.DisconnectSessionAsync(Id); break;           // UI disconnect
                    case 1: a.EndConnection(); b.EndConnection(); break;                     // both connections drop
                    case 2: b.EndConnection(); a.EndConnection(); break;
                    default:                                                                 // the current one drops
                        (host.Manager.GetSession(Id) as FakeSession)?.EndConnection();
                        a.EndConnection(); b.EndConnection();
                        break;
                }
                await Task.WhenAll(loops).WaitAsync(TimeSpan.FromSeconds(10));
                await host.Manager.LifecycleIdleAsync(Id).WaitAsync(TimeSpan.FromSeconds(10));
                Assert.False(host.HasSession, $"cycle {i}: still registered");
                Assert.Null(host.State.GetSession(Id));
            }

            Assert.True(await TestHost.WaitUntil(() => host.RefreshWorkers() == 0));
            Assert.True(await TestHost.WaitUntil(() => host.Manager.PendingLifecycleCount == 0));
            Assert.All(sessions, s => Assert.True(s.IsClosed, $"{s} closed"));
            Assert.DoesNotContain(host.Logs.Entries, e => e.Level >= LogLevel.Error);
            Assert.False(host.Commands.IsCommandInProgress(Id));
        }
        finally
        {
            for (var i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }
        Assert.Equal(0, unobserved);
    }

    internal sealed class LifecycleRecorder
    {
        private readonly List<string> _events = new();
        public void Add(string e) { lock (_events) _events.Add(e); }
        public string[] Events() { lock (_events) return _events.ToArray(); }
    }

    internal sealed class SlowLifecycleHandler :
        INotificationHandler<SessionConnectedNotification>,
        INotificationHandler<SessionDisconnectedNotification>
    {
        private readonly LifecycleRecorder _recorder;
        public SlowLifecycleHandler(LifecycleRecorder recorder) => _recorder = recorder;

        public async Task Handle(SessionConnectedNotification notification, CancellationToken cancellationToken)
        {
            _recorder.Add("start connected");
            await Task.Delay(50, cancellationToken);
            _recorder.Add("end connected");
        }

        public async Task Handle(SessionDisconnectedNotification notification, CancellationToken cancellationToken)
        {
            _recorder.Add("start disconnected");
            await Task.Delay(300, cancellationToken);
            _recorder.Add("end disconnected");
        }
    }
}
