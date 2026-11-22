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

using DSC.TLink.ITv2.Messages;

namespace DSC.TLink.ITv2;

/// <summary>
/// Represents a connected ITv2 session that can send commands and stream notifications.
/// </summary>
internal interface IITv2Session : IAsyncDisposable
{
    string SessionId { get; }

    /// <summary>
    /// Send a message and wait for the protocol+command transaction to complete.
    /// Returns the command response for command messages, or the original message for notifications.
    /// </summary>
    Task<Result<IMessageData>> SendAsync(IMessageData message, CancellationToken ct = default);

    /// <summary>
    /// Like <see cref="SendAsync"/>, but <paramref name="mayStillSend"/> is called right before the message is
    /// written (once the session is ready, with the send lock held). When it returns false nothing is sent and the
    /// result is a failure (<see cref="SendRefusedMessage"/>). Used for FAP commands, so that a policy change or a
    /// caller that gave up while the send waited is honoured. The default implementation, for test doubles, calls
    /// it once before <see cref="SendAsync"/>.
    /// </summary>
    Task<Result<IMessageData>> SendGuardedAsync(IMessageData message, Func<bool> mayStillSend, CancellationToken ct = default) =>
        mayStillSend()
            ? SendAsync(message, ct)
            : Task.FromResult(Result<IMessageData>.Fail(TLinkErrorCode.Cancelled, SendRefusedMessage));

    /// <summary>The failure text of a <see cref="SendGuardedAsync"/> whose condition refused the send.</summary>
    const string SendRefusedMessage = "Not sent: the send condition refused it";

    /// <summary>
    /// Yields inbound notifications after the session is connected.
    /// Command responses are routed internally and never appear here.
    /// MultipleMessagePackets are expanded — individual sub-messages are yielded.
    /// </summary>
    IAsyncEnumerable<IMessageData> GetNotificationsAsync(CancellationToken ct = default);

    /// <summary>
    /// Ends the session like <see cref="IAsyncDisposable.DisposeAsync"/>, but without sending END_SESSION.
    /// Used for a connection that a newer connection with the same session id has replaced: the panel has
    /// moved to the new connection, and an END_SESSION on the old one must not reach the panel's session.
    /// The notification stream ends, waiting sends fail, and the transport is closed. Later calls of either
    /// method do nothing.
    /// </summary>
    ValueTask AbandonAsync();
}
