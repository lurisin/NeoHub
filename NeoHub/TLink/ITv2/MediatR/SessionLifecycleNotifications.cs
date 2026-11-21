using MediatR;

namespace DSC.TLink.ITv2.MediatR
{
    /// <summary>A panel connection registered a session id, which starts the connect-time pull.</summary>
    public record SessionConnectedNotification(string SessionId) : INotification
    {
        /// <summary>
        /// True when this connection replaced a still registered connection with the same session id (the panel
        /// reconnected before the old connection was torn down). The session's state is kept; the pull refreshes it.
        /// </summary>
        public bool ReplacedConnection { get; init; }
    }

    public record SessionDisconnectedNotification(string SessionId) : INotification;
}