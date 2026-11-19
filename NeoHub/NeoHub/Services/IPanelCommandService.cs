using DSC.TLink;
using DSC.TLink.ITv2.Enumerations;

namespace NeoHub.Services
{
    /// <summary>
    /// Service for sending commands to the alarm panel.
    /// Abstracts the TLink session layer from the UI and API.
    /// </summary>
    public interface IPanelCommandService
    {
        Task<PanelCommandResult> ArmAsync(string sessionId, byte partition, ArmingMode mode, string? accessCode = null);
        Task<PanelCommandResult> DisarmAsync(string sessionId, byte partition, string? accessCode = null);
        Task<PanelCommandResult> BypassZoneAsync(string sessionId, byte partition, byte zoneNumber, bool bypass, string? accessCode = null);

        /// <summary>
        /// True while an <see cref="ArmAsync"/>, <see cref="DisarmAsync"/> or <see cref="BypassZoneAsync"/> call
        /// for this session is running (from the call until it returns, including the wait for the panel).
        /// Background reads use it to stay out of the way of commands.
        /// </summary>
        bool IsCommandInProgress(string sessionId);

        /// <summary>
        /// Raised when <see cref="ArmAsync"/> was rejected by the panel with response code 1 (not ready to arm),
        /// after the command stopped counting as in progress and before ArmAsync returns. Handlers must not block.
        /// </summary>
        event EventHandler<ArmRejectedNotReadyEventArgs>? ArmRejectedNotReady;
    }

    public class ArmRejectedNotReadyEventArgs : EventArgs
    {
        public required string SessionId { get; init; }
        public required byte Partition { get; init; }
    }

    public record PanelCommandResult
    {
        public bool Success { get; init; }

        /// <summary>Infrastructure error code. Null when the panel itself rejected the command.</summary>
        public TLinkErrorCode? ErrorCode { get; init; }

        /// <summary>Human-readable error detail. Set for both infrastructure and panel-level failures.</summary>
        public string? ErrorMessage { get; init; }

        /// <summary>
        /// Numeric panel response code (TLink <c>CommandResponseCode</c>) when the panel itself rejected the command.
        /// Null on success and on infrastructure errors.
        /// </summary>
        public int? PanelResponseCode { get; init; }

        /// <summary>
        /// Short text for people when a known rejection means more than <see cref="ErrorMessage"/>
        /// (for example code 1 on an arm command). Null otherwise.
        /// </summary>
        public string? UserMessage { get; init; }

        /// <summary>
        /// Name of the ITv2 NACK code when the panel interface refused the command with a <c>Command_Error</c>.
        /// <see cref="Success"/> is still true then, as it always was, so existing callers are unaffected;
        /// the command was not carried out. Null otherwise.
        /// </summary>
        public string? PanelNack { get; init; }

        /// <summary>True when the panel did not answer in time; the panel may still have acted on the command.</summary>
        public bool TimedOut { get; init; }

        /// <summary>The text to show people: <see cref="UserMessage"/> when set, otherwise <see cref="ErrorMessage"/>.</summary>
        public string? DisplayMessage => UserMessage ?? ErrorMessage;

        public static PanelCommandResult Ok() => new() { Success = true };

        public static PanelCommandResult Error(TLinkErrorCode code, string message) =>
            new() { Success = false, ErrorCode = code, ErrorMessage = message };

        public static PanelCommandResult Error(string message) =>
            new() { Success = false, ErrorMessage = message };

        public static PanelCommandResult Rejected(int panelResponseCode, string message) =>
            new() { Success = false, PanelResponseCode = panelResponseCode, ErrorMessage = message };
    }
}
