using DSC.TLink.ITv2.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NeoHub.Tests.Infrastructure;

namespace NeoHub.Tests;

/// <summary>Closing a real ITv2Session: END_SESSION, idempotent disposal, and a receive pump that ends cleanly.</summary>
public class RealSessionLifecycleTests
{
    private static int EndSessions(RealSession s) => s.Transport.SentMessages().Count(m => m is ConnectionEndSession);

    [Fact]
    public async Task Abandon_closes_without_END_SESSION_and_a_later_dispose_does_nothing()
    {
        var s = await RealSession.StartAsync("S1");

        await s.Session.AbandonAsync();
        await s.Session.DisposeAsync();   // the connection handler's "await using"

        await s.PumpTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(s.PumpTask.IsCompletedSuccessfully);
        Assert.Equal(0, EndSessions(s));
        Assert.True(s.Transport.Disposed);
        await Assert.ThrowsAnyAsync<OperationCanceledException>(async () =>
        {
            await foreach (var _ in s.Session.GetNotificationsAsync()) { }
        });
    }

    [Fact]
    public async Task Dispose_sends_END_SESSION_once_when_called_twice()
    {
        var s = await RealSession.StartAsync("S1");

        await s.Session.DisposeAsync();
        await s.Session.DisposeAsync();

        await s.PumpTask.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(s.PumpTask.IsCompletedSuccessfully);
        Assert.Equal(1, EndSessions(s));
    }

    [Fact]
    public async Task Pump_ends_cleanly_when_the_panel_closes_before_the_session_is_disposed()
    {
        var s = await RealSession.StartAsync("S1");

        s.Transport.EndInbound();
        await s.PumpTask.WaitAsync(TimeSpan.FromSeconds(5));
        await s.Session.DisposeAsync();

        Assert.True(s.PumpTask.IsCompletedSuccessfully);
    }

    [Fact]
    public async Task A_send_racing_the_close_fails_as_a_cancellation()
    {
        var s = await RealSession.StartAsync("S1");
        await s.Session.AbandonAsync();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => s.Session.SendAsync(new ConnectionPoll()));
    }

    [Fact]
    public async Task Forty_takeover_cycles_with_real_sessions_leave_no_session_no_state_and_no_unobserved_exception()
    {
        var unobserved = 0;
        void OnUnobserved(object? sender, UnobservedTaskExceptionEventArgs e) => Interlocked.Increment(ref unobserved);
        TaskScheduler.UnobservedTaskException += OnUnobserved;
        var all = new List<RealSession>();
        try
        {
            await using var host = await TestHost.StartAsync(troubleRefreshIntervalSeconds: 60);
            var loggers = host.Services.GetRequiredService<ILoggerFactory>();
            for (var i = 0; i < 40; i++)
            {
                var a = await RealSession.StartAsync(TestHost.SessionId, loggers, waitUntilReady: false);
                var b = await RealSession.StartAsync(TestHost.SessionId, loggers, waitUntilReady: false);
                all.Add(a); all.Add(b);
                var loopA = host.Connect(a.Session);
                Assert.True(await TestHost.WaitUntil(() => host.Manager.IsCurrentSession(TestHost.SessionId, a.Session)));
                var loopB = host.Connect(b.Session);
                Assert.True(await TestHost.WaitUntil(() => host.Manager.IsCurrentSession(TestHost.SessionId, b.Session)));
                await loopA.WaitAsync(TimeSpan.FromSeconds(5));   // A was closed by the takeover

                if (i % 2 == 0)
                    await host.Manager.DisconnectSessionAsync(TestHost.SessionId);
                else
                    b.Transport.EndInbound();                       // the panel drops the new connection
                await loopB.WaitAsync(TimeSpan.FromSeconds(5));
                await host.Manager.LifecycleIdleAsync(TestHost.SessionId).WaitAsync(TimeSpan.FromSeconds(10));
                Assert.False(host.HasSession, $"cycle {i}");
                Assert.Null(host.State.GetSession(TestHost.SessionId));
                Assert.Equal(0, a.Transport.SentMessages().Count(m => m is ConnectionEndSession));
            }

            await Task.WhenAll(all.Select(s => s.PumpTask)).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.All(all, s => Assert.True(s.PumpTask.IsCompletedSuccessfully));
            Assert.All(all, s => Assert.True(s.Transport.Disposed));
            Assert.True(await TestHost.WaitUntil(() => host.Manager.PendingLifecycleCount == 0));
            Assert.True(await TestHost.WaitUntil(() => host.RefreshWorkers() == 0));
            Assert.DoesNotContain(host.Logs.Entries, e => e.Level >= LogLevel.Error);
        }
        finally
        {
            for (var i = 0; i < 3; i++) { GC.Collect(); GC.WaitForPendingFinalizers(); }
            TaskScheduler.UnobservedTaskException -= OnUnobserved;
        }
        Assert.Equal(0, unobserved);
    }
}
