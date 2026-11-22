namespace NeoHub.Services.Models
{
    /// <summary>
    /// A keypad alarm reported by the panel (ModuleStatus_Miscellaneous_NonZone_Alarm_Status, state 01).
    /// </summary>
    /// <param name="Partition">The partition the panel named, also one NeoHub does not know (for example 0).</param>
    /// <param name="AlarmType">"fire" for alarm type 03; "unknown" for any other value (09 is not identified yet).</param>
    /// <param name="Code">The raw alarm type byte.</param>
    /// <param name="Time">When NeoHub received it (UTC, NeoHub's clock).</param>
    public sealed record KeypadAlarmRecord(int Partition, string AlarmType, int Code, DateTime Time);
}
