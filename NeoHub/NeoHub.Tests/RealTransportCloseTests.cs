using System.IO.Pipelines;
using System.Reflection;
using DSC.TLink;
using DSC.TLink.ITv2;
using Microsoft.Extensions.Logging;
using NeoHub.Tests.Infrastructure;

namespace NeoHub.Tests;

/// <summary>Closing a session that runs on the real TLinkTransport over a pipe, as Kestrel gives it one.</summary>
public class RealTransportCloseTests
{
    private sealed class DuplexPipe : IDuplexPipe
    {
        public DuplexPipe(PipeReader input, PipeWriter output) { Input = input; Output = output; }
        public PipeReader Input { get; }
        public PipeWriter Output { get; }
    }

    private static async Task<(ITv2Session Session, Task Pump, Pipe ToSession, Pipe FromSession, CaptureLoggerProvider Logs)> StartAsync()
    {
        var logs = new CaptureLoggerProvider();
        var factory = LoggerFactory.Create(b => b.AddProvider(logs).SetMinimumLevel(LogLevel.Trace));
        var toSession = new Pipe();
        var fromSession = new Pipe();
        var transport = new TLinkTransport(new DuplexPipe(toSession.Reader, fromSession.Writer), factory.CreateLogger<TLinkTransport>());
        var ctor = typeof(ITv2Session).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).Single();
        var session = (ITv2Session)ctor.Invoke(new object[] { transport, NullProxy.Create<IConnectionSettingsProvider>(), factory.CreateLogger<ITv2Session>() });
        var pump = (Task)typeof(ITv2Session).GetMethod("ReceivePumpAsync", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(session, null)!;
        var ready = (TaskCompletionSource)typeof(ITv2Session).GetField("_sessionReady", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(session)!;
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(10));
        return (session, pump, toSession, fromSession, logs);
    }

    private static async Task<bool> OutputCompletedAsync(Pipe fromSession)
    {
        // Drain what the session wrote until its side of the pipe is completed.
        while (true)
        {
            var read = await fromSession.Reader.ReadAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(5));
            fromSession.Reader.AdvanceTo(read.Buffer.End);
            if (read.IsCompleted)
                return true;
        }
    }

    [Fact]
    public async Task Abandon_ends_the_pump_completes_the_pipe_and_logs_no_error()
    {
        var (session, pump, _, fromSession, logs) = await StartAsync();

        await session.AbandonAsync();
        await session.DisposeAsync();

        await pump.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.True(pump.IsCompletedSuccessfully);
        Assert.True(await OutputCompletedAsync(fromSession));
        Assert.DoesNotContain(logs.Entries, e => e.Level >= LogLevel.Error);
        Assert.DoesNotContain(logs.Entries, e => e.Text == "END_SESSION sent");
    }

    [Fact]
    public async Task Dispose_after_the_panel_closed_the_pipe_ends_cleanly()
    {
        var (session, pump, toSession, fromSession, logs) = await StartAsync();

        await toSession.Writer.CompleteAsync();   // the panel closes the connection
        await pump.WaitAsync(TimeSpan.FromSeconds(5));
        await session.DisposeAsync();
        await session.DisposeAsync();

        Assert.True(pump.IsCompletedSuccessfully);
        Assert.True(await OutputCompletedAsync(fromSession));
        Assert.DoesNotContain(logs.Entries, e => e.Level >= LogLevel.Error);
    }
}
