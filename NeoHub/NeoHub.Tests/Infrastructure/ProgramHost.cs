using System.Diagnostics;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using NeoHub.Services;
using NeoHub.Services.Settings;

namespace NeoHub.Tests.Infrastructure;

/// <summary>
/// The host that NeoHub's real Program.Main builds, with its persist folder in a temporary directory: Main is stopped
/// right after the host is built (the "HostBuilt" hosting event), so nothing listens or runs. Environment variables
/// are set only while the host is built and its settings services are created.
/// </summary>
internal sealed class ProgramHost : IDisposable, IObserver<DiagnosticListener>, IObserver<KeyValuePair<string, object?>>
{
    private sealed class Stop : Exception { }

    private readonly List<IDisposable> _subscriptions = new();
    private IHost? _host;

    public IHost Host => _host!;
    public IServiceProvider Services => Host.Services;
    public string PersistDir { get; private set; } = string.Empty;
    public string SettingsFile => Path.Combine(PersistDir, "userSettings.json");

    /// <param name="userSettingsJson">Content of userSettings.json, or null for no file.</param>
    /// <param name="environment">Extra environment variables while the host is built.</param>
    public static ProgramHost Build(string? userSettingsJson, IDictionary<string, string?>? environment = null)
    {
        var dir = Directory.CreateTempSubdirectory("neohub-program-test-").FullName;
        if (userSettingsJson is not null)
            File.WriteAllText(Path.Combine(dir, "userSettings.json"), userSettingsJson);

        var variables = new Dictionary<string, string?>(environment ?? new Dictionary<string, string?>())
        {
            ["NEOHUB_PERSIST_PATH"] = dir
        };
        var saved = variables.Keys.ToDictionary(k => k, Environment.GetEnvironmentVariable);
        var host = new ProgramHost { PersistDir = dir };
        try
        {
            foreach (var (name, value) in variables)
                Environment.SetEnvironmentVariable(name, value);
            using (DiagnosticListener.AllListeners.Subscribe(host))
            {
                try
                {
                    NeoHub.Program.Main(Array.Empty<string>());
                }
                catch (Stop)
                {
                }
            }
            if (host._host is null)
                throw new InvalidOperationException("Program.Main did not build a host");
            // Created while NEOHUB_PERSIST_PATH points at the test folder.
            host.Services.GetRequiredService<ISettingsPersistenceService>();
            host.Services.GetRequiredService<IFapCommandSettings>();
            return host;
        }
        catch
        {
            host.Dispose();
            throw;
        }
        finally
        {
            foreach (var (name, value) in saved)
                Environment.SetEnvironmentVariable(name, value);
            foreach (var subscription in host._subscriptions)
                subscription.Dispose();
            host._subscriptions.Clear();
        }
    }

    public void Dispose()
    {
        _host?.Dispose();
        try { Directory.Delete(PersistDir, recursive: true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
    }

    void IObserver<DiagnosticListener>.OnNext(DiagnosticListener value)
    {
        if (value.Name == "Microsoft.Extensions.Hosting")
            _subscriptions.Add(value.Subscribe(this));
    }

    void IObserver<KeyValuePair<string, object?>>.OnNext(KeyValuePair<string, object?> value)
    {
        if (value.Key == "HostBuilt" && _host is null)
        {
            _host = (IHost)value.Value!;
            throw new Stop();
        }
    }

    void IObserver<DiagnosticListener>.OnCompleted() { }
    void IObserver<DiagnosticListener>.OnError(Exception error) { }
    void IObserver<KeyValuePair<string, object?>>.OnCompleted() { }
    void IObserver<KeyValuePair<string, object?>>.OnError(Exception error) { }
}
