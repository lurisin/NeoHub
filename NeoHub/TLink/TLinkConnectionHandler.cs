// DSC TLink - a communications library for DSC Powerseries NEO alarm panels
// Copyright (C) 2024 Brian Humlicek
//
// This program is free software: you can redistribute it and/or modify
// it under the terms of the GNU General Public License as published by
// the Free Software Foundation, either version 3 of the License, or
// (at your option) any later version.
//
// This program is distributed in the hope that it will be useful,
// but WITHOUT ANY WARRANTY; without even the implied warranty of
// MERCHANTABILITY or FITNESS FOR A PARTICULAR PURPOSE.  See the
// GNU General Public License for more details.
//
// You should have received a copy of the GNU General Public License
// along with this program.  If not, see <https://www.gnu.org/licenses/>.

using DSC.TLink.ITv2;
using DSC.TLink.ITv2.MediatR;
using Microsoft.AspNetCore.Connections;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace DSC.TLink
{
    internal class ITv2ConnectionHandler : ConnectionHandler
    {
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<ITv2ConnectionHandler> _log;

        internal static Dictionary<string, object> CreateLogScope(string sessionId) =>
            new() { [ConnectionSettings.LogScopeKey] = sessionId };

        public ITv2ConnectionHandler(
            IServiceProvider serviceProvider,
            ILogger<ITv2ConnectionHandler> log)
        {
            _serviceProvider = serviceProvider;
            _log = log;
        }

        public override async Task OnConnectedAsync(ConnectionContext connection)
        {
            _log.LogInformation("Connection request from {RemoteEndPoint}", connection.RemoteEndPoint);

            try
            {
                var settingsProvider = _serviceProvider.GetRequiredService<IConnectionSettingsProvider>();
                var loggerFactory = _serviceProvider.GetRequiredService<ILoggerFactory>();
                var sessionMediator = _serviceProvider.GetRequiredService<SessionMediator>();
                var sessionManager = _serviceProvider.GetRequiredService<IITv2SessionManager>();

                var result = await ITv2Session.CreateAsync(
                    connection.Transport, settingsProvider, loggerFactory, connection.ConnectionClosed);

                if (result.IsFailure)
                {
                    _log.LogError("Session failed to initialize: {Error}", result.Error);
                    return;
                }

                await using var session = result.Value;

                using var sessionScope = _log.BeginScope(
                    CreateLogScope(session.SessionId));

                await RunSessionAsync(session, sessionManager, sessionMediator, connection.ConnectionClosed, _log);
            }
            catch (OperationCanceledException) when (connection.ConnectionClosed.IsCancellationRequested)
            {
                _log.LogInformation("Connection cancelled");
            }
            catch (Exception ex)
            {
                _log.LogError(ex, "ITv2 connection error");
            }
            finally
            {
                _log.LogInformation("TLink disconnected from {RemoteEndPoint}", connection.RemoteEndPoint);
            }
        }

        /// <summary>
        /// Registers the session, publishes its notifications while it owns the registration, and unregisters it
        /// when its notifications end. A newer connection with the same session id can take the registration over
        /// (<see cref="IITv2SessionManager.RegisterSession"/>): the manager then closes this session, the loop ends,
        /// and the unregistration removes nothing. Notifications that arrive after this session lost the
        /// registration (takeover, or a disconnect from the UI) are dropped, so they cannot recreate state for a
        /// connection NeoHub no longer uses.
        /// </summary>
        internal static async Task RunSessionAsync(
            IITv2Session session,
            IITv2SessionManager sessionManager,
            SessionMediator sessionMediator,
            CancellationToken connectionClosed,
            ILogger log)
        {
            var sessionId = session.SessionId;
            sessionManager.RegisterSession(sessionId, session);
            try
            {
                await foreach (var message in session.GetNotificationsAsync(connectionClosed))
                {
                    if (!sessionManager.IsCurrentSession(sessionId, session))
                    {
                        log.LogDebug("Dropping {MessageType} from a connection that no longer owns the session",
                            message.GetType().Name);
                        continue;
                    }
                    await sessionMediator.PublishNotificationAsync(sessionId, message);
                }
            }
            catch (OperationCanceledException) when (!connectionClosed.IsCancellationRequested
                                                     && !sessionManager.IsCurrentSession(sessionId, session))
            {
                // The session was closed from our side (replaced by a newer connection, or disconnected in the UI).
                log.LogInformation("Connection closed after the session was replaced or disconnected");
            }
            finally
            {
                sessionManager.UnregisterSession(sessionId, session);
            }
        }
    }
}
