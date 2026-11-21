using DSC.TLink.ITv2.Messages;

namespace DSC.TLink.ITv2.MediatR;

/// <summary>
/// Response from a <see cref="SessionCommand"/>.
/// On success, <see cref="MessageData"/> holds the panel's response (e.g. a <see cref="CommandResponse"/>).
/// On failure, <see cref="ErrorCode"/> identifies the infrastructure error and <see cref="ErrorMessage"/> gives details.
/// When the panel itself rejected the command, <see cref="PanelResponseCode"/> holds its numeric response code.
/// When the panel interface answered with a NACK (<c>Command_Error</c>), <see cref="Success"/> stays true as before
/// and <see cref="PanelNack"/> names the NACK code.
/// </summary>
public record SessionResponse
{
    public bool Success { get; init; }
    public IMessageData? MessageData { get; init; }
    public TLinkErrorCode? ErrorCode { get; init; }
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// Numeric <c>CommandResponseCode</c> of a <see cref="CommandResponse"/> that rejected the command
    /// (panel-level rejection). Null on success and on infrastructure errors.
    /// </summary>
    public int? PanelResponseCode { get; init; }

    /// <summary>
    /// Name of the ITv2 NACK code (for example <c>SystemBusy</c>) when the panel interface answered the command
    /// with a <c>Command_Error</c> instead of a command response, so the command was not carried out.
    /// <see cref="Success"/> is still true in that case, as it always was; callers that need to know check this.
    /// Null otherwise.
    /// </summary>
    public string? PanelNack { get; init; }

    /// <summary>Numeric ITv2 NACK code of that <c>Command_Error</c> (see <see cref="PanelNack"/>). Null otherwise.</summary>
    public int? PanelNackCode { get; init; }

    /// <summary>
    /// True when the command was refused before anything was sent to the panel (session not found, or a FAP command
    /// while FAP commands are disabled). For other failures the command may have reached the panel.
    /// </summary>
    public bool NotSent { get; init; }
}
