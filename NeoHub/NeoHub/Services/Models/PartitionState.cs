namespace NeoHub.Services.Models
{
    /// <summary>
    /// The trouble flag, trouble types, siren and alarm memory of a partition at one moment
    /// (<see cref="PartitionState.ConditionValues"/>).
    /// </summary>
    public sealed record PartitionConditionValues(
        bool TroublesPresent,
        IReadOnlyList<string> TroubleTypes,
        bool SirenActive,
        bool AlarmsInMemory)
    {
        public static readonly PartitionConditionValues None = new(false, Array.Empty<string>(), false, false);
    }

    /// <summary>
    /// State of a single partition (no longer contains zones).
    /// </summary>
    public class PartitionState
    {
        private PartitionConditionValues _conditionValues = PartitionConditionValues.None;

        public byte PartitionNumber { get; set; }
        public string? Name { get; set; }
        public string DisplayName => Name ?? $"Partition {PartitionNumber}";
        public bool IsReady { get; set; }

        /// <summary>
        /// The panel reports one or more troubles for this partition (ModulePartitionStatus,
        /// PartitionStatus2.TroublesPresent). Set by the status pull when the session connects and
        /// refreshed by <see cref="NeoHub.Services.PartitionTroubleRefreshService"/>, through
        /// <see cref="NeoHub.Services.PartitionConditionsService"/>, which also turns it on for every partition when
        /// the panel reports a trouble detail.
        /// </summary>
        public bool TroublesPresent
        {
            get => ConditionValues.TroublesPresent;
            set => ConditionValues = ConditionValues with { TroublesPresent = value };
        }

        /// <summary>
        /// The active trouble types while <see cref="TroublesPresent"/> is set, empty otherwise: the labels of the
        /// panel-wide set of active troubles (ModuleStatus_Trouble_Detail_Notification) in ordinal order, then
        /// "unknown" when a status read reported troubles the set does not explain; the same list for every partition.
        /// Set by <see cref="NeoHub.Services.PartitionConditionsService"/>; replaced, never changed in place.
        /// </summary>
        public IReadOnlyList<string> TroubleTypes
        {
            get => ConditionValues.TroubleTypes;
            set => ConditionValues = ConditionValues with { TroubleTypes = value };
        }

        /// <summary>
        /// The partition's siren (bell) is sounding: the panel reported it on (Notification_Partition_Audible_Bell),
        /// or a status read showed PartitionStatus2.AudibleBellSiren. Set by
        /// <see cref="NeoHub.Services.PartitionConditionsService"/>.
        /// </summary>
        public bool SirenActive
        {
            get => ConditionValues.SirenActive;
            set => ConditionValues = ConditionValues with { SirenActive = value };
        }

        /// <summary>
        /// An alarm is in the partition's memory (PartitionStatus2.AlarmsInMemory, or Notification_Partition_Alarm_Memory).
        /// Set by <see cref="NeoHub.Services.PartitionConditionsService"/>.
        /// </summary>
        public bool AlarmsInMemory
        {
            get => ConditionValues.AlarmsInMemory;
            set => ConditionValues = ConditionValues with { AlarmsInMemory = value };
        }

        /// <summary>
        /// <see cref="TroublesPresent"/>, <see cref="TroubleTypes"/>, <see cref="SirenActive"/> and
        /// <see cref="AlarmsInMemory"/> as one value, replaced in one assignment
        /// (<see cref="NeoHub.Services.PartitionConditionsService"/> sets all four at once). Code that reports more
        /// than one of them, on another thread than the one that changes them, reads this once, so it never mixes
        /// two moments (for example troubles off with the trouble types of before).
        /// </summary>
        public PartitionConditionValues ConditionValues
        {
            get => Volatile.Read(ref _conditionValues);
            set => Volatile.Write(ref _conditionValues, value ?? throw new ArgumentNullException(nameof(value)));
        }

        public PartitionStatus Status { get; set; } = PartitionStatus.Unknown;
        public DateTime LastUpdated { get; set; } = DateTime.UtcNow;

        // Exit delay tracking
        public bool ExitDelayActive { get; set; }
        public DateTime? ExitDelayStartedAt { get; set; }
        public int ExitDelayDurationSeconds { get; set; }
        public bool ExitDelayAudible { get; set; }
        public bool ExitDelayUrgent { get; set; }

        /// <summary>
        /// Whether the partition is in any armed state (including arming during exit delay).
        /// </summary>
        public bool IsArmed => Status is PartitionStatus.ArmedAway
                                      or PartitionStatus.ArmedHome
                                      or PartitionStatus.ArmedNight
                                      or PartitionStatus.Arming;

        /// <summary>
        /// The effective status, accounting for exit delay.
        /// During exit delay, this returns Arming regardless of the stored Status.
        /// </summary>
        public PartitionStatus EffectiveStatus =>
            ExitDelayActive && ExitDelayRemaining.HasValue && ExitDelayRemaining.Value > TimeSpan.Zero
                ? PartitionStatus.Arming
                : Status;

        /// <summary>
        /// Returns the remaining exit delay time, or null if no delay is active.
        /// </summary>
        public TimeSpan? ExitDelayRemaining
        {
            get
            {
                if (!ExitDelayActive || !ExitDelayStartedAt.HasValue)
                    return null;

                var elapsed = DateTime.UtcNow - ExitDelayStartedAt.Value;
                var remaining = TimeSpan.FromSeconds(ExitDelayDurationSeconds) - elapsed;
                return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
            }
        }
    }
}