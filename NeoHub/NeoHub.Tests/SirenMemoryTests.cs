using System.Text.Json;
using DSC.TLink.ITv2.Enumerations;
using DSC.TLink.ITv2.Messages;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NeoHub.Services;
using NeoHub.Tests.Infrastructure;
using static NeoHub.Tests.Infrastructure.ConditionsScenario;
using static NeoHub.Tests.PanelNotificationParserTests;

namespace NeoHub.Tests;

/// <summary>
/// Siren (Notification_Partition_Audible_Bell, 0x023B) and alarm memory (Notification_Partition_Alarm_Memory, 0x023C)
/// per partition, from the notifications, the status reads and arm / disarm notifications, with the payloads captured
/// from a PowerSeries Neo panel (bytes only). Each step checks the partition_update frames it sent: exactly one per
/// partition whose visible state changed, none otherwise.
/// </summary>
public class SirenMemoryTests
{
    private const string BellOn = "01-01-01-02-02-00-F0";
    private const string BellOff = "01-01-00-00-02-00-00";
    private const string MemorySet = "01-01-01";
    private const string MemoryClear = "01-01-00";

    private static List<string> InfoLogs(ConditionsScenario s, string startsWith) =>
        s.Host.Logs.Entries.Where(e => e.Level == LogLevel.Information && e.Text.StartsWith(startsWith)).Select(e => e.Text).ToList();

    [Fact]
    public async Task Captured_fire_alarm_then_disarm()
    {
        await using var s = await StartAsync();
        s.Pull(false, false);
        s.SirenMemoryUpdates();

        // Fire key: siren on, then the alarm memory.
        await s.Bell(BellOn);
        Assert.Equal(new[] { "p1 disarmed siren:on memory:off" }, s.SirenMemoryUpdates());
        await s.Memory(MemorySet);
        Assert.Equal(new[] { "p1 disarmed siren:on memory:on" }, s.SirenMemoryUpdates());
        await s.Bell(BellOn);   // no change, no frame
        await s.Memory(MemorySet);
        Assert.Empty(s.SirenMemoryUpdates());

        // Disarm with a user code: one frame, the siren already off in it.
        await s.ArmDisarm(1, ArmingMode.Disarm);
        Assert.Equal(new[] { "p1 disarmed siren:off memory:on" }, s.SirenMemoryUpdates());
        await s.Bell(BellOff);   // already off
        Assert.Empty(s.SirenMemoryUpdates());

        // The re-reads keep showing the memory; one without the bit clears it.
        s.ReadBits(1, MemoryBit);
        Assert.Empty(s.SirenMemoryUpdates());
        s.ReadBits(1, 0);
        Assert.Equal(new[] { "p1 disarmed siren:off memory:off" }, s.SirenMemoryUpdates());
        Assert.Equal("siren:off memory:off", s.SirenMemoryOf(2));

        Assert.Equal(new[] { "Audible bell of partition 1 on", "Audible bell of partition 1 on", "Audible bell of partition 1 off" },
            InfoLogs(s, "Audible bell"));
        Assert.Equal(new[] { "Siren cleared by arm/disarm on partition 1" }, InfoLogs(s, "Siren cleared"));
        Assert.Contains(s.Host.Logs.Entries, e => e.Level == LogLevel.Debug
            && e.Text == "Audible bell: partition 1, on/off byte 0x01, rest [02-02-00-F0]");
    }

    [Fact]
    public async Task A_read_may_turn_the_siren_on_but_never_off_while_the_bell_says_on()
    {
        await using var s = await StartAsync();
        s.Pull(false, false);
        s.SirenMemoryUpdates();

        await s.Bell(BellOn);
        s.SirenMemoryUpdates();
        s.ReadBits(1, 0);
        s.ReadBits(1, 0);
        Assert.Empty(s.SirenMemoryUpdates());
        Assert.Equal("siren:on memory:off", s.SirenMemoryOf(1));
        Assert.Single(InfoLogs(s, "Partition 1: the status read shows no siren"));   // logged once

        s.ReadBits(1, SirenBit);
        Assert.Empty(s.SirenMemoryUpdates());
        await s.Bell(BellOff);
        Assert.Equal(new[] { "p1 disarmed siren:off memory:off" }, s.SirenMemoryUpdates());

        // A siren only the bit turned on goes off with the next read without it.
        s.ReadBits(2, SirenBit);
        Assert.Equal(new[] { "p2 disarmed siren:on memory:off" }, s.SirenMemoryUpdates());
        s.ReadBits(2, SirenBit);
        Assert.Empty(s.SirenMemoryUpdates());
        s.ReadBits(2, 0);
        Assert.Equal(new[] { "p2 disarmed siren:off memory:off" }, s.SirenMemoryUpdates());
        Assert.Single(InfoLogs(s, "Partition 1: the status read shows no siren"));
    }

    [Fact]
    public async Task Any_arm_or_disarm_turns_the_siren_off_in_the_same_frame()
    {
        await using var s = await StartAsync();
        s.Pull(false, false);
        s.SirenMemoryUpdates();

        // A siren from the bell, whose "off" never arrives: the next arm clears it.
        await s.Bell(BellOn);
        s.SirenMemoryUpdates();
        await s.ArmDisarm(1, ArmingMode.StayArm);
        Assert.Equal(new[] { "p1 armed_home siren:off memory:off" }, s.SirenMemoryUpdates());

        // A siren from the bit only.
        s.ReadBits(2, SirenBit);
        s.SirenMemoryUpdates();
        await s.ArmDisarm(2, ArmingMode.AwayArm);
        Assert.Equal(new[] { "p2 armed_away siren:off memory:off" }, s.SirenMemoryUpdates());

        // Nothing to clear: the arm's frame only, and no log line.
        await s.ArmDisarm(1, ArmingMode.Disarm);
        Assert.Equal(new[] { "p1 disarmed siren:off memory:off" }, s.SirenMemoryUpdates());
        Assert.Equal(new[] { "Siren cleared by arm/disarm on partition 1", "Siren cleared by arm/disarm on partition 2" },
            InfoLogs(s, "Siren cleared"));

        // A read requested before the disarm and answered with the bit after it does not turn the siren back on.
        await s.Bell(BellOn);
        var before = s.Request();
        await s.ArmDisarm(1, ArmingMode.Disarm);
        s.SirenMemoryUpdates();
        s.AnswerBits(before, 1, SirenBit);
        Assert.Empty(s.SirenMemoryUpdates());
        Assert.Equal("siren:off memory:off", s.SirenMemoryOf(1));
    }

    [Fact]
    public async Task The_connect_pull_sets_the_siren_from_the_bit()
    {
        await using var s = await StartAsync();
        s.Pull(false, false);
        await s.Bell(BellOn);
        await s.Memory(MemorySet);
        s.SirenMemoryUpdates();

        // A new connection's pull: the bell's "on" from before is not kept without the bit.
        s.PullStarted();
        s.AnswerBits(s.Request(), 1, MemoryBit, StatusReadKind.ConnectPull);
        // A bell "on" that arrives after the pull's read was requested is kept.
        var p2 = s.Request();
        await s.Bell("01-02-01-02-02-00-F0");
        s.AnswerBits(p2, 2, 0, StatusReadKind.ConnectPull);
        s.PullDone();
        Assert.Equal("siren:off memory:on", s.SirenMemoryOf(1));
        Assert.Equal("siren:on memory:off", s.SirenMemoryOf(2));

        // The pull sets it from the bit as well.
        s.PullStarted();
        s.AnswerBits(s.Request(), 1, SirenBit, StatusReadKind.ConnectPull);
        s.AnswerBits(s.Request(), 2, 0, StatusReadKind.ConnectPull);
        s.PullDone();
        Assert.Equal("siren:on memory:off", s.SirenMemoryOf(1));
        Assert.Equal("siren:off memory:off", s.SirenMemoryOf(2));
        // That siren came from the bit only: a re-read without it turns it off.
        s.ReadBits(1, 0);
        Assert.Equal("siren:off memory:off", s.SirenMemoryOf(1));
    }

    [Fact]
    public async Task Alarm_memory_follows_the_notifications_and_the_reads_with_the_in_flight_rule()
    {
        await using var s = await StartAsync();
        s.Pull(false, false);
        s.SirenMemoryUpdates();

        // Set by a read; a read without the bit clears it.
        s.ReadBits(2, MemoryBit);
        Assert.Equal(new[] { "p2 disarmed siren:off memory:on" }, s.SirenMemoryUpdates());
        s.ReadBits(2, 0);
        Assert.Equal(new[] { "p2 disarmed siren:off memory:off" }, s.SirenMemoryUpdates());

        // A read requested before a nonzero 0x023C and answered without the bit after it is not used.
        var stale = s.Request();
        await s.Memory(MemorySet);
        Assert.Equal(new[] { "p1 disarmed siren:off memory:on" }, s.SirenMemoryUpdates());
        s.AnswerBits(stale, 1, 0);
        Assert.Empty(s.SirenMemoryUpdates());
        Assert.Equal("siren:off memory:on", s.SirenMemoryOf(1));
        s.ReadBits(1, 0);   // requested after it
        Assert.Equal(new[] { "p1 disarmed siren:off memory:off" }, s.SirenMemoryUpdates());

        // A zero 0x023C clears it; a read requested before that clear does not set it again.
        await s.Memory("01-01-80");   // any nonzero value
        s.SirenMemoryUpdates();
        var before = s.Request();
        await s.Memory(MemoryClear);
        Assert.Equal(new[] { "p1 disarmed siren:off memory:off" }, s.SirenMemoryUpdates());
        s.AnswerBits(before, 1, MemoryBit);
        Assert.Empty(s.SirenMemoryUpdates());

        // Troubles, siren and memory of one read change in one frame.
        s.ReadBits(1, MemoryBit | SirenBit | ModulePartitionStatus.PartitionStatus2.TroublesPresent);
        Assert.Equal(new[] { "p1 disarmed siren:on memory:on" }, s.SirenMemoryUpdates());
        Assert.Equal("on:unknown", s.StateOf(1));

        Assert.Equal(new[]
        {
            "Alarm memory set on partition 1 (value 0x01)",
            "Alarm memory set on partition 1 (value 0x80)",
            "Alarm memory cleared on partition 1 (value 0x00)",
        }, InfoLogs(s, "Alarm memory"));
    }

    [Fact]
    public async Task Notifications_of_unknown_partitions_and_malformed_ones_change_nothing()
    {
        await using var s = await StartAsync();
        s.Pull(false, false);
        s.SirenMemoryUpdates();

        await s.Bell("01-03-01-02-02-00-F0");   // partition 3 is not known
        await s.Bell("01-00-01");               // partition 0
        await s.Memory("01-03-01");
        await s.Bell("01-01");                  // malformed: no on/off byte
        await s.Bell("00-01-01");               // malformed: zero-length partition
        await s.Memory("01-01-01-00");          // malformed: trailing byte

        Assert.Empty(s.SirenMemoryUpdates());
        Assert.Equal(new byte[] { 1, 2 }, s.Session.Partitions.Keys.OrderBy(k => k));
        Assert.Equal(("siren:off memory:off", "siren:off memory:off"), (s.SirenMemoryOf(1), s.SirenMemoryOf(2)));
        Assert.Equal(3, InfoLogs(s, "Ignoring the ").Count);
        Assert.Equal(2, InfoLogs(s, "Ignoring a Notification_Partition_Audible_Bell that does not fit").Count);
        Assert.Contains(InfoLogs(s, "Ignoring a Notification_Partition_Alarm_Memory that does not fit"), t => t.EndsWith("[01-01-01-00]"));
    }

    [Fact]
    public async Task A_real_session_passes_bell_and_memory_on_and_full_state_lists_them()
    {
        await using var host = await TestHost.StartAsync(60);
        var panel = new FakePanel();
        panel.PartitionStatus[2] = (0x02, 0x10);   // partition 2: alarm in memory
        var real = await RealSession.StartAsync(Id, host.Services.GetRequiredService<ILoggerFactory>());
        TransportPanel.Attach(real, panel);
        var loop = host.Connect(real.Session);
        Assert.True(await TestHost.WaitUntil(() => host.Manager.IsCurrentSession(Id, real.Session)));
        await host.Manager.LifecycleIdleAsync(Id).WaitAsync(TimeSpan.FromSeconds(10));

        // Fire key, as captured: keypad alarm (active, restored), bell on and alarm memory in one packet.
        var n = host.Client.Count;
        real.Transport.Feed(0, new MultipleMessagePacket
        {
            Messages = new IMessageData[]
            {
                new NonZoneAlarmStatusNotification { Payload = Hex("01-01-03-01") },
                new NonZoneAlarmStatusNotification { Payload = Hex("01-01-03-00") },
                new PartitionAudibleBellNotification { Payload = Hex(BellOn) },
                new PartitionAlarmMemoryNotification { Payload = Hex(MemorySet) },
            }
        });
        Assert.True(await TestHost.WaitUntil(() => host.State.GetPartition(Id, 1)!.AlarmsInMemory));
        await Task.Delay(100);
        var frames = host.Client.FramesSince(n);
        Assert.Equal(3, frames.Count);
        Assert.Contains("\"type\":\"keypad_alarm\"", frames[0]);
        Assert.Contains("\"siren\":true,\"alarm_memory\":false", frames[1]);
        Assert.Contains("\"siren\":true,\"alarm_memory\":true", frames[2]);

        n = host.Client.Count;
        await host.FromClient("""{"type":"get_full_state"}""");
        var state = JsonDocument.Parse(host.Client.FramesSince(n).Single()).RootElement;
        var partitions = state.GetProperty("sessions")[0].GetProperty("partitions");
        Assert.Equal(
            """{"partition_number":1,"name":"Partition 1","status":"disarmed","ready":true,"troubles":false,"trouble_types":[],"siren":true,"alarm_memory":true}""",
            partitions[0].GetRawText());
        Assert.Equal(
            """{"partition_number":2,"name":"Partition 2","status":"disarmed","ready":true,"troubles":false,"trouble_types":[],"siren":false,"alarm_memory":true}""",
            partitions[1].GetRawText());
        Assert.Equal(TestHost.BaseCapabilities, state.GetProperty("capabilities").EnumerateArray().Select(c => c.GetString()));

        real.Transport.EndInbound();
        await loop.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
