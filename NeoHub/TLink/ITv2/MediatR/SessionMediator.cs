using DSC.TLink.ITv2.Enumerations;
using DSC.TLink.ITv2.Messages;
using MediatR;
using Microsoft.Extensions.Logging;

namespace DSC.TLink.ITv2.MediatR
{
    /// <summary>
    /// Unified mediator that handles both outbound commands (from Blazor UI) 
    /// and publishes inbound notifications (from panel).
    /// Registered as singleton - uses SessionManager for routing.
    /// </summary>
    internal class SessionMediator : IRequestHandler<SessionCommand, SessionResponse>
    {
        private readonly IMediator _mediator;
        private readonly IITv2SessionManager _sessionManager;
        private readonly ILogger<SessionMediator> _logger;
        private readonly IFapCommandPolicy[] _fapPolicies;

        /// <summary>The text of a refused <see cref="ActivateFap"/>.</summary>
        public const string FapDisabledMessage = ActivateFap.DisabledMessage;

        public SessionMediator(
            IMediator mediator,
            IITv2SessionManager sessionManager,
            ILogger<SessionMediator> logger)
            : this(mediator, sessionManager, logger, Array.Empty<IFapCommandPolicy>())
        {
        }

        public SessionMediator(
            IMediator mediator,
            IITv2SessionManager sessionManager,
            ILogger<SessionMediator> logger,
            IEnumerable<IFapCommandPolicy> fapPolicies)
        {
            _mediator = mediator;
            _sessionManager = sessionManager;
            _logger = logger;
            _fapPolicies = fapPolicies.ToArray();
        }

        #region Command Handling (Outbound from Blazor UI)

        public async Task<SessionResponse> Handle(
            SessionCommand request,
            CancellationToken cancellationToken)
        {
            var failureLevel = request.LogFailuresAtDebug ? LogLevel.Debug : LogLevel.Warning;

            // Every command to a panel goes through here (web UI, WebSocket API, Dev Console, background reads),
            // so this is where a FAP command is stopped unless a policy allows it. Fails closed: no policy, no FAP.
            if (CarriesFap(request.MessageData) && !_fapPolicies.Any(p => p.AllowFapCommands))
            {
                _logger.LogWarning("Refused to send ModuleControl_Activate_FAP: {Reason}", FapDisabledMessage);
                return new SessionResponse
                {
                    Success = false,
                    ErrorMessage = FapDisabledMessage,
                    NotSent = true
                };
            }

            var session = _sessionManager.GetSession(request.SessionID);
            if (session == null)
            {
                _logger.Log(failureLevel, "Command failed - session not found");
                return new SessionResponse
                {
                    Success = false,
                    ErrorCode = TLinkErrorCode.SessionNotFound,
                    ErrorMessage = $"Session {request.SessionID} not found",
                    NotSent = true
                };
            }

            try
            {
                var result = await session.SendAsync(request.MessageData, cancellationToken);

                if (result.IsFailure)
                {
                    return new SessionResponse
                    {
                        Success = false,
                        ErrorCode = result.Error!.Value.Code,
                        ErrorMessage = result.Error.Value.Message
                    };
                }

                if (result.Value is CommandResponse { ResponseCode: not CommandResponseCode.Success } errorResponse)
                {
                    var description = DescribeResponseCode(errorResponse.ResponseCode);
                    _logger.Log(failureLevel, "Panel rejected command: [{Code}] {Description}",
                        errorResponse.ResponseCode, description);

                    return new SessionResponse
                    {
                        Success = false,
                        ErrorMessage = description,
                        PanelResponseCode = (int)errorResponse.ResponseCode,
                    };
                }

                if (result.Value is CommandError nack)
                {
                    _logger.Log(failureLevel, "Panel interface refused command {Command}: NACK {NackCode}",
                        nack.NackCommand, nack.NackCode);

                    // Still reported as success, as before, so existing callers behave the same;
                    // PanelNack lets callers that care detect it.
                    return new SessionResponse
                    {
                        Success = true,
                        MessageData = result.Value,
                        PanelNack = nack.NackCode.ToString(),
                        PanelNackCode = (int)nack.NackCode
                    };
                }

                return new SessionResponse
                {
                    Success = true,
                    MessageData = result.Value
                };
            }
            catch (OperationCanceledException ex) when (!cancellationToken.IsCancellationRequested)
            {
                // A cancellation the caller did not ask for: the session was closed while the command waited
                // (disconnect, or a newer connection took the session over). Expected, so no Error with a stack
                // trace; the message is unchanged. Cancelled, not Disconnected, which a failed transport send gives.
                _logger.Log(failureLevel, "Command ended: the session was closed while it waited for the panel");
                return new SessionResponse
                {
                    Success = false,
                    ErrorCode = TLinkErrorCode.Cancelled,
                    ErrorMessage = ex.Message
                };
            }
            catch (Exception ex)
            {
                _logger.Log(request.LogFailuresAtDebug ? LogLevel.Debug : LogLevel.Error, ex, "Error handling command");
                return new SessionResponse
                {
                    Success = false,
                    ErrorMessage = ex.Message
                };
            }
        }

        /// <summary>
        /// True when sending the message would put ModuleControl_Activate_FAP on the wire: an <see cref="ActivateFap"/>
        /// (the only type registered for that command word), also wrapped in a request or a multiple-message packet.
        /// </summary>
        internal static bool CarriesFap(IMessageData? message) => message switch
        {
            null => false,
            ActivateFap => true,
            CommandRequestMessage request => CarriesFap(request.Request),
            MultipleMessagePacket multiple => multiple.Messages.Any(CarriesFap),
            DefaultMessage undecoded => undecoded.Command == ITv2Command.ModuleControl_Activate_FAP,
            _ => false,
        };

        /// <summary>
        /// The TLink description of a response code. A code the enum does not define has no description, and
        /// Description() throws for it; that exception used to end the command, its message becoming the error text.
        /// The text stays the same, so replies without request_id do not change, but the code is now reported as a
        /// panel response code.
        /// </summary>
        private static string DescribeResponseCode(CommandResponseCode code)
        {
            try
            {
                return code.Description();
            }
            catch (InvalidOperationException ex)
            {
                return ex.Message;
            }
        }

        #endregion

        #region Notification Publishing (Inbound from Panel)

        /// <summary>
        /// Publishes an inbound message as a typed SessionNotification&lt;T&gt;.
        /// Called by the connection handler for each notification from the session.
        /// MultipleMessagePacket expansion is already handled by the session.
        /// </summary>
        public async Task PublishNotificationAsync(string sessionId, IMessageData message)
        {
            try
            {
                var messageType = message.GetType();
                var notificationType = typeof(SessionNotification<>).MakeGenericType(messageType);

                var notification = Activator.CreateInstance(
                    notificationType, sessionId, message, DateTime.UtcNow);

                if (notification == null)
                {
                    _logger.LogError("Failed to create notification for type {MessageType}", messageType.Name);
                    return;
                }

                await _mediator.Publish(notification);

                _logger.LogTrace("Published SessionNotification<{MessageType}>",
                    messageType.Name);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error publishing notification");
            }
        }

        #endregion
    }
}
