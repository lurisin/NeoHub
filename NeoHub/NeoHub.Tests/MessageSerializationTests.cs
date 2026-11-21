using DSC.TLink.ITv2.Enumerations;
using DSC.TLink.ITv2.Messages;
using NeoHub.Api.WebSocket;

namespace NeoHub.Tests;

/// <summary>Checks of existing behaviour that the later tests build on.</summary>
public class MessageSerializationTests
{
    [Fact]
    public void PartitionArm_serializes_command_word_sequence_compact_partition_mode_and_bcd_code()
    {
        var message = new PartitionArm { CommandSequence = 0x21, Partition = 1, ArmMode = ArmingMode.StayArm, AccessCode = "1111" };

        var bytes = MessageFactory.SerializeMessage(message).ToArray();

        // 0x0900 ModuleControl_Partition_Arm_Control, sequence, CompactInt partition 1, mode, BCD code
        Assert.Equal(new byte[] { 0x09, 0x00, 0x21, 0x01, 0x01 }, bytes[..5]);
        Assert.Equal((byte)ArmingMode.StayArm, bytes[5]);
    }

    [Theory]
    [InlineData("""{"type":"disarm","code":"1111"}""", """{"type":"disarm","code":"***"}""")]
    [InlineData("""{"type":"disarm","code":null}""", """{"type":"disarm","code":null}""")]
    public void RedactAccessCodes_hides_code_values(string json, string expected)
    {
        Assert.Equal(expected, PanelWebSocketHandler.RedactAccessCodes(json));
    }
}
