using DSC.TLink.ITv2;
using Microsoft.Extensions.Configuration.Json;
using Microsoft.Extensions.Primitives;
using NeoHub.Services.Settings;

namespace NeoHub.Services
{
    /// <summary>
    /// Whether FAP commands (Fire / Auxiliary / Panic, ModuleControl_Activate_FAP) may be sent:
    /// <see cref="ApplicationSettings.AllowFapCommands"/> as saved in the settings.
    /// </summary>
    public interface IFapCommandSettings : IFapCommandPolicy
    {
        /// <summary>Raised when the saved setting changes (after the settings file is reloaded).</summary>
        event Action? Changed;
    }

    /// <summary>
    /// Reads <see cref="ApplicationSettings.AllowFapCommands"/> from the configuration, so only the saved value counts:
    /// the configuration changes only when the settings file is written and reloaded, never while the Settings page is
    /// being edited. The SessionMediator gate, PanelCommandService, the WebSocket "fap" capability and its full_state
    /// push all read this one value. Missing or invalid means off.
    ///
    /// NeoHub's own wiring (<see cref="ForSettingsFile"/>) reads the key only from the saved settings file
    /// (userSettings.json): an environment variable, a command-line argument or appsettings.json cannot turn FAP
    /// commands on, and a value set there is logged at Warning as ignored.
    /// </summary>
    public sealed class FapCommandSettings : IFapCommandSettings, IDisposable
    {
        public const string ConfigurationKey = ApplicationSettings.SectionName + ":" + nameof(ApplicationSettings.AllowFapCommands);

        private readonly IConfiguration _configuration;
        private readonly ILogger<FapCommandSettings> _logger;
        private readonly string? _settingsFileName;
        private readonly IDisposable _reloadRegistration;
        private readonly object _lock = new();
        private bool _enabled;
        private string? _ignoredValueLogged;

        /// <summary>Reads the key from the whole <paramref name="configuration"/> (every source counts).</summary>
        public FapCommandSettings(IConfiguration configuration, ILogger<FapCommandSettings> logger)
            : this(configuration, logger, settingsFileName: null)
        {
        }

        /// <summary>
        /// With <paramref name="settingsFileName"/>, reads the key only from the JSON file provider of that file
        /// name (the last one, if several); without such a provider FAP commands stay off.
        /// </summary>
        public FapCommandSettings(IConfiguration configuration, ILogger<FapCommandSettings> logger, string? settingsFileName)
        {
            _configuration = configuration;
            _logger = logger;
            _settingsFileName = settingsFileName;
            _enabled = Read();
            if (_enabled)
                _logger.LogWarning("FAP commands are enabled in the NeoHub settings (experimental): clients can raise a real Fire, Auxiliary or Panic alarm");
            _reloadRegistration = ChangeToken.OnChange(_configuration.GetReloadToken, OnReload);
        }

        /// <summary>NeoHub's wiring: only the saved settings file (<paramref name="settingsFilePath"/>) counts.</summary>
        public static FapCommandSettings ForSettingsFile(IServiceProvider services, string settingsFilePath) =>
            new(services.GetRequiredService<IConfiguration>(),
                services.GetRequiredService<ILogger<FapCommandSettings>>(),
                Path.GetFileName(settingsFilePath));

        public bool AllowFapCommands
        {
            get { lock (_lock) return _enabled; }
        }

        public event Action? Changed;

        private bool Read()
        {
            var raw = _settingsFileName is null ? _configuration[ConfigurationKey] : ReadFromSettingsFile();
            if (string.IsNullOrEmpty(raw))
                return false;
            if (bool.TryParse(raw, out var value))
                return value;
            _logger.LogWarning("Invalid {Key} value in the settings; FAP commands stay disabled", ConfigurationKey);
            return false;
        }

        /// <summary>The key's value in the settings file provider, or null. Logs a value from another source as ignored.</summary>
        private string? ReadFromSettingsFile()
        {
            var provider = (_configuration as IConfigurationRoot)?.Providers
                .OfType<JsonConfigurationProvider>()
                .LastOrDefault(p => string.Equals(Path.GetFileName(p.Source.Path), _settingsFileName, StringComparison.OrdinalIgnoreCase));
            string? raw = null;
            if (provider is null)
                _logger.LogWarning("The settings file {File} is not part of the configuration; FAP commands stay disabled", _settingsFileName);
            else
                provider.TryGet(ConfigurationKey, out raw);

            // Another source (environment variable, command-line argument, appsettings.json) does not count.
            var effective = _configuration[ConfigurationKey];
            var ignored = !string.IsNullOrEmpty(effective) && !string.Equals(effective, raw, StringComparison.Ordinal) ? effective : null;
            if (ignored is not null && ignored != _ignoredValueLogged)
                _logger.LogWarning(
                    "{Key} is set to {Value} outside {File} (for example by an environment variable); ignored: only the value saved in the NeoHub settings counts",
                    ConfigurationKey, ignored, _settingsFileName);
            _ignoredValueLogged = ignored;
            return raw;
        }

        private void OnReload()
        {
            var now = Read();
            lock (_lock)
            {
                if (now == _enabled)
                    return;
                _enabled = now;
            }

            if (now)
                _logger.LogWarning("FAP commands enabled in the NeoHub settings (experimental): clients can raise a real Fire, Auxiliary or Panic alarm");
            else
                _logger.LogWarning("FAP commands disabled in the NeoHub settings");

            try
            {
                Changed?.Invoke();
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error in a FAP setting change handler");
            }
        }

        public void Dispose() => _reloadRegistration.Dispose();
    }
}
