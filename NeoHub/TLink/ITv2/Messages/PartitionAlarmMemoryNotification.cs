using DSC.TLink.ITv2.Enumerations;
using DSC.TLink.Serialization;

namespace DSC.TLink.ITv2.Messages
{
    /// <summary>
    /// Notification_Partition_Alarm_Memory (0x023C): the partition's alarm memory was set or cleared.
    ///
    /// Layout <c>[CompactInt partition][value]</c> as LawPaul/HA_DSC_Neo_ITv2 (GPL-3.0) reads it, nonzero = an alarm is
    /// in memory. Only a set (value 01) has been captured on a PowerSeries Neo; a clear message was not seen.
    ///
    /// The payload is kept as received and parsed by <see cref="TryParse"/>.
    /// </summary>
    [ITv2Command(ITv2Command.Notification_Partition_Alarm_Memory)]
    public sealed record PartitionAlarmMemoryNotification : IMessageData
    {
        public byte[] Payload { get; init; } = Array.Empty<byte>();

        /// <summary>Parses <see cref="Payload"/>: exactly a CompactInt partition and one value byte. False otherwise.</summary>
        public bool TryParse(out PartitionAlarmMemory memory)
        {
            memory = default;
            int offset = 0;
            if (!CompactIntegerSerializer.TryReadInt32(Payload, ref offset, out var partition) || Payload.Length != offset + 1)
                return false;

            memory = new PartitionAlarmMemory(partition, Payload[offset] != 0, Payload[offset]);
            return true;
        }
    }

    /// <summary>A parsed <see cref="PartitionAlarmMemoryNotification"/>.</summary>
    public readonly record struct PartitionAlarmMemory(int Partition, bool InMemory, byte Value);
}
