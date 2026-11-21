using System.Text.Json;
using DSC.TLink.ITv2.Enumerations;
using DSC.TLink.ITv2.MediatR;
using DSC.TLink.ITv2.Messages;
using MediatR;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using NeoHub.Services;
using NeoHub.Services.Settings;
using NeoHub.Tests.Infrastructure;

namespace NeoHub.Tests;

/// <summary>
/// activate_fap end to end, down to the bytes on a real ITv2Session's transport: nothing leaves while the setting is
/// off or the request is invalid, exactly one frame leaves for a valid request, and it is never sent again.
/// </summary>
public class FapCommandTests
{
    private const string Id = TestHost.SessionId;
    private const ushort FapCommandWord = 0x0908;
    private const string NoAnswer = "No answer from the panel: the alarm may still have been triggered";
    private const string Disabled = "FAP commands are disabled in the NeoHub settings";

    private sealed record Connected(TestHost Host, FakePanel Panel, RealSession Session, Task Loop) : IAsyncDisposable
    {
        public int FapFrames => Session.Transport.SentPayloads.Count(p => FakeTransport.CarriesCommandWord(p, FapCommandWord));

        public List<ActivateFap> FapMessages() =>
            Session.Transport.SentPayloads.Where(p => FakeTransport.CarriesCommandWord(p, FapCommandWord))
                .Select(p => (ActivateFap)FakeTransport.Decode(p).Message).ToList();

        public async ValueTask DisposeAsync()
        {
            Session.Transport.EndInbound();
            await Loop.WaitAsync(TimeSpan.FromSeconds(5));
            await Host.DisposeAsync();
        }
    }

    private static async Task<Connected> ConnectAsync(bool allowFap)
    {
        var host = await TestHost.StartAsync(allowFap: allowFap);
        var panel = new FakePanel();
        var session = await RealSession.StartAsync(Id, host.Services.GetRequiredService<ILoggerFactory>());
        TransportPanel.Attach(session, panel);
        var loop = host.Connect(session.Session);
        Assert.True(await TestHost.WaitUntil(() => host.Manager.IsCurrentSession(Id, session.Session)));
        await host.Manager.LifecycleIdleAsync(Id).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(2, host.State.GetPartitions(Id).Count);
        return new Connected(host, panel, session, loop);
    }

    /// <summary>An activate_fap message; each argument is raw JSON, or null to leave the field out.</summary>
    private static string Fap(string? requestId = "\"r1\"", string? sessionId = "\"" + Id + "\"", string? partition = "1", string? fapType = "\"fire\"")
    {
        var fields = new List<string> { "\"type\":\"activate_fap\"" };
        if (sessionId is not null) fields.Add($"\"session_id\":{sessionId}");
        if (partition is not null) fields.Add($"\"partition\":{partition}");
        if (fapType is not null) fields.Add($"\"fap_type\":{fapType}");
        if (requestId is not null) fields.Add($"\"request_id\":{requestId}");
        return "{" + string.Join(",", fields) + "}";
    }

    private static string Failed(string requestId, string message, int? code = null, string? kind = null) =>
        JsonSerializer.Serialize(new { type = "command_result", request_id = requestId, success = false, code, code_kind = kind, message });

    private static async Task<List<string>> Send(TestHost host, string json)
    {
        var n = host.Client.Count;
        await host.FromClient(json);
        return host.Client.FramesSince(n);
    }

    [Fact]
    public async Task With_the_setting_off_nothing_is_sent_by_the_websocket_the_command_service_or_a_generic_session_command()
    {
        await using var c = await ConnectAsync(allowFap: false);

        Assert.Equal(new[] { Failed("r1", Disabled) }, await Send(c.Host, Fap()));
        Assert.Equal(new[] { Failed("r2", Disabled) }, await Send(c.Host, Fap("\"r2\"", fapType: "\"Fire\"")));   // the setting is checked first
        Assert.Equal(new[] { """{"type":"error","message":"activate_fap requires request_id"}""" }, await Send(c.Host, Fap(requestId: null)));

        var direct = await c.Host.Commands.ActivateFapAsync(Id, 1, FapType.Panic);
        Assert.False(direct.Success);
        Assert.True(direct.NotSent);
        Assert.Equal(Disabled, direct.DisplayMessage);

        // What the Dev Console (or any other caller) would do: a SessionCommand through MediatR.
        var mediator = c.Host.Services.GetRequiredService<IMediator>();
        var generic = await mediator.Send(new SessionCommand { SessionID = Id, MessageData = new ActivateFap { Partition = 1, Type = FapType.Fire } });
        Assert.False(generic.Success);
        Assert.Equal(Disabled, generic.ErrorMessage);

        Assert.Equal(0, c.FapFrames);
        Assert.Contains(c.Host.Logs.Entries, e => e.Level == LogLevel.Warning && e.Text.Contains("client1") && e.Text.Contains(Disabled));
    }

    public static IEnumerable<object?[]> InvalidRequests() => new List<object?[]>
    {
        new object?[] { Fap(sessionId: null), "activate_fap requires session_id" },
        new object?[] { Fap(sessionId: "5"), "activate_fap requires session_id" },
        new object?[] { Fap(sessionId: "\"\""), "activate_fap requires session_id" },
        new object?[] { Fap(sessionId: "\"OTHER\""), "Session OTHER not found" },
        new object?[] { Fap(partition: null), "activate_fap requires partition: a partition number of 1 or more" },
        new object?[] { Fap(partition: "\"1\""), "activate_fap requires partition: a partition number of 1 or more" },
        new object?[] { Fap(partition: "1.0"), "activate_fap requires partition: a partition number of 1 or more" },
        new object?[] { Fap(partition: "1e0"), "activate_fap requires partition: a partition number of 1 or more" },
        new object?[] { Fap(partition: "0"), "activate_fap requires partition: a partition number of 1 or more" },
        new object?[] { Fap(partition: "-1"), "activate_fap requires partition: a partition number of 1 or more" },
        new object?[] { Fap(partition: "true"), "activate_fap requires partition: a partition number of 1 or more" },
        new object?[] { Fap(partition: "null"), "activate_fap requires partition: a partition number of 1 or more" },
        new object?[] { Fap(partition: "99999999999"), "activate_fap requires partition: a partition number of 1 or more" },
        new object?[] { Fap(partition: "3"), $"Partition 3 is not a known partition of session {Id}" },
        new object?[] { Fap(partition: "257"), $"Partition 257 is not a known partition of session {Id}" },
        new object?[] { Fap(fapType: null), "activate_fap requires fap_type: \"fire\", \"auxiliary\" or \"panic\"" },
        new object?[] { Fap(fapType: "\"Fire\""), "activate_fap requires fap_type: \"fire\", \"auxiliary\" or \"panic\"" },
        new object?[] { Fap(fapType: "\"FIRE\""), "activate_fap requires fap_type: \"fire\", \"auxiliary\" or \"panic\"" },
        new object?[] { Fap(fapType: "\"fir\""), "activate_fap requires fap_type: \"fire\", \"auxiliary\" or \"panic\"" },
        new object?[] { Fap(fapType: "\"panic \""), "activate_fap requires fap_type: \"fire\", \"auxiliary\" or \"panic\"" },
        new object?[] { Fap(fapType: "\"\""), "activate_fap requires fap_type: \"fire\", \"auxiliary\" or \"panic\"" },
        new object?[] { Fap(fapType: "0"), "activate_fap requires fap_type: \"fire\", \"auxiliary\" or \"panic\"" },
        new object?[] { Fap(fapType: "2"), "activate_fap requires fap_type: \"fire\", \"auxiliary\" or \"panic\"" },
        new object?[] { Fap(fapType: "null"), "activate_fap requires fap_type: \"fire\", \"auxiliary\" or \"panic\"" },
        new object?[] { Fap(fapType: "[\"fire\"]"), "activate_fap requires fap_type: \"fire\", \"auxiliary\" or \"panic\"" },
    };

    [Fact]
    public async Task Invalid_requests_are_refused_before_any_panel_traffic()
    {
        await using var c = await ConnectAsync(allowFap: true);
        foreach (var row in InvalidRequests())
        {
            var (json, message) = ((string)row[0]!, (string)row[1]!);
            Assert.Equal(new[] { Failed("r1", message) }, await Send(c.Host, json));
        }

        // Without a usable request_id: the "error" message and nothing else.
        foreach (var requestId in new string?[] { null, "5", "null", "{}" })
            Assert.Equal(new[] { """{"type":"error","message":"activate_fap requires request_id"}""" }, await Send(c.Host, Fap(requestId)));

        Assert.Equal(0, c.FapFrames);
        Assert.Equal(InvalidRequests().Count() + 4,
            c.Host.Logs.Entries.Count(e => e.Level == LogLevel.Warning && e.Template.StartsWith("Client {ClientId} → activate_fap") && e.Template.Contains("refused, nothing sent")));
    }

    [Theory]
    [InlineData("fire", FapType.Fire, 0x00)]
    [InlineData("auxiliary", FapType.Auxiliary, 0x01)]
    [InlineData("panic", FapType.Panic, 0x02)]
    public async Task A_valid_request_sends_exactly_one_frame_with_the_expected_bytes(string name, FapType type, byte typeByte)
    {
        await using var c = await ConnectAsync(allowFap: true);

        Assert.Equal(new[] { """{"type":"command_result","request_id":"r1","success":true,"code":null,"code_kind":null,"message":null}""" },
            await Send(c.Host, Fap(fapType: $"\"{name}\"")));

        var sent = Assert.Single(c.FapMessages());
        Assert.Equal((1, type), (sent.Partition, sent.Type));
        var frame = Assert.Single(c.Session.Transport.SentPayloads, p => FakeTransport.CarriesCommandWord(p, FapCommandWord));
        var packet = FakeTransport.Decode(frame);
        Assert.Equal(new byte[] { 0x09, 0x08, sent.CommandSequence, 0x01, 0x01, typeByte }, MessageFactory.SerializeMessage(packet.Message).ToArray());

        var warnings = c.Host.Logs.Entries.Where(e => e.Level == LogLevel.Warning && e.Text.Contains("activate_fap") && e.Text.Contains("client1")).ToList();
        Assert.Contains(warnings, e => e.Text.Contains($"Type={type}"));
        Assert.Contains(warnings, e => e.Text.Contains("result: accepted by the panel"));
    }

    [Fact]
    public async Task No_answer_is_reported_once_with_the_may_have_triggered_text_and_never_retried()
    {
        await using var c = await ConnectAsync(allowFap: true);
        c.Panel.NeverAnswer[typeof(ActivateFap)] = true;

        Assert.Equal(new[] { Failed("t", NoAnswer) }, await Send(c.Host, Fap("\"t\"")));
        await Task.Delay(1500);
        Assert.Equal(1, c.FapFrames);
        Assert.Contains(c.Host.Logs.Entries, e => e.Level == LogLevel.Warning && e.Text.Contains("client1") && e.Text.Contains(NoAnswer));
    }

    [Fact]
    public async Task Panel_rejections_and_nacks_use_the_rejection_texts_and_code_1_is_not_not_ready()
    {
        await using var c = await ConnectAsync(allowFap: true);
        var notReady = 0;
        c.Host.Commands.ArmRejectedNotReady += (_, _) => notReady++;

        c.Panel.ResponseCodes[typeof(ActivateFap)] = 1;
        Assert.Equal(new[] { Failed("a", "Panel rejected the command (code 1)", 1, "response") }, await Send(c.Host, Fap("\"a\"")));
        c.Panel.ResponseCodes[typeof(ActivateFap)] = 23;
        Assert.Equal(new[] { Failed("b", "Function not available", 23, "response") }, await Send(c.Host, Fap("\"b\"")));
        c.Panel.ResponseCodes.Clear();
        c.Panel.NackCodes[typeof(ActivateFap)] = 23;
        Assert.Equal(new[] { Failed("c", "Function not available", 23, "nack") }, await Send(c.Host, Fap("\"c\"")));

        Assert.Equal(0, notReady);
        Assert.Equal(3, c.FapFrames);   // one per request, none repeated
    }

    [Fact]
    public async Task A_takeover_while_waiting_reports_may_have_triggered_and_the_new_connection_sends_nothing()
    {
        await using var c = await ConnectAsync(allowFap: true);
        c.Panel.NeverAnswer[typeof(ActivateFap)] = true;

        var reply = Task.Run(() => Send(c.Host, Fap("\"k\"")));
        Assert.True(await TestHost.WaitUntil(() => c.FapFrames == 1));
        var b = await RealSession.StartAsync(Id, c.Host.Services.GetRequiredService<ILoggerFactory>());
        TransportPanel.Attach(b, c.Panel);
        var loopB = c.Host.Connect(b.Session);

        // The new connection's connect pull broadcasts state meanwhile; only the command_result matters here.
        var results = (await reply.WaitAsync(TimeSpan.FromSeconds(5))).Where(f => f.StartsWith("{\"type\":\"command_result\"")).ToList();
        Assert.Equal(new[] { Failed("k", NoAnswer) }, results);
        await c.Host.Manager.LifecycleIdleAsync(Id).WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal(1, c.FapFrames);
        Assert.DoesNotContain(c.Session.Transport.SentMessages(), m => m is ConnectionEndSession);   // replaced: no END_SESSION
        Assert.DoesNotContain(b.Transport.SentPayloads, p => FakeTransport.CarriesCommandWord(p, FapCommandWord));
        b.Transport.EndInbound();
        await loopB.WaitAsync(TimeSpan.FromSeconds(5));
    }

    [Fact]
    public async Task Only_a_transport_ack_and_then_silence_is_no_answer_and_sends_once()
    {
        await using var c = await ConnectAsync(allowFap: true);
        c.Panel.AckFirst[typeof(ActivateFap)] = null;

        Assert.Equal(new[] { Failed("s", NoAnswer) }, await Send(c.Host, Fap("\"s\"")));
        Assert.Equal(1, c.FapFrames);
    }

    [Fact]
    public async Task An_ack_then_a_command_response_in_a_later_packet_is_success()
    {
        await using var c = await ConnectAsync(allowFap: true);
        c.Panel.AckFirst[typeof(ActivateFap)] = TimeSpan.FromMilliseconds(300);

        Assert.Equal(new[] { """{"type":"command_result","request_id":"l","success":true,"code":null,"code_kind":null,"message":null}""" },
            await Send(c.Host, Fap("\"l\"")));
        Assert.Equal(1, c.FapFrames);
    }

    [Fact]
    public async Task Each_gate_holds_on_its_own()
    {
        // The TLink policy allows FAP, but a command service or WebSocket handler built without FAP settings refuses.
        await using var c = await ConnectAsync(allowFap: true);
        var services = c.Host.Services;
        var commands = new PanelCommandService(services.GetRequiredService<IMediator>(),
            services.GetRequiredService<ILogger<PanelCommandService>>(),
            services.GetRequiredService<IOptionsMonitor<PanelConnectionsSettings>>());
        var refused = await commands.ActivateFapAsync(Id, 1, FapType.Fire);
        Assert.Equal((false, true, Disabled), (refused.Success, refused.NotSent, refused.DisplayMessage));

        var handler = new NeoHub.Api.WebSocket.PanelWebSocketHandler(c.Host.State, c.Host.Commands, c.Host.Manager,
            services.GetRequiredService<ISessionMonitor>(), services.GetRequiredService<ILogger<NeoHub.Api.WebSocket.PanelWebSocketHandler>>());
        var socket = new CaptureSocket();
        await (Task)typeof(NeoHub.Api.WebSocket.PanelWebSocketHandler)
            .GetMethod("ProcessMessageAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance)!
            .Invoke(handler, new object[] { socket, Fap("\"g\""), "client2" })!;
        Assert.Equal(new[] { Failed("g", Disabled) }, socket.Frames());

        Assert.Equal(0, c.FapFrames);
    }

    private static string[] Capabilities(string fullState) =>
        JsonDocument.Parse(fullState).RootElement.GetProperty("capabilities").EnumerateArray().Select(e => e.GetString()!).ToArray();

    [Fact]
    public async Task The_fap_capability_follows_the_saved_setting_and_a_change_pushes_full_state()
    {
        await using var host = await TestHost.StartAsync();
        var full = (await Send(host, """{"type":"get_full_state"}""")).Single();
        Assert.Equal(TestHost.BaseCapabilities, Capabilities(full));

        // Moving the switch on the Settings page without saving changes nothing.
        host.Services.GetRequiredService<IOptionsMonitor<ApplicationSettings>>().CurrentValue.AllowFapCommands = true;
        Assert.DoesNotContain("fap", Capabilities((await Send(host, """{"type":"get_full_state"}""")).Single()));

        var n = host.Client.Count;
        host.SaveAllowFap(true);
        Assert.True(await TestHost.WaitUntil(() => host.Client.FramesSince(n).Any(f => f.Contains("\"full_state\""))));
        var pushed = host.Client.FramesSince(n).Single(f => f.Contains("\"full_state\""));
        Assert.Equal(TestHost.BaseCapabilities.Append("fap"), Capabilities(pushed));

        n = host.Client.Count;
        host.SaveAllowFap(false);
        Assert.True(await TestHost.WaitUntil(() => host.Client.FramesSince(n).Any(f => f.Contains("\"full_state\""))));
        Assert.DoesNotContain("fap", Capabilities(host.Client.FramesSince(n).Single(f => f.Contains("\"full_state\""))));

        n = host.Client.Count;
        host.SaveAllowFap(false);   // unchanged: no push
        await Task.Delay(200);
        Assert.Empty(host.Client.FramesSince(n));
    }
}
