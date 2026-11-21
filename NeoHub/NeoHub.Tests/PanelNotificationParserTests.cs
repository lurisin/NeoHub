using DSC.TLink.ITv2.Messages;

namespace NeoHub.Tests;

/// <summary>
/// The parsers of 0x0823, 0x0841, 0x023B and 0x023C, with payloads captured from a PowerSeries Neo panel (bytes only)
/// and malformed variants of them.
/// </summary>
public class PanelNotificationParserTests
{
    internal static byte[] Hex(string hex) => hex.Length == 0
        ? Array.Empty<byte>()
        : hex.Split('-').Select(b => Convert.ToByte(b, 16)).ToArray();

    /// <summary>Deserializes command word + payload the way the session does for a received message.</summary>
    private static IMessageData Decode(ushort command, string payload) =>
        MessageFactory.DeserializeMessage(new byte[] { (byte)(command >> 8), (byte)command }.Concat(Hex(payload)).ToArray());

    // ---- 0x0823 ModuleStatus_Trouble_Detail_Notification

    [Theory]
    [InlineData("01-00-01-09-01-00-01", "00_09_00", true)]
    [InlineData("01-00-01-09-01-00-00", "00_09_00", false)]
    [InlineData("01-12-01-81-01-01-01", "12_81_01", true)]
    [InlineData("01-12-01-81-01-01-00", "12_81_01", false)]
    [InlineData("01-00-01-87-01-00-01", "00_87_00", true)]
    [InlineData("01-00-01-87-01-00-00", "00_87_00", false)]
    [InlineData("01-00-01-8A-01-00-01", "00_8a_00", true)]      // a tuple never captured
    [InlineData("02-00-12-01-09-01-00-00", "0012_09_00", false)] // a two-byte CompactInt keeps both bytes
    [InlineData("01-00-02-00-09-01-00-01", "00_0009_00", true)]  // not the same tuple as 00_09_00
    public void Trouble_detail_parses_three_compact_ints_and_the_state(string payload, string key, bool active)
    {
        var message = Assert.IsType<TroubleDetailNotification>(Decode(0x0823, payload));
        Assert.Equal(Hex(payload), message.Payload);
        Assert.False(message.ReceivedInConnectBurst);

        Assert.True(message.TryParse(out var detail));
        Assert.Equal(key, detail.Key);
        Assert.Equal(active, detail.Active);
    }

    [Theory]
    [InlineData("01-00-01-09-01-00-01-00")]   // trailing byte
    [InlineData("01-00-01-09-01-00-02")]      // state neither 00 nor 01
    [InlineData("01-00-01-09-01-00-FF")]
    [InlineData("01-00-01-09-01-00")]         // no state byte
    [InlineData("01-00-01-09-01")]            // truncated in the third CompactInt
    [InlineData("01-00-01")]                  // length byte without its value
    [InlineData("01-00-00-01-00-01")]         // zero-length CompactInt
    [InlineData("00-01-09-01-00-01")]
    [InlineData("05-00-01-09-01")]            // length longer than the payload
    [InlineData("")]
    public void Malformed_trouble_detail_is_not_parsed(string payload)
    {
        // Still decoded as its own type (never DefaultMessage), so it is not an "undecoded notification".
        var message = Assert.IsType<TroubleDetailNotification>(Decode(0x0823, payload));
        Assert.False(message.TryParse(out _));
    }

    // ---- 0x0841 ModuleStatus_Miscellaneous_NonZone_Alarm_Status

    [Theory]
    [InlineData("01-01-03-01", 1, 0x03, true)]
    [InlineData("01-01-03-00", 1, 0x03, false)]
    [InlineData("01-01-09-01", 1, 0x09, true)]
    [InlineData("01-01-09-00", 1, 0x09, false)]
    [InlineData("01-00-03-01", 0, 0x03, true)]       // partition 0 is passed on as such
    [InlineData("02-01-00-09-01", 256, 0x09, true)]  // two-byte partition
    public void Keypad_alarm_parses_partition_type_and_state(string payload, int partition, byte type, bool active)
    {
        var message = Assert.IsType<NonZoneAlarmStatusNotification>(Decode(0x0841, payload));
        Assert.True(message.TryParse(out var status));
        Assert.Equal(new NonZoneAlarmStatus(partition, type, active), status);
    }

    [Theory]
    [InlineData("01-01-03-01-00")]   // trailing byte
    [InlineData("01-01-03-02")]      // bad state
    [InlineData("01-01-03")]         // no state
    [InlineData("01-01")]
    [InlineData("00-03-01")]         // zero-length CompactInt
    [InlineData("05-FF-FF-FF-FF-FF-03-01")] // partition does not fit an int
    [InlineData("")]
    public void Malformed_keypad_alarm_is_not_parsed(string payload)
    {
        var message = Assert.IsType<NonZoneAlarmStatusNotification>(Decode(0x0841, payload));
        Assert.False(message.TryParse(out _));
    }

    // ---- 0x023B Notification_Partition_Audible_Bell

    [Theory]
    [InlineData("01-01-01-02-02-00-F0", 1, true, "02-02-00-F0")]
    [InlineData("01-01-00-00-02-00-00", 1, false, "00-02-00-00")]
    [InlineData("01-01-01", 1, true, "")]            // nothing after the on/off byte
    [InlineData("01-02-05-00", 2, true, "00")]       // any nonzero byte is on
    public void Bell_uses_only_the_byte_after_the_partition(string payload, int partition, bool on, string rest)
    {
        var message = Assert.IsType<PartitionAudibleBellNotification>(Decode(0x023B, payload));
        Assert.True(message.TryParse(out var bell));
        Assert.Equal(partition, bell.Partition);
        Assert.Equal(on, bell.On);
        Assert.Equal(Hex(rest), bell.Rest);
    }

    [Theory]
    [InlineData("01-01")]     // no on/off byte
    [InlineData("01")]
    [InlineData("00-01-01")]  // zero-length CompactInt
    [InlineData("")]
    public void Malformed_bell_is_not_parsed(string payload)
    {
        var message = Assert.IsType<PartitionAudibleBellNotification>(Decode(0x023B, payload));
        Assert.False(message.TryParse(out _));
    }

    // ---- 0x023C Notification_Partition_Alarm_Memory

    [Theory]
    [InlineData("01-01-01", 1, true)]
    [InlineData("01-01-00", 1, false)]
    [InlineData("01-02-80", 2, true)]
    public void Alarm_memory_parses_partition_and_value(string payload, int partition, bool inMemory)
    {
        var message = Assert.IsType<PartitionAlarmMemoryNotification>(Decode(0x023C, payload));
        Assert.True(message.TryParse(out var memory));
        Assert.Equal(partition, memory.Partition);
        Assert.Equal(inMemory, memory.InMemory);
    }

    [Theory]
    [InlineData("01-01-01-00")]   // trailing byte
    [InlineData("01-01")]         // no value
    [InlineData("00-01")]         // zero-length CompactInt
    [InlineData("")]
    public void Malformed_alarm_memory_is_not_parsed(string payload)
    {
        var message = Assert.IsType<PartitionAlarmMemoryNotification>(Decode(0x023C, payload));
        Assert.False(message.TryParse(out _));
    }

    // ---- inside a MultipleMessagePacket

    [Fact]
    public void Sub_messages_of_a_multiple_message_packet_are_decoded()
    {
        // 0x0623, then each sub-message as [length][command word][payload]: 0x0841 01-01-09-01, 0x0841 01-01-09-00,
        // 0x0823 01-00-01-87-01-00-01, 0x023B 01-01-01-02-02-00-F0, 0x023C 01-01-01.
        var packet = Hex("06-23-06-08-41-01-01-09-01-06-08-41-01-01-09-00-09-08-23-01-00-01-87-01-00-01-09-02-3B-01-01-01-02-02-00-F0-05-02-3C-01-01-01");

        var message = Assert.IsType<MultipleMessagePacket>(MessageFactory.DeserializeMessage(packet));

        Assert.Collection(message.Messages,
            m => Assert.Equal(new NonZoneAlarmStatus(1, 0x09, true), Parse(Assert.IsType<NonZoneAlarmStatusNotification>(m))),
            m => Assert.Equal(new NonZoneAlarmStatus(1, 0x09, false), Parse(Assert.IsType<NonZoneAlarmStatusNotification>(m))),
            m =>
            {
                Assert.True(Assert.IsType<TroubleDetailNotification>(m).TryParse(out var detail));
                Assert.Equal(("00_87_00", true), (detail.Key, detail.Active));
            },
            m =>
            {
                Assert.True(Assert.IsType<PartitionAudibleBellNotification>(m).TryParse(out var bell));
                Assert.True(bell.On);
            },
            m =>
            {
                Assert.True(Assert.IsType<PartitionAlarmMemoryNotification>(m).TryParse(out var memory));
                Assert.True(memory.InMemory);
            });

        // The payloads serialize back to the same bytes.
        Assert.Equal(packet, MessageFactory.SerializeMessage(message).ToArray());

        static NonZoneAlarmStatus Parse(NonZoneAlarmStatusNotification n)
        {
            Assert.True(n.TryParse(out var s));
            return s;
        }
    }
}
