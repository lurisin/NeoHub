using DSC.TLink.ITv2.Enumerations;
using DSC.TLink.Serialization;

namespace DSC.TLink.ITv2.Messages
{
    /// <summary>
    /// Notification_Partition_Audible_Bell (0x023B): the partition's siren (bell) started or stopped sounding.
    ///
    /// LawPaul/HA_DSC_Neo_ITv2 (GPL-3.0) documents <c>[CompactInt partition][flags]</c> without meanings. Only the
    /// first byte after the partition is used here, nonzero = sounding; it was 01 while a fire alarm sounded and 00
    /// after the disarm. What follows it (<see cref="AudibleBell.Rest"/>; seen as 02-02-00-F0 and 00-02-00-00) is
    /// not interpreted and not length-checked.
    ///
    /// The payload is kept as received and parsed by <see cref="TryParse"/>.
    /// </summary>
    [ITv2Command(ITv2Command.Notification_Partition_Audible_Bell)]
    public sealed record PartitionAudibleBellNotification : IMessageData
    {
        public byte[] Payload { get; init; } = Array.Empty<byte>();

        /// <summary>
        /// Parses <see cref="Payload"/>: a CompactInt partition and at least the on/off byte. False when either is
        /// missing or malformed.
        /// </summary>
        public bool TryParse(out AudibleBell bell)
        {
            bell = null!;
            int offset = 0;
            if (!CompactIntegerSerializer.TryReadInt32(Payload, ref offset, out var partition) || offset >= Payload.Length)
                return false;

            bell = new AudibleBell(partition, Payload[offset] != 0, Payload[offset], Payload[(offset + 1)..]);
            return true;
        }
    }

    /// <summary>A parsed <see cref="PartitionAudibleBellNotification"/>.</summary>
    public sealed record AudibleBell(int Partition, bool On, byte OnOffByte, byte[] Rest);
}
