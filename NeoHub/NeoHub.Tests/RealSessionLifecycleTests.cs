using DSC.TLink.ITv2.Messages;
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
}
