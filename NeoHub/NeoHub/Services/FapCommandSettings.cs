using DSC.TLink.ITv2;
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
    /// the Settings page edits the live options object as soon as the switch moves, before Save, but the configuration
    /// changes only when the settings file is written and reloaded. The SessionMediator gate, PanelCommandService, the
    /// WebSocket "fap" capability and its full_state push all read this one value. Missing or invalid means off.
    /// </summary>
    public sealed class FapCommandSettings : IFapCommandSettings, IDisposable
    {
        public const string ConfigurationKey = ApplicationSettings.SectionName + ":" + nameof(ApplicationSettings.AllowFapCommands);

        private readonly IConfiguration _configuration;
        private readonly ILogger<FapCommandSettings> _logger;
        private readonly IDisposable _reloadRegistration;
        private readonly object _lock = new();
        private bool _enabled;

        public FapCommandSettings(IConfiguration configuration, ILogger<FapCommandSettings> logger)
        {
            _configuration = configuration;
            _logger = logger;
            _enabled = Read();
            if (_enabled)
                _logger.LogWarning("FAP commands are enabled in the NeoHub settings (experimental): clients can raise a real Fire, Auxiliary or Panic alarm");
            _reloadRegistration = ChangeToken.OnChange(_configuration.GetReloadToken, OnReload);
        }

        public bool AllowFapCommands
        {
            get { lock (_lock) return _enabled; }
        }

        public event Action? Changed;

        private bool Read()
        {
            var raw = _configuration[ConfigurationKey];
            if (string.IsNullOrEmpty(raw))
                return false;
            if (bool.TryParse(raw, out var value))
                return value;
            _logger.LogWarning("Invalid {Key} value in the settings; FAP commands stay disabled", ConfigurationKey);
            return false;
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
