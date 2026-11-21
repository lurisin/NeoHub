using DSC.TLink.ITv2.Enumerations;

namespace DSC.TLink.ITv2;

/// <summary>
/// Short English texts for people when the panel did not carry out a command: one map for the codes of a
/// <c>Command_Response</c> (<see cref="CommandResponseCode"/>) and a separate one for the NACK codes of a
/// <c>Command_Error</c> (<see cref="ITv2NackCode"/>). The two numberings overlap with different meanings (response 1
/// is CannotExitConfiguration, NACK 1 is InvalidCommandLength), so a code is only meaningful with its kind.
/// Each map has one entry per member of its enum; a value outside the enum gets a generic text with the number.
/// </summary>
public static class PanelRejectionText
{
    /// <summary>Response code 1 to an arm command (any arming mode). DSC's description of code 1 is generic.</summary>
    public const string NotReadyToArm = "Partition not ready to arm";

    internal static readonly IReadOnlyDictionary<CommandResponseCode, string> ResponseTexts = new Dictionary<CommandResponseCode, string>
    {
        [CommandResponseCode.Success] = "Success",
        // Code 1 has many meanings (DSC: "Cannot Exit Configuration / ... / Invalid Type"); only for an arm command
        // does NeoHub read it as "not ready" (ForResponseCode).
        [CommandResponseCode.CannotExitConfiguration] = "Panel rejected the command (code 1)",
        [CommandResponseCode.InvalidProgrammingType] = "Invalid type or value (code 2)",
        [CommandResponseCode.UnsupportedModule] = "Unsupported module (code 3)",
        [CommandResponseCode.InvalidSignalType] = "Invalid signal type, or a partition failed to arm (code 4)",
        [CommandResponseCode.NotInCorrectProgrammingMode] = "Not in the correct programming mode",
        [CommandResponseCode.InvalidAccessCode] = "Invalid access code",
        [CommandResponseCode.AccessCodeRequired] = "Access code required",
        [CommandResponseCode.SystemPartitionBusy] = "Panel busy",
        [CommandResponseCode.InvalidPartition] = "Invalid partition",
        [CommandResponseCode.FunctionNotAvailable] = "Function not available",
        [CommandResponseCode.InternalError] = "Panel internal error",
        [CommandResponseCode.CommandTimeOut] = "Panel command timed out",
        [CommandResponseCode.NoTroublesPresentForRequestedType] = "No troubles of the requested type",
        [CommandResponseCode.NoRequestedAlarmsFound] = "No requested alarms found",
        [CommandResponseCode.InvalidDeviceModule] = "Invalid device or module",
        [CommandResponseCode.InvalidTroubleType] = "Invalid trouble type",
    };

    internal static readonly IReadOnlyDictionary<ITv2NackCode, string> NackTexts = new Dictionary<ITv2NackCode, string>
    {
        [ITv2NackCode.UnknownError] = "Unknown error",
        [ITv2NackCode.InvalidCommandLength] = "Invalid command length",
        [ITv2NackCode.InvalidCommand] = "Invalid command",
        [ITv2NackCode.InvalidSequence] = "Invalid sequence",
        [ITv2NackCode.PanelNotResponding] = "Panel not responding",
        [ITv2NackCode.InvalidPassthruCommand] = "Invalid pass-through command",
        [ITv2NackCode.InvalidDestination] = "Invalid destination",
        [ITv2NackCode.InvalidSession] = "Invalid session",
        [ITv2NackCode.InsufficientBufferSize] = "Insufficient buffer size",
        [ITv2NackCode.LockOut] = "Keypad locked out",
        [ITv2NackCode.UnsupportedSubCommand] = "Unsupported sub-command",
        [ITv2NackCode.PowerupShunt] = "Panel is in its power-up delay",
        [ITv2NackCode.InvalidProgrammingMode] = "Invalid programming mode",
        [ITv2NackCode.InvalidAccessCode] = "Invalid access code",
        [ITv2NackCode.AccessCodeRequired] = "Access code required",
        [ITv2NackCode.SystemBusy] = "Panel busy",
        [ITv2NackCode.InvalidPartition] = "Invalid partition",
        [ITv2NackCode.FunctionNotAvailable] = "Function not available",
        [ITv2NackCode.InternalError] = "Panel internal error",
        [ITv2NackCode.CommandTimeout] = "Panel command timed out",
        [ITv2NackCode.InvalidDeviceModule] = "Invalid device or module",
        [ITv2NackCode.InvalidTroubleType] = "Invalid trouble type",
        [ITv2NackCode.ProgTagOrAccessCodeRequired] = "Programming tag or access code required",
        [ITv2NackCode.ProgTagRequired] = "Programming tag required",
    };

    /// <summary>
    /// Text for a <c>Command_Response</c> code that rejected a command. Code 1 reads "Partition not ready to arm" only
    /// when <paramref name="armCommand"/> is true (an arm command in any arming mode).
    /// </summary>
    public static string ForResponseCode(int code, bool armCommand)
    {
        if (armCommand && code == (int)CommandResponseCode.CannotExitConfiguration)
            return NotReadyToArm;
        return code is >= byte.MinValue and <= byte.MaxValue
               && ResponseTexts.TryGetValue((CommandResponseCode)code, out var text)
            ? text
            : $"Panel rejected the command (code {code})";
    }

    /// <summary>Text for the NACK code of a <c>Command_Error</c>. NACK 1 is never "not ready".</summary>
    public static string ForNackCode(int code) =>
        code is >= byte.MinValue and <= byte.MaxValue && NackTexts.TryGetValue((ITv2NackCode)code, out var text)
            ? text
            : $"Panel refused the command (NACK {code})";
}
