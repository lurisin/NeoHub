using System.Text.Json;
using DSC.TLink.ITv2.MediatR;
using DSC.TLink.ITv2.Messages;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NeoHub.Tests.Infrastructure;
using static NeoHub.Tests.PanelNotificationParserTests;

namespace NeoHub.Tests;

/// <summary>
/// Keypad alarms (0x0841): a keypad_alarm message for each state 01, the last one in full_state, and no change to the
/// partition state. Payloads as captured from a PowerSeries Neo panel (bytes only).
/// </summary>
public class KeypadAlarmTests
{
    private const string Id = TestHost.SessionId;
    private static readonly DateTime T1 = new(2000, 1, 1, 0, 0, 1, 250, DateTimeKind.Utc);
    private static readonly DateTime T2 = new(2000, 1, 1, 0, 0, 2, 0, DateTimeKind.Utc);

    private static async Task<(TestHost Host, FakeSession Session)> ConnectAsync()
    {
        var host = await TestHost.StartAsync();
        var session = host.NewSession(new FakePanel(), "A");
        _ = host.Connect(session);
        Assert.True(await TestHost.WaitUntil(() => host.Manager.IsCurrentSession(Id, session)));
        await host.Manager.LifecycleIdleAsync(Id).WaitAsync(TimeSpan.FromSeconds(10));
        return (host, session);
    }

    private static Task Publish(TestHost host, string payload, DateTime receivedAt) =>
        host.Services.GetRequiredService<IMediator>().Publish(new SessionNotification<NonZoneAlarmStatusNotification>(
            Id, new NonZoneAlarmStatusNotification { Payload = Hex(payload) }, receivedAt));

    private static async Task<JsonElement> FullStateSession(TestHost host)
    {
        var n = host.Client.Count;
        await host.FromClient("""{"type":"get_full_state"}""");
        var frame = host.Client.FramesSince(n).Single();
        return JsonDocument.Parse(frame).RootElement.GetProperty("sessions")[0];
    }

    [Fact]
    public async Task Each_state_01_is_broadcast_and_kept_as_the_last_keypad_alarm()
    {
        var (host, _) = await ConnectAsync();
        await using var _h = host;
        Assert.Equal(JsonValueKind.Null, (await FullStateSession(host)).GetProperty("last_keypad_alarm").ValueKind);
        var partitionsBefore = (await FullStateSession(host)).GetProperty("partitions").GetRawText();

        var n = host.Client.Count;
        await Publish(host, "01-01-03-01", T1);   // fire, active
        await Publish(host, "01-01-03-00", T1);   // its restore: logged only
        await Publish(host, "01-01-09-01", T2);   // not identified
        await Publish(host, "01-01-09-00", T2);
        await Publish(host, "01-00-03-01", T2);   // partition 0, unknown to NeoHub: still broadcast
        Assert.Equal(new[]
        {
            """{"type":"keypad_alarm","session_id":"TESTSESSION","partition":1,"alarm_type":"fire","code":3,"time":"2000-01-01T00:00:01.250Z"}""",
            """{"type":"keypad_alarm","session_id":"TESTSESSION","partition":1,"alarm_type":"unknown","code":9,"time":"2000-01-01T00:00:02.000Z"}""",
            """{"type":"keypad_alarm","session_id":"TESTSESSION","partition":0,"alarm_type":"fire","code":3,"time":"2000-01-01T00:00:02.000Z"}""",
        }, host.Client.FramesSince(n));

        var session = await FullStateSession(host);
        Assert.Equal("""{"partition":0,"alarm_type":"fire","code":3,"time":"2000-01-01T00:00:02.000Z"}""",
            session.GetProperty("last_keypad_alarm").GetRawText());
        // The partitions are not changed by keypad alarms.
        Assert.Equal(partitionsBefore, session.GetProperty("partitions").GetRawText());

        var infos = host.Logs.Entries.Where(e => e.Level == LogLevel.Information && e.Text.StartsWith("Keypad alarm")).Select(e => e.Text).ToList();
        Assert.Equal(new[]
        {
            "Keypad alarm fire (code 3) on partition 1 active",
            "Keypad alarm fire (code 3) on partition 1 restored",
            "Keypad alarm unknown (code 9) on partition 1 active",
            "Keypad alarm unknown (code 9) on partition 1 restored",
            "Keypad alarm fire (code 3) on partition 0 active",
        }, infos);
    }

    [Fact]
    public async Task A_malformed_keypad_alarm_is_logged_with_its_hex_and_ignored()
    {
        var (host, _) = await ConnectAsync();
        await using var _h = host;
        var n = host.Client.Count;

        foreach (var payload in new[] { "01-01-03-02", "01-01-03-01-00", "01-01-03", "00-03-01", "00-FF-01" })
            await Publish(host, payload, T1);

        Assert.Empty(host.Client.FramesSince(n));
        Assert.Equal(JsonValueKind.Null, (await FullStateSession(host)).GetProperty("last_keypad_alarm").ValueKind);
        Assert.Equal(5, host.Logs.Entries.Count(e => e.Level == LogLevel.Information
            && e.Template.StartsWith("Ignoring a ModuleStatus_Miscellaneous_NonZone_Alarm_Status")));
        Assert.Contains(host.Logs.Entries, e => e.Text.EndsWith("[00-FF-01]"));
    }

    [Fact]
    public async Task The_last_keypad_alarm_survives_a_takeover_and_goes_with_a_disconnect()
    {
        var (host, a) = await ConnectAsync();
        await using var _h = host;
        a.Notify(new NonZoneAlarmStatusNotification { Payload = Hex("01-01-03-01") });
        Assert.True(await TestHost.WaitUntil(() => host.State.GetSession(Id)?.LastKeypadAlarm is not null));
        var kept = host.State.GetSession(Id)!.LastKeypadAlarm;

        var b = host.NewSession(new FakePanel(), "B");
        var loopB = host.Connect(b);
        Assert.True(await TestHost.WaitUntil(() => host.Manager.IsCurrentSession(Id, b)));
        await host.Manager.LifecycleIdleAsync(Id).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Same(kept, host.State.GetSession(Id)!.LastKeypadAlarm);
        Assert.Equal("fire", (await FullStateSession(host)).GetProperty("last_keypad_alarm").GetProperty("alarm_type").GetString());

        b.EndConnection();
        await loopB.WaitAsync(TimeSpan.FromSeconds(5));
        await host.Manager.LifecycleIdleAsync(Id).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Null(host.State.GetSession(Id));
    }

    [Fact]
    public async Task Keypad_alarms_in_a_multiple_message_packet_of_a_real_session()
    {
        await using var host = await TestHost.StartAsync();
        var real = await RealSession.StartAsync(Id, host.Services.GetRequiredService<ILoggerFactory>());
        TransportPanel.Attach(real, new FakePanel());
        var loop = host.Connect(real.Session);
        Assert.True(await TestHost.WaitUntil(() => host.Manager.IsCurrentSession(Id, real.Session)));
        await host.Manager.LifecycleIdleAsync(Id).WaitAsync(TimeSpan.FromSeconds(10));

        var n = host.Client.Count;
        real.Transport.Feed(0, new MultipleMessagePacket
        {
            Messages = new IMessageData[]
            {
                new NonZoneAlarmStatusNotification { Payload = Hex("01-01-09-01") },
                new NonZoneAlarmStatusNotification { Payload = Hex("01-01-09-00") },
            }
        });
        Assert.True(await TestHost.WaitUntil(() => host.Client.FramesSince(n).Any(f => f.Contains("\"keypad_alarm\""))));
        await Task.Delay(100);
        var frame = Assert.Single(host.Client.FramesSince(n));
        var root = JsonDocument.Parse(frame).RootElement;
        Assert.Equal(("unknown", 9, 1), (root.GetProperty("alarm_type").GetString(), root.GetProperty("code").GetInt32(), root.GetProperty("partition").GetInt32()));
        Assert.True(DateTime.Parse(root.GetProperty("time").GetString()!, null, System.Globalization.DateTimeStyles.RoundtripKind).Kind == DateTimeKind.Utc);

        real.Transport.EndInbound();
        await loop.WaitAsync(TimeSpan.FromSeconds(5));
    }
}
