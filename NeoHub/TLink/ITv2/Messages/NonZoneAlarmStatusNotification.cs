using DSC.TLink.ITv2.Enumerations;
using DSC.TLink.Serialization;

namespace DSC.TLink.ITv2.Messages
{
    /// <summary>
    /// ModuleStatus_Miscellaneous_NonZone_Alarm_Status (0x0841): an alarm that does not come from a zone, such as one
    /// raised with the Fire, Auxiliary or Panic keys of a keypad.
    ///
    /// INFERRED LAYOUT, not documented anywhere: <c>[CompactInt partition][alarm type][state]</c>, state 01 = active,
    /// 00 = restored. On a PowerSeries Neo the restore follows within milliseconds, so state 01 means "a keypad alarm
    /// happened", not a lasting state. Alarm type 03 is the fire alarm (it came with the siren and the alarm memory);
    /// the other values, 09 for example, are not identified.
    ///
    /// The payload is kept as received and parsed by <see cref="TryParse"/>.
    /// </summary>
    [ITv2Command(ITv2Command.ModuleStatus_Miscellaneous_NonZone_Alarm_Status)]
    public sealed record NonZoneAlarmStatusNotification : IMessageData
    {
        /// <summary>The alarm type byte of a fire alarm.</summary>
        public const byte FireAlarmType = 0x03;

        public byte[] Payload { get; init; } = Array.Empty<byte>();

        /// <summary>
        /// Parses <see cref="Payload"/>: exactly a CompactInt partition, an alarm type byte and a state byte of 00 or 01.
        /// False for anything else.
        /// </summary>
        public bool TryParse(out NonZoneAlarmStatus status)
        {
            status = default;
            int offset = 0;
            if (!CompactIntegerSerializer.TryReadInt32(Payload, ref offset, out var partition))
                return false;
            if (Payload.Length != offset + 2 || Payload[offset + 1] > 1)
                return false;

            status = new NonZoneAlarmStatus(partition, Payload[offset], Payload[offset + 1] == 1);
            return true;
        }
    }

    /// <summary>A parsed <see cref="NonZoneAlarmStatusNotification"/>.</summary>
    public readonly record struct NonZoneAlarmStatus(int Partition, byte AlarmType, bool Active);
}
