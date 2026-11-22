using DSC.TLink.ITv2.Enumerations;
using DSC.TLink.Serialization;

namespace DSC.TLink.ITv2.Messages
{
    /// <summary>
    /// ModuleStatus_Trouble_Detail_Notification (0x0823): the panel reports that a trouble became active or was restored.
    ///
    /// INFERRED LAYOUT, not documented anywhere: one or more records <c>[CompactInt A][CompactInt B][CompactInt C][state]</c>,
    /// state 01 = active, 00 = restored, 02 = in memory. Every payload captured on a PowerSeries Neo with a TL280-family
    /// communicator was a single seven-byte record, every CompactInt one byte long; a fixed seven-byte format fits them
    /// equally well, and a longer CompactInt was never seen. Several independent parsers of other panels' ITv2 traffic
    /// read this message as repeated records, and one of them names state 02 "trouble in memory"; neither was seen
    /// here. A plausible but unconfirmed reading is A = device or module type, B = trouble code, C = device number. The
    /// CompactInt encoding is the one NeoHub uses for partition numbers, also described by LawPaul/HA_DSC_Neo_ITv2
    /// (GPL-3.0).
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
        /// Parses <see cref="Payload"/> as one or more whole records, in order. False, and no record, when anything does
        /// not parse as whole records: an empty payload, a partial record at the end, a CompactInt of zero or more than
        /// four bytes, a value that does not fit an int, or a state other than 00, 01 and 02 in any record.
        /// </summary>
        public bool TryParse(out IReadOnlyList<TroubleDetail> details) => TroubleDetail.TryParseAll(Payload, out details);
    }

    /// <summary>The state byte of a <see cref="TroubleDetail"/>.</summary>
    public enum TroubleDetailState : byte
    {
        Restored = 0x00,
        Active = 0x01,
        /// <summary>"Trouble in memory" in another vendor's code for this message; never seen on a PowerSeries Neo.</summary>
        InMemory = 0x02,
    }

    /// <summary>
    /// One record of a <see cref="TroubleDetailNotification"/>. A, B and C are the values of the three CompactInts; RawA,
    /// RawB and RawC their value bytes exactly as received. Their meaning is not established.
    /// </summary>
    public sealed record TroubleDetail(int A, int B, int C, byte[] RawA, byte[] RawB, byte[] RawC, TroubleDetailState State)
    {
        public bool Active => State == TroubleDetailState.Active;

        /// <summary>The tuple of values, which identifies the trouble.</summary>
        public (int A, int B, int C) Tuple => (A, B, C);

        /// <summary>
        /// The value bytes as lower-case hex, two digits per byte as received, joined with "_" (for example "00_09_00";
        /// a B sent as two bytes 00 09 gives "00_0009_00").
        /// </summary>
        public string RawKey => $"{Hex(RawA)}_{Hex(RawB)}_{Hex(RawC)}";

        /// <summary>Parses one or more whole records; see <see cref="TroubleDetailNotification.TryParse"/>.</summary>
        public static bool TryParseAll(ReadOnlySpan<byte> payload, out IReadOnlyList<TroubleDetail> details)
        {
            details = Array.Empty<TroubleDetail>();
            var records = new List<TroubleDetail>();
            int offset = 0;
            do
            {
                if (!TryParseRecord(payload, ref offset, out var record))
                    return false;
                records.Add(record);
            }
            while (offset < payload.Length);

            details = records;
            return true;
        }

        private static bool TryParseRecord(ReadOnlySpan<byte> payload, ref int offset, out TroubleDetail detail)
        {
            detail = null!;
            if (!CompactIntegerSerializer.TryReadInt32(payload, ref offset, out var a, out var rawA)
                || !CompactIntegerSerializer.TryReadInt32(payload, ref offset, out var b, out var rawB)
                || !CompactIntegerSerializer.TryReadInt32(payload, ref offset, out var c, out var rawC))
                return false;
            if (offset >= payload.Length)
                return false;
            var state = payload[offset];
            if (state > (byte)TroubleDetailState.InMemory)
                return false;
            offset++;

            detail = new TroubleDetail(a, b, c, rawA, rawB, rawC, (TroubleDetailState)state);
            return true;
        }

        private static string Hex(byte[] value) => Convert.ToHexString(value).ToLowerInvariant();
    }
}
