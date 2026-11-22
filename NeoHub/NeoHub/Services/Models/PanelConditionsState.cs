namespace NeoHub.Services.Models
{
    /// <summary>
    /// What <see cref="NeoHub.Services.PartitionConditionsService"/> keeps per session to derive the partitions' trouble
    /// flags, trouble types, siren and alarm memory. Lives on <see cref="SessionState"/>, so it survives a takeover by a newer connection and
    /// goes with the state on a disconnect. Every member is guarded by <see cref="Gate"/>.
    /// </summary>
    public sealed class PanelConditionsState
    {
        internal object Gate { get; } = new();

        /// <summary>
        /// Monotonic counter (under <see cref="Gate"/>) that orders status read requests and notifications for the
        /// in-flight rules: a read request records the value it got, a notification the value it raised the counter to.
        /// </summary>
        internal long Sequence;

        /// <summary>Panel-wide set of active troubles from 0x0823: tuple of values (A, B, C) → label.</summary>
        internal Dictionary<(int A, int B, int C), string> ActiveTroubles { get; } = new();

        /// <summary>Sequence value of the last 0x0823 with state 01 (active), 0 if none.</summary>
        internal long LastTroubleActiveAt;

        /// <summary>Sequence value of the last change of <see cref="ActiveTroubles"/>, 0 if none.</summary>
        internal long LastTroubleSetChangeAt;

        /// <summary>
        /// The trouble flag of every partition was turned on by a 0x0823 with state 01 (panel-wide). Cleared by a status
        /// read that reports no troubles and is not older than the last state 01.
        /// </summary>
        internal bool TroublesForced;

        /// <summary>
        /// The connect pull of the current connection has read the partition status. Until then, 0x0823 updates the set
        /// of active troubles but does not change the trouble flags.
        /// </summary>
        internal bool ConnectPullDone;

        /// <summary>Per partition, also for a partition that has no <see cref="PartitionState"/> yet.</summary>
        internal Dictionary<byte, PartitionConditions> Partitions { get; } = new();
    }

    /// <summary>Per-partition part of <see cref="PanelConditionsState"/> (guarded by its gate).</summary>
    internal sealed class PartitionConditions
    {
        /// <summary>
        /// The trouble flag from this partition's own status reads, as before trouble details existed: the last read
        /// that counted reported troubles.
        /// </summary>
        public bool ReadTroubles;

        /// <summary>
        /// "unknown" in this partition's trouble types: its own read reported troubles that the set of active troubles
        /// does not explain. Only set together with <see cref="ReadTroubles"/>.
        /// </summary>
        public bool Unknown;

        /// <summary>The siren is on because the panel said so (Notification_Partition_Audible_Bell on).</summary>
        public bool SirenFromBell;

        /// <summary>The siren is on because a status read showed PartitionStatus2.AudibleBellSiren.</summary>
        public bool SirenFromBit;

        /// <summary>The disagreement of a read without the siren bit with <see cref="SirenFromBell"/> was logged.</summary>
        public bool SirenDisagreementLogged;

        /// <summary>Sequence value of the last audible bell "on", 0 if none.</summary>
        public long LastBellOnAt;

        /// <summary>Sequence value of the last siren clear (audible bell "off", or an arm or disarm), 0 if none.</summary>
        public long LastSirenClearAt;

        /// <summary>An alarm is in the partition's memory (PartitionStatus2.AlarmsInMemory, or Notification_Partition_Alarm_Memory).</summary>
        public bool AlarmMemory;

        /// <summary>Sequence value of the last alarm memory notification with a nonzero value, 0 if none.</summary>
        public long LastMemorySetAt;

        /// <summary>Sequence value of the last alarm memory notification with value zero, 0 if none.</summary>
        public long LastMemoryClearAt;
    }
}
