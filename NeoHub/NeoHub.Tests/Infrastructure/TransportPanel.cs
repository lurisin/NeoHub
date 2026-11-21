using DSC.TLink.ITv2;
using DSC.TLink.ITv2.Messages;

namespace NeoHub.Tests.Infrastructure;

/// <summary>
/// Puts a <see cref="FakePanel"/> behind a <see cref="RealSession"/>'s transport: every request or command the session
/// sends is answered as the fake panel decides (data, CommandResponse, Command_Error, or no answer at all).
/// </summary>
internal static class TransportPanel
{
    public static void Attach(RealSession session, FakePanel panel)
    {
        session.Transport.OnSend = packet =>
        {
            var message = packet.Message;
            if (message is SimpleAck)
                return;
            _ = Task.Run(async () =>
            {
                if (message is not CommandRequestMessage and not ICommandMessage)
                {
                    session.Transport.Feed(packet.SenderSequence, new SimpleAck());
                    return;
                }
                if (panel.AckFirst.TryGetValue(message.GetType(), out var later))
                {
                    panel.Received.Enqueue(message);
                    session.Transport.Feed(packet.SenderSequence, new SimpleAck());
                    if (later is not TimeSpan delay)
                        return;
                    await Task.Delay(delay);
                    // A new inbound transaction: matched by command sequence, not by receiver sequence.
                    session.Transport.Feed(0, new CommandResponse
                    {
                        CommandSequence = ((CommandMessageBase)message).CommandSequence,
                        ResponseCode = DSC.TLink.ITv2.Enumerations.CommandResponseCode.Success
                    });
                    return;
                }
                var answer = await panel.HandleAsync(message, CancellationToken.None);
                if (answer.IsSuccess)
                    session.Transport.Feed(packet.SenderSequence, answer.Value);
            });
        };
    }
}
