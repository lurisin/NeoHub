namespace NeoHub.Services.Models
{
    /// <summary>
    /// State of a single partition (no longer contains zones).
    /// </summary>
    public class PartitionState
    {
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
        public bool TroublesPresent { get; set; }

        /// <summary>
        /// The active trouble types while <see cref="TroublesPresent"/> is set, empty otherwise: the labels of the
        /// panel-wide set of active troubles (ModuleStatus_Trouble_Detail_Notification) in ordinal order, then
        /// "unknown" when this partition's own status read reported troubles the set does not explain. Set by
        /// <see cref="NeoHub.Services.PartitionConditionsService"/>; replaced, never changed in place.
        /// </summary>
        public IReadOnlyList<string> TroubleTypes { get; set; } = Array.Empty<string>();

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