using DSC.TLink;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace NeoHub.Tests.Infrastructure;

internal static class ConnectionLoop
{
    /// <summary>
    /// What ITv2ConnectionHandler.OnConnectedAsync does after the handshake: the session loop, then the
    /// "await using" disposal of the session.
    /// </summary>
    public static async Task RunAsync(TestHost host, DSC.TLink.ITv2.IITv2Session session, CancellationToken connectionClosed)
    {
        try
        {
            var log = host.Services.GetRequiredService<ILoggerFactory>().CreateLogger("DSC.TLink.ITv2ConnectionHandler");
            await ITv2ConnectionHandler.RunSessionAsync(session, host.Manager, host.SessionMediator, connectionClosed, log);
        }
        finally
        {
            await session.DisposeAsync();
        }
    }
}
