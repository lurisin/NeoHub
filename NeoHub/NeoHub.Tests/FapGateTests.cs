using DSC.TLink.ITv2;
using DSC.TLink.ITv2.Enumerations;
using DSC.TLink.ITv2.MediatR;
using DSC.TLink.ITv2.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Abstractions;
using NeoHub.Services;
using NeoHub.Tests.Infrastructure;

namespace NeoHub.Tests;

/// <summary>
/// A FAP command can wait for its session before it is written (the session is not ready yet, for example right
/// after a takeover). The FAP setting is checked again right before the write, and a command whose caller gave up
/// is never written later; the reply says so.
/// </summary>
public class FapGateTests
{
    private const string Id = TestHost.SessionId;
    private const ushort FapCommandWord = 0x0908;
    private const string Disabled = "FAP commands are disabled in the NeoHub settings";

    private sealed class Policy : IFapCommandPolicy
    {
        public volatile bool On;
        public bool AllowFapCommands => On;
    }

    private sealed class OneSession : IITv2SessionManager
    {
        private readonly IITv2Session _session;
        public OneSession(IITv2Session session) => _session = session;
        public void RegisterSession(string sessionId, IITv2Session session) { }
        public void UnregisterSession(string sessionId, IITv2Session session) { }
        public bool IsCurrentSession(string sessionId, IITv2Session session) => ReferenceEquals(session, _session);
        public IITv2Session? GetSession(string sessionId) => sessionId == Id ? _session : null;
        public IEnumerable<string> GetActiveSessions() => new[] { Id };
        public Task DisconnectSessionAsync(string sessionId) => Task.CompletedTask;
        public Task DisconnectAllAsync() => Task.CompletedTask;
    }

    private static int FapFrames(RealSession session) =>
        session.Transport.SentPayloads.Count(p => FakeTransport.CarriesCommandWord(p, FapCommandWord));

    /// <summary>Keeps a real session from becoming ready (it is ready after 2 s without inbound traffic) until cancelled.</summary>
    private static async Task KeepBusyAsync(RealSession session, CancellationToken ct)
    {
        try
        {
            while (!ct.IsCancellationRequested)
            {
                session.Transport.Feed(0, new ConnectionPoll());
                await Task.Delay(400, ct);
            }
        }
        catch (OperationCanceledException)
        {
        }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task The_setting_is_checked_again_right_before_the_write(bool staysOn)
    {
        var s = await RealSession.StartAsync(Id, waitUntilReady: false);
        var policy = new Policy { On = true };
        var mediator = new SessionMediator(null!, new OneSession(s.Session), NullLogger<SessionMediator>.Instance, new IFapCommandPolicy[] { policy });
        s.Transport.OnSend = packet =>
        {
            if (packet.Message is ActivateFap fap)
                s.Transport.Feed(packet.SenderSequence, new CommandResponse { CommandSequence = fap.CommandSequence, ResponseCode = CommandResponseCode.Success });
        };

        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(8));
        var pending = mediator.Handle(new SessionCommand { SessionID = Id, MessageData = new ActivateFap { Partition = 1, Type = FapType.Fire } }, timeout.Token);
        await Task.Delay(100);
        Assert.False(pending.IsCompleted);   // waiting for the session to become ready
        if (!staysOn)
            policy.On = false;   // saved off while the command waits

        await s.WaitReadyAsync();
        var response = await pending.WaitAsync(TimeSpan.FromSeconds(5));
        await Task.Delay(300);

        if (staysOn)
        {
            // Positive control: the same wait, with the setting still on, sends it once.
            Assert.True(response.Success);
            Assert.Equal(1, FapFrames(s));
        }
        else
        {
            Assert.False(response.Success);
            Assert.True(response.NotSent);
            Assert.Equal(Disabled, response.ErrorMessage);
            Assert.Equal(0, FapFrames(s));
        }
        await s.Session.DisposeAsync();
    }

    [Fact]
    public async Task A_command_withdrawn_before_the_write_is_never_written()
    {
        var s = await RealSession.StartAsync(Id, waitUntilReady: false);
        var mediator = new SessionMediator(null!, new OneSession(s.Session), NullLogger<SessionMediator>.Instance, new IFapCommandPolicy[] { new Policy { On = true } });
        var withdrawn = false;

        var pending = mediator.Handle(new SessionCommand
        {
            SessionID = Id,
            MessageData = new ActivateFap { Partition = 1, Type = FapType.Fire },
            MayStillSend = () => !Volatile.Read(ref withdrawn)
        }, CancellationToken.None);
        await Task.Delay(100);
        Volatile.Write(ref withdrawn, true);
        await s.WaitReadyAsync();
        var response = await pending.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.False(response.Success);
        Assert.True(response.NotSent);
        Assert.Equal(SessionMediator.WithdrawnMessage, response.ErrorMessage);
        Assert.Equal(0, FapFrames(s));
        await s.Session.DisposeAsync();
    }

    private static async Task<(TestHost Host, FakePanel Panel, FakeSession A, Task LoopA)> ConnectFirstAsync()
    {
        var host = await TestHost.StartAsync(allowFap: true);
        var panel = new FakePanel();
        var a = host.NewSession(panel, "A");
        var loopA = host.Connect(a);
        Assert.True(await TestHost.WaitUntil(() => host.Manager.IsCurrentSession(Id, a)));
        await host.Manager.LifecycleIdleAsync(Id).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, host.State.GetPartitions(Id).Count);
        return (host, panel, a, loopA);
    }

    [Fact]
    public async Task A_command_that_waited_too_long_for_the_session_says_not_sent_and_never_leaves()
    {
        var (host, panel, _, _) = await ConnectFirstAsync();
        await using var _host = host;

        // A newer connection takes the session over and stays not ready for longer than the command waits.
        var b = await RealSession.StartAsync(Id, host.Services.GetRequiredService<ILoggerFactory>(), waitUntilReady: false);
        TransportPanel.Attach(b, panel);
        using var busy = new CancellationTokenSource();
        var keepBusy = KeepBusyAsync(b, busy.Token);
        var loopB = host.Connect(b.Session);
        Assert.True(await TestHost.WaitUntil(() => host.Manager.IsCurrentSession(Id, b.Session)));

        var n = host.Client.Count;
        await host.FromClient("""{"type":"activate_fap","session_id":"TESTSESSION","partition":1,"fap_type":"fire","request_id":"w"}""");
        var results = host.Client.FramesSince(n).Where(f => f.StartsWith("{\"type\":\"command_result\"")).ToList();
        Assert.Equal(new[]
        {
            """{"type":"command_result","request_id":"w","success":false,"code":null,"code_kind":null,"message":"The FAP command was not sent: the panel session was not ready, or ended, before it could be sent"}"""
        }, results);

        // The session becomes ready afterwards: its connect pull runs, and the FAP command never leaves.
        busy.Cancel();
        await keepBusy;
        await b.WaitReadyAsync();
        await host.Manager.LifecycleIdleAsync(Id).WaitAsync(TimeSpan.FromSeconds(15));
        await Task.Delay(500);
        Assert.Equal(0, FapFrames(b));
        Assert.Contains(b.Transport.SentMessages(), m => m is CommandRequestMessage);   // the pull did talk to the panel
        Assert.Contains(host.Logs.Entries, e => e.Level == LogLevel.Warning && e.Text.Contains("client1") && e.Text.Contains("was not sent"));

        b.Transport.EndInbound();
        await loopB.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task A_command_that_waits_for_the_session_is_sent_once_when_it_becomes_ready()
    {
        // Positive control for the test above: the takeover connection becomes ready within the wait.
        var (host, panel, _, _) = await ConnectFirstAsync();
        await using var _host = host;
        var b = await RealSession.StartAsync(Id, host.Services.GetRequiredService<ILoggerFactory>(), waitUntilReady: false);
        TransportPanel.Attach(b, panel);
        var loopB = host.Connect(b.Session);
        Assert.True(await TestHost.WaitUntil(() => host.Manager.IsCurrentSession(Id, b.Session)));

        var n = host.Client.Count;
        await host.FromClient("""{"type":"activate_fap","session_id":"TESTSESSION","partition":1,"fap_type":"fire","request_id":"v"}""");
        var results = host.Client.FramesSince(n).Where(f => f.StartsWith("{\"type\":\"command_result\"")).ToList();
        Assert.Equal(new[] { """{"type":"command_result","request_id":"v","success":true,"code":null,"code_kind":null,"message":null}""" }, results);
        Assert.Equal(1, FapFrames(b));

        await host.Manager.LifecycleIdleAsync(Id).WaitAsync(TimeSpan.FromSeconds(15));
        Assert.Equal(1, FapFrames(b));
        b.Transport.EndInbound();
        await loopB.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
