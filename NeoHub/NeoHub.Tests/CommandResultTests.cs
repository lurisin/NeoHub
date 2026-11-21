using DSC.TLink.ITv2.Messages;
using NeoHub.Services;
using NeoHub.Tests.Infrastructure;

namespace NeoHub.Tests;

/// <summary>command_result "code", "code_kind" and the texts; the legacy replies without request_id stay as they were.</summary>
public class CommandResultTests
{
    private const string Id = TestHost.SessionId;

    private static string Result(string requestId, int? code, string? kind, string message) =>
        $$"""{"type":"command_result","request_id":"{{requestId}}","success":false,"code":{{(code is null ? "null" : code.ToString())}},"code_kind":{{(kind is null ? "null" : $"\"{kind}\"")}},"message":"{{message}}"}""";

    private static string Command(string type, string? requestId, string code = "1111", int partition = 1) =>
        $$"""{"type":"{{type}}","session_id":"{{Id}}","partition_number":{{partition}},"code":"{{code}}"{{(requestId is null ? "" : $",\"request_id\":\"{requestId}\"")}}}""";

    private static string Bypass(string? requestId) =>
        $$"""{"type":"bypass_zone","session_id":"{{Id}}","partition_number":1,"zone_number":2,"bypass":true,"code":"1111"{{(requestId is null ? "" : $",\"request_id\":\"{requestId}\"")}}}""";

    private static async Task<(TestHost Host, FakePanel Panel, FakeSession Session)> ConnectedAsync()
    {
        var host = await TestHost.StartAsync();
        var panel = new FakePanel();
        var session = host.NewSession(panel, "A");
        _ = host.Connect(session);
        await TestHost.WaitUntil(() => host.Manager.IsCurrentSession(Id, session));
        await host.Manager.LifecycleIdleAsync(Id);
        return (host, panel, session);
    }

    private static async Task<List<string>> Send(TestHost host, string json)
    {
        var n = host.Client.Count;
        await host.FromClient(json);
        return host.Client.FramesSince(n);
    }

    [Fact]
    public async Task Success_has_null_code_code_kind_and_message()
    {
        var (host, _, session) = await ConnectedAsync();
        await using var _h = host;
        Assert.Equal(new[] { """{"type":"command_result","request_id":"r","success":true,"code":null,"code_kind":null,"message":null}""" },
            await Send(host, Command("arm_away", "r")));
        session.EndConnection();
    }

    [Theory]
    [InlineData("arm_away")]
    [InlineData("arm_home")]
    [InlineData("arm_night")]
    [InlineData("arm_home_instant")]
    [InlineData("arm_away_instant")]
    public async Task Response_code_1_on_any_arm_command_is_not_ready(string type)
    {
        var (host, panel, session) = await ConnectedAsync();
        await using var _h = host;
        panel.ResponseCodes[typeof(PartitionArm)] = 1;
        var rejected = 0;
        host.Commands.ArmRejectedNotReady += (_, _) => rejected++;

        Assert.Equal(new[] { Result("r", 1, "response", "Partition not ready to arm") }, await Send(host, Command(type, "r")));
        Assert.Equal(1, rejected);
        session.EndConnection();
    }

    [Fact]
    public async Task Response_code_1_on_disarm_and_bypass_is_the_generic_text()
    {
        var (host, panel, session) = await ConnectedAsync();
        await using var _h = host;
        panel.ResponseCodes[typeof(PartitionDisarm)] = 1;
        panel.ResponseCodes[typeof(ConfigurationEnter)] = 1;

        Assert.Equal(new[] { Result("d", 1, "response", "Panel rejected the command (code 1)") }, await Send(host, Command("disarm", "d")));
        Assert.Equal(new[] { Result("b", 1, "response", "Panel rejected the command (code 1)") }, await Send(host, Bypass("b")));
        session.EndConnection();
    }

    [Fact]
    public async Task Other_response_codes_use_their_text_and_unknown_codes_the_number()
    {
        var (host, panel, session) = await ConnectedAsync();
        await using var _h = host;
        panel.ResponseCodes[typeof(PartitionArm)] = 17;
        panel.ResponseCodes[typeof(PartitionDisarm)] = 99;

        Assert.Equal(new[] { Result("a", 17, "response", "Invalid access code") }, await Send(host, Command("arm_home", "a")));
        Assert.Equal(new[] { Result("d", 99, "response", "Panel rejected the command (code 99)") }, await Send(host, Command("disarm", "d")));

        // Without request_id the error text for an undefined code is what it was before (Description() fails for it).
        Assert.Equal(new[] { """{"type":"error","message":"Sequence contains no elements"}""" }, await Send(host, Command("disarm", null)));
        session.EndConnection();
    }

    [Fact]
    public async Task A_nack_reports_its_code_as_nack_and_nack_1_is_not_not_ready()
    {
        var (host, panel, session) = await ConnectedAsync();
        await using var _h = host;
        panel.NackCodes[typeof(PartitionArm)] = 1;
        panel.NackCodes[typeof(PartitionDisarm)] = 9;
        var rejected = 0;
        host.Commands.ArmRejectedNotReady += (_, _) => rejected++;

        Assert.Equal(new[] { Result("a", 1, "nack", "Invalid command length") }, await Send(host, Command("arm_home", "a")));
        Assert.Equal(new[] { Result("d", 9, "nack", "Keypad locked out") }, await Send(host, Command("disarm", "d")));
        Assert.Equal(0, rejected);

        panel.NackCodes[typeof(PartitionDisarm)] = 77;
        Assert.Equal(new[] { Result("u", 77, "nack", "Panel refused the command (NACK 77)") }, await Send(host, Command("disarm", "u")));
        session.EndConnection();
    }

    [Fact]
    public async Task Failures_without_a_panel_code_have_null_code_and_code_kind()
    {
        await using var host = await TestHost.StartAsync();
        Assert.Equal(new[] { Result("n", null, null, $"Session {Id} not found") }, await Send(host, Command("arm_home", "n")));
    }

    [Fact]
    public async Task No_answer_in_time_has_null_code_and_code_kind()
    {
        var (host, panel, session) = await ConnectedAsync();
        await using var _h = host;
        panel.NeverAnswer[typeof(PartitionDisarm)] = true;

        Assert.Equal(new[] { Result("t", null, null, "No confirmation from the panel within 8 s; check the partition state") },
            await Send(host, Command("disarm", "t")));
        session.EndConnection();
    }

    [Fact]
    public async Task Replies_without_request_id_keep_the_tlink_description_and_a_nack_still_sends_nothing()
    {
        var (host, panel, session) = await ConnectedAsync();
        await using var _h = host;
        panel.ResponseCodes[typeof(PartitionArm)] = 17;
        Assert.Equal(new[] { """{"type":"error","message":"Invalid Access Code"}""" }, await Send(host, Command("arm_home", null)));

        panel.ResponseCodes[typeof(PartitionArm)] = 1;
        var code1 = await Send(host, Command("arm_home", null));
        Assert.Equal(new[] { """{"type":"error","message":"Cannot Exit Configuration/Ignore Firmware (No Firmware Available)/ Block out of range/ Command Output is not defined/System cannot be put in the specified test mode/ Unsupported Buffer ID/ Enabled Late To Open/ Invalid Type"}""" }, code1);

        panel.NackCodes[typeof(PartitionDisarm)] = 9;
        Assert.Empty(await Send(host, Command("disarm", null)));
        session.EndConnection();
    }

    [Fact]
    public async Task The_web_ui_result_reads_the_new_texts_and_treats_a_nack_as_not_accepted()
    {
        var (host, panel, session) = await ConnectedAsync();
        await using var _h = host;
        panel.NackCodes[typeof(PartitionDisarm)] = 19;
        panel.ResponseCodes[typeof(PartitionArm)] = 1;

        var disarm = await host.Commands.DisarmAsync(Id, 1, "1111");
        var arm = await host.Commands.ArmAsync(Id, 1, DSC.TLink.ITv2.Enumerations.ArmingMode.AwayArm);

        Assert.True(disarm.Success);           // unchanged for existing callers
        Assert.False(disarm.Accepted);
        Assert.Equal("Panel busy", disarm.DisplayMessage);
        Assert.False(arm.Accepted);
        Assert.Equal("Partition not ready to arm", arm.DisplayMessage);
        session.EndConnection();
    }
}
