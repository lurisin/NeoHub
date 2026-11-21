using DSC.TLink.ITv2.Enumerations;
using DSC.TLink.Serialization;

namespace DSC.TLink.ITv2.Messages
{
    /// <summary>
    /// ModuleStatus_Trouble_Detail_Notification (0x0823): the panel reports that a trouble became active or was restored.
    ///
    /// INFERRED LAYOUT, not documented anywhere: <c>[CompactInt A][CompactInt B][CompactInt C][state]</c>, state 01 =
    /// active, 00 = restored. It fits every payload captured on a PowerSeries Neo with a TL280-family communicator
    /// (seven bytes each, every CompactInt one byte long); a fixed seven-byte format fits them equally well, and a
    /// longer CompactInt was never seen. A plausible but unconfirmed reading is A = device or module type, B = trouble
    /// code, C = device number. The CompactInt encoding is the one NeoHub uses for partition numbers, also described
    /// by LawPaul/HA_DSC_Neo_ITv2 (GPL-3.0).
    ///
    /// The payload is kept as received and parsed by <see cref="TryParse"/>, so one that does not fit the layout is
    /// reported by the caller instead of being decoded by guesswork.
    /// </summary>
    [ITv2Command(ITv2Command.ModuleStatus_Trouble_Detail_Notification)]
    public sealed record TroubleDetailNotification : IMessageData
    {
        public byte[] Payload { get; init; } = Array.Empty<byte>();

        /// <summary>
        /// Set by the session when it passed this message on from the initial burst a panel sends right after it
        /// connects, which is otherwise discarded. The trouble it reports may be old.
        /// </summary>
        [IgnoreProperty]
        public bool ReceivedInConnectBurst { get; set; }

        /// <summary>
        /// Parses <see cref="Payload"/>: exactly three CompactInts of at least one byte and a state byte of 00 or 01.
        /// False for anything else (truncated, a zero-length CompactInt, trailing bytes, another state value).
        /// </summary>
        public bool TryParse(out TroubleDetail detail) => TroubleDetail.TryParse(Payload, out detail);
    }

    /// <summary>
    /// A parsed <see cref="TroubleDetailNotification"/>. A, B and C are the value bytes of the three CompactInts exactly
    /// as received; their meaning is not established.
    /// </summary>
    public sealed record TroubleDetail(byte[] A, byte[] B, byte[] C, bool Active)
    {
        /// <summary>
        /// The tuple as lower-case hex, two digits per value byte as received, joined with "_" (for example "00_09_00").
        /// A value sent with more bytes than needed is therefore a different tuple.
        /// </summary>
        public string Key => $"{Hex(A)}_{Hex(B)}_{Hex(C)}";

        public static bool TryParse(ReadOnlySpan<byte> payload, out TroubleDetail detail)
        {
            detail = null!;
            int offset = 0;
            if (!CompactIntegerSerializer.TryReadValueBytes(payload, ref offset, out var a)
                || !CompactIntegerSerializer.TryReadValueBytes(payload, ref offset, out var b)
                || !CompactIntegerSerializer.TryReadValueBytes(payload, ref offset, out var c))
                return false;
            if (payload.Length != offset + 1)
                return false;
            var state = payload[offset];
            if (state > 1)
                return false;

            detail = new TroubleDetail(a, b, c, state == 1);
            return true;
        }

        private static string Hex(byte[] value) => Convert.ToHexString(value).ToLowerInvariant();
    }
}
