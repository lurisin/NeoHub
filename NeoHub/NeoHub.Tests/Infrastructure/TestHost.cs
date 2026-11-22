using System.Collections.Concurrent;
using System.Net.WebSockets;
using System.Reflection;
using DSC.TLink;
using DSC.TLink.ITv2;
using DSC.TLink.ITv2.Enumerations;
using DSC.TLink.ITv2.MediatR;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using NeoHub.Api.WebSocket;
using NeoHub.Services;
using NeoHub.Services.PanelConfiguration;
using NeoHub.Services.Settings;

namespace NeoHub.Tests.Infrastructure;

/// <summary>
/// The real NeoHub and TLink services wired as in Program.cs (MediatR over both assemblies, session manager,
/// session mediator, state, command and WebSocket services, connect pull, lifecycle handler, trouble refresh),
/// with fake panel connections (<see cref="FakeSession"/>) and one recording WebSocket client.
/// </summary>
internal sealed class TestHost : IAsyncDisposable
{
    public const string SessionId = "TESTSESSION";

    /// <summary>The capabilities full_state lists, in order, while FAP commands are off ("fap" follows when on).</summary>
    public static readonly string[] BaseCapabilities = { "command_result", "partition_ready", "partition_troubles", "trouble_types", "keypad_alarms", "partition_siren_memory" };

    private static readonly MethodInfo ProcessMessage =
        typeof(PanelWebSocketHandler).GetMethod("ProcessMessageAsync", BindingFlags.NonPublic | BindingFlags.Instance)!;

    private readonly CancellationTokenSource _stop = new();
    private readonly ConcurrentBag<Task> _connections = new();

    private TestHost(ServiceProvider services, CaptureLoggerProvider logs)
    {
        Services = services;
        Logs = logs;
        Client = new CaptureSocket();
        var clients = (ConcurrentDictionary<WebSocket, byte>)typeof(PanelWebSocketHandler)
            .GetField("_connectedClients", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Ws)!;
        clients.TryAdd(Client, 0);
    }

    public ServiceProvider Services { get; }
    public CaptureLoggerProvider Logs { get; }
    public CaptureSocket Client { get; }
    public ConnectionSettings Connection { get; private set; } = null!;

    /// <summary>The configuration NeoHub reads its settings from (in memory here; the settings file in NeoHub).</summary>
    public IConfigurationRoot Configuration { get; private set; } = null!;

    public ITv2SessionManager Manager => (ITv2SessionManager)Services.GetRequiredService<IITv2SessionManager>();
    public PanelStateService State => (PanelStateService)Services.GetRequiredService<IPanelStateService>();
    public PanelCommandService Commands => (PanelCommandService)Services.GetRequiredService<IPanelCommandService>();
    public PanelWebSocketHandler Ws => Services.GetRequiredService<PanelWebSocketHandler>();
    public PartitionTroubleRefreshService Refresh => Services.GetRequiredService<PartitionTroubleRefreshService>();
    public SessionMediator SessionMediator => Services.GetRequiredService<SessionMediator>();

    public static async Task<TestHost> StartAsync(int troubleRefreshIntervalSeconds = 0, Action<IServiceCollection>? configure = null, bool allowFap = false)
    {
        var configuration = new ConfigurationBuilder().AddInMemoryCollection(new Dictionary<string, string?>
        {
            [FapCommandSettings.ConfigurationKey] = allowFap ? "true" : null,
        }).Build();
        var logs = new CaptureLoggerProvider();
        var connection = new ConnectionSettings
        {
            SessionId = SessionId,
            DefaultAccessCode = "1111",
            TroubleRefreshIntervalSeconds = troubleRefreshIntervalSeconds,
        };

        var services = new ServiceCollection();
        services.AddLogging(b => b.ClearProviders().AddProvider(logs).SetMinimumLevel(LogLevel.Trace));
        services.AddMediatR(cfg => cfg.RegisterServicesFromAssemblies(typeof(Program).Assembly, typeof(StartupExtensions).Assembly));
        services.Configure<PanelConnectionsSettings>(s => s.Connections.Add(connection));
        services.AddSingleton<IConfiguration>(configuration);
        services.Configure<ApplicationSettings>(configuration.GetSection(ApplicationSettings.SectionName));
        services.AddSingleton<FapCommandSettings>();
        services.AddSingleton<IFapCommandSettings>(sp => sp.GetRequiredService<FapCommandSettings>());
        services.AddSingleton<IFapCommandPolicy>(sp => sp.GetRequiredService<FapCommandSettings>());
        services.AddSingleton<IPanelStateService, PanelStateService>();
        services.AddSingleton<IPanelCommandService, PanelCommandService>();
        services.AddSingleton<ISessionMonitor, SessionMonitor>();
        services.AddSingleton<IConnectionSettingsProvider, NoConnectionSettings>();
        services.AddSingleton<PartitionConditionsService>();
        services.AddSingleton(NullProxy.Create<IPanelConfigurationService>());
        services.AddSingleton<IITv2SessionManager, ITv2SessionManager>();
        services.AddSingleton<SessionMediator>();
        services.AddSingleton<PanelWebSocketHandler>();
        services.AddSingleton<PartitionTroubleRefreshService>();
        configure?.Invoke(services);

        var provider = services.BuildServiceProvider();
        var host = new TestHost(provider, logs) { Connection = connection, Configuration = configuration };
        await host.Refresh.StartAsync(CancellationToken.None);
        return host;
    }

    public FakeSession NewSession(FakePanel panel, string name) => new(SessionId, panel, name);

    /// <summary>Saves AllowFapCommands, as a Save on the Settings page does (the settings file is reloaded).</summary>
    public void SaveAllowFap(bool on)
    {
        Configuration[FapCommandSettings.ConfigurationKey] = on ? "true" : "false";
        Configuration.Reload();
    }

    /// <summary>Runs one panel connection the way the TLink connection handler does, until its notifications end.</summary>
    public Task Connect(IITv2Session session)
    {
        var task = Task.Run(() => ConnectionLoop.RunAsync(this, session, _stop.Token));
        _connections.Add(task);
        return task;
    }

    /// <summary>Sends a client message through the WebSocket handler, as if the recording client had sent it.</summary>
    public Task FromClient(string json) => (Task)ProcessMessage.Invoke(Ws, new object[] { Client, json, "client1" })!;

    public bool HasSession => Manager.GetActiveSessions().Contains(SessionId);

    public int RefreshWorkers()
    {
        var workers = (System.Collections.IDictionary)typeof(PartitionTroubleRefreshService)
            .GetField("_workers", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Refresh)!;
        lock (typeof(PartitionTroubleRefreshService).GetField("_lock", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Refresh)!)
            return workers.Count;
    }

    public static async Task<bool> WaitUntil(Func<bool> condition, double seconds = 5)
    {
        var end = DateTime.UtcNow.AddSeconds(seconds);
        while (DateTime.UtcNow < end)
        {
            if (condition()) return true;
            await Task.Delay(10);
        }
        return condition();
    }

    public async ValueTask DisposeAsync()
    {
        _stop.Cancel();
        try { await Task.WhenAll(_connections).WaitAsync(TimeSpan.FromSeconds(5)); } catch { }
        await Refresh.StopAsync(CancellationToken.None);
        await Services.DisposeAsync();
    }

    private sealed class NoConnectionSettings : IConnectionSettingsProvider
    {
        public ConnectionSettings? ResolveConnection(string sessionId, EncryptionType encryptionType) => null;
        public void ConfirmDefaults(string sessionId) { }
    }
}

/// <summary>A DispatchProxy that answers every call with a default value (completed tasks for async members).</summary>
public class NullProxy : DispatchProxy
{
    public static T Create<T>() where T : class => DispatchProxy.Create<T, NullProxy>();

    protected override object? Invoke(MethodInfo? method, object?[]? args)
    {
        var type = method!.ReturnType;
        if (type == typeof(Task)) return Task.CompletedTask;
        if (type == typeof(ValueTask)) return ValueTask.CompletedTask;
        if (type == typeof(void)) return null;
        return type.IsValueType ? Activator.CreateInstance(type) : null;
    }
}
