using DSC.TLink.ITv2.Messages;
using MediatR;

namespace DSC.TLink.ITv2.MediatR
{
    /// <summary>
    /// Command to send a message to a specific panel session.
    /// Used by Blazor UI to interact with the panel.
    /// </summary>
    public record SessionCommand : IRequest<SessionResponse>
    {
        public required string SessionID { get; init; }
        public required IMessageData MessageData { get; init; }

        /// <summary>
        /// When true, <see cref="SessionMediator"/> logs a failed, rejected or refused command at Debug
        /// instead of Warning or Error. For background reads that handle and rate-limit their own failure logs.
        /// </summary>
        public bool LogFailuresAtDebug { get; init; }

        /// <summary>
        /// Optional: called right before the message is written to the panel (once the session is ready, with the
        /// send lock held). When it returns false the message is not sent and the response has
        /// <see cref="SessionResponse.NotSent"/> set. A caller that stops waiting can make it return false, so that
        /// the message cannot be written later; it is called at most once.
        /// </summary>
        public Func<bool>? MayStillSend { get; init; }
    }
}
