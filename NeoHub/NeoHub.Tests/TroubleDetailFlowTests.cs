using DSC.TLink.ITv2.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NeoHub.Services;
using NeoHub.Tests.Infrastructure;
using static NeoHub.Tests.Infrastructure.ConditionsScenario;
using static NeoHub.Tests.PanelNotificationParserTests;

namespace NeoHub.Tests;

/// <summary>
/// Trouble details through a panel connection: the connection loop, the handler, the connect pull and the extra read
/// of <see cref="PartitionTroubleRefreshService"/> after a restore that leaves no active trouble detail.
/// </summary>
public class TroubleDetailFlowTests
{
    private static TroubleDetailNotification Detail(string payload) => new() { Payload = Hex(payload) };

    private static string StateOf(TestHost host, byte partition)
    {
        var p = host.State.GetPartition(Id, partition)!;
        return (p.TroublesPresent ? "on:" : "off:") + string.Join(",", p.TroubleTypes);
    }

    private static int StatusReads(FakePanel panel) =>
        panel.ReceivedOf<CommandRequestMessage>().Count(r => r.Request is ModulePartitionStatus);

    private static async Task<(TestHost Host, FakePanel Panel, FakeSession Session)> ConnectAsync(int interval)
    {
        var host = await TestHost.StartAsync(interval);
        var panel = new FakePanel();
        var session = host.NewSession(panel, "A");
        _ = host.Connect(session);
        Assert.True(await TestHost.WaitUntil(() => host.Manager.IsCurrentSession(Id, session)));
        await host.Manager.LifecycleIdleAsync(Id).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.True(await TestHost.WaitUntil(() => host.RefreshWorkers() == 1));
        return (host, panel, session);
    }

    [Fact]
    public async Task A_restore_that_leaves_no_active_trouble_reads_every_partition_once_more_after_the_delay()
    {
        var saved = PartitionTroubleRefreshService.ExtraReadDelay;
        PartitionTroubleRefreshService.ExtraReadDelay = TimeSpan.FromMilliseconds(400);
        try
        {
            var (host, panel, session) = await ConnectAsync(interval: 60);
            await using var _ = host;
            var pullReads = StatusReads(panel);

            session.Notify(Detail(TamperOn));
            Assert.True(await TestHost.WaitUntil(() => StateOf(host, 1) == "on:tamper" && StateOf(host, 2) == "on:tamper"));

            // Restore, then a second restore before the read is due: the read moves, and happens once.
            session.Notify(Detail(TamperOff));
            Assert.True(await TestHost.WaitUntil(() => StateOf(host, 1) == "on:"));
            await Task.Delay(150);
            session.Notify(Detail(AcOff));
            var restoredAt = DateTime.UtcNow;
            await Task.Delay(200);
            Assert.Equal(pullReads, StatusReads(panel));
            Assert.Equal("on:", StateOf(host, 2));   // never cleared by the restore itself

            Assert.True(await TestHost.WaitUntil(() => StateOf(host, 1) == "off:" && StateOf(host, 2) == "off:"));
            Assert.True(DateTime.UtcNow - restoredAt >= TimeSpan.FromMilliseconds(350));
            await Task.Delay(800);
            Assert.Equal(pullReads + 2, StatusReads(panel));
            Assert.DoesNotContain(host.Logs.Entries, e => e.Level >= LogLevel.Warning);
        }
        finally
        {
            PartitionTroubleRefreshService.ExtraReadDelay = saved;
        }
    }

    [Fact]
    public async Task The_extra_read_shows_what_is_still_active_as_unknown()
    {
        var saved = PartitionTroubleRefreshService.ExtraReadDelay;
        PartitionTroubleRefreshService.ExtraReadDelay = TimeSpan.FromMilliseconds(200);
        try
        {
            var (host, panel, session) = await ConnectAsync(interval: 60);
            await using var _ = host;

            session.Notify(Detail(AcOn));
            Assert.True(await TestHost.WaitUntil(() => StateOf(host, 1) == "on:ac_failure"));
            panel.PartitionStatus[1] = (0x02, 0x02);
            panel.PartitionStatus[2] = (0x02, 0x02);
            session.Notify(Detail(AcOff));

            Assert.True(await TestHost.WaitUntil(() => StateOf(host, 1) == "on:unknown" && StateOf(host, 2) == "on:unknown"));
        }
        finally
        {
            PartitionTroubleRefreshService.ExtraReadDelay = saved;
        }
    }

    [Fact]
    public async Task No_extra_read_with_the_refresh_off()
    {
        var saved = PartitionTroubleRefreshService.ExtraReadDelay;
        PartitionTroubleRefreshService.ExtraReadDelay = TimeSpan.FromMilliseconds(100);
        try
        {
            var (host, panel, session) = await ConnectAsync(interval: 0);
            await using var _ = host;
            var pullReads = StatusReads(panel);

            session.Notify(Detail(TamperOn));
            Assert.True(await TestHost.WaitUntil(() => StateOf(host, 1) == "on:tamper"));
            session.Notify(Detail(TamperOff));
            Assert.True(await TestHost.WaitUntil(() => StateOf(host, 1) == "off:" && StateOf(host, 2) == "off:"));
            await Task.Delay(600);
            Assert.Equal(pullReads, StatusReads(panel));
        }
        finally
        {
            PartitionTroubleRefreshService.ExtraReadDelay = saved;
        }
    }

    [Fact]
    public async Task A_real_session_passes_on_the_connect_burst_and_multiple_message_packets()
    {
        await using var host = await TestHost.StartAsync(60);
        var panel = new FakePanel();
        panel.PartitionStatus[1] = (0x02, 0x02);
        panel.PartitionStatus[2] = (0x02, 0x02);
        var real = await RealSession.StartAsync(Id, host.Services.GetRequiredService<ILoggerFactory>(), waitUntilReady: false);
        TransportPanel.Attach(real, panel);

        // In the connect burst: updates the set only; the connect pull reads troubles, and the set names them.
        real.Transport.Feed(0, Detail(AcOn));
        var loop = host.Connect(real.Session);
        Assert.True(await TestHost.WaitUntil(() => host.Manager.IsCurrentSession(Id, real.Session)));
        await real.WaitReadyAsync();
        await host.Manager.LifecycleIdleAsync(Id).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(("on:ac_failure", "on:ac_failure"), (StateOf(host, 1), StateOf(host, 2)));
        Assert.Contains(host.Logs.Entries, e => e.Text.StartsWith("Trouble ac_failure active (tuple 00_09_00) before the session was ready, in the connect burst"));

        // After the burst, two trouble details in one multiple-message packet, one frame per partition and change.
        var n = host.Client.Count;
        real.Transport.Feed(0, new MultipleMessagePacket { Messages = new IMessageData[] { Detail(TamperOn), Detail(AcOff) } });
        Assert.True(await TestHost.WaitUntil(() => StateOf(host, 1) == "on:tamper" && StateOf(host, 2) == "on:tamper"));
        await Task.Delay(100);
        var updates = host.Client.FramesSince(n).Where(f => f.Contains("\"partition_update\"")).ToList();
        Assert.Equal(4, updates.Count);
        Assert.Equal(2, updates.Count(f => f.Contains("\"trouble_types\":[\"ac_failure\",\"tamper\"]")));
        Assert.Equal(2, updates.Count(f => f.Contains("\"trouble_types\":[\"tamper\"]")));

        real.Transport.EndInbound();
        await loop.WaitAsync(TimeSpan.FromSeconds(5));
    }

    /// <summary>
    /// A restore that empties the set after the connect pull has read the partition status but before the pull has
    /// finished (zone status still outstanding), when no refresh worker exists yet: the extra read still happens once
    /// the refresh starts, not one interval later.
    /// </summary>
    [Theory]
    [InlineData(10)]
    [InlineData(0)]
    public async Task A_restore_before_the_refresh_has_started_still_gets_its_extra_read(int interval)
    {
        var saved = PartitionTroubleRefreshService.ExtraReadDelay;
        PartitionTroubleRefreshService.ExtraReadDelay = TimeSpan.FromMilliseconds(200);
        try
        {
            await using var host = await TestHost.StartAsync(interval);
            var panel = new FakePanel { HoldZoneStatus = new TaskCompletionSource() };
            var session = host.NewSession(panel, "A");
            var loop = host.Connect(session);
            Assert.True(await TestHost.WaitUntil(() => host.State.GetSession(Id)?.Conditions.ConnectPullDone == true, 10));
            Assert.Equal(0, host.RefreshWorkers());

            session.Notify(Detail(TamperOn));
            Assert.True(await TestHost.WaitUntil(() => StateOf(host, 1) == "on:tamper"));
            session.Notify(Detail(TamperOff));
            var expected = interval > 0 ? "on:" : "off:";   // with the refresh off the flag follows the set
            Assert.True(await TestHost.WaitUntil(() => StateOf(host, 1) == expected));
            var reads = StatusReads(panel);

            panel.HoldZoneStatus.SetResult();
            await host.Manager.LifecycleIdleAsync(Id).WaitAsync(TimeSpan.FromSeconds(10));
            Assert.True(await TestHost.WaitUntil(() => host.RefreshWorkers() == 1));

            if (interval > 0)
            {
                // Well before the first periodic round (10 s after the pull): both partitions read, flags off.
                Assert.True(await TestHost.WaitUntil(
                    () => StateOf(host, 1) == "off:" && StateOf(host, 2) == "off:" && StatusReads(panel) == reads + 2, 3));
                await Task.Delay(300);
                Assert.Equal(reads + 2, StatusReads(panel));
            }
            else
            {
                // Positive control: without the refresh there is no extra read.
                await Task.Delay(1500);
                Assert.Equal(reads, StatusReads(panel));
                Assert.Equal("off:", StateOf(host, 1));
            }

            session.EndConnection();
            await loop.WaitAsync(TimeSpan.FromSeconds(5));
        }
        finally
        {
            PartitionTroubleRefreshService.ExtraReadDelay = saved;
        }
    }
}
