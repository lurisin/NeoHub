using Microsoft.Extensions.Options;
using System.Text.Json;

namespace NeoHub.Services.Settings
{
    /// <summary>
    /// Generic service for reading and persisting settings to persist/userSettings.json
    /// </summary>
    public interface ISettingsPersistenceService
    {
        object GetSettings(Type settingsType);
        Task SaveSettingsAsync(Type settingsType, object settings);
        string PersistPath { get; }
    }

    public class SettingsPersistenceService : ISettingsPersistenceService
    {
        private const string PersistFolder = "persist";
        private const string SettingsFileName = "userSettings.json";
        
        private readonly IServiceProvider _serviceProvider;
        private readonly ILogger<SettingsPersistenceService> _log;
        private readonly SemaphoreSlim _fileLock = new(1, 1);

        /// <summary>
        /// Returns the persist folder path. Checks NEOHUB_PERSIST_PATH env var first,
        /// falls back to {contentRootPath}/persist.
        /// </summary>
        public static string GetPersistPath(string contentRootPath)
        {
            var envPath = Environment.GetEnvironmentVariable("NEOHUB_PERSIST_PATH");
            return !string.IsNullOrEmpty(envPath) ? envPath : Path.Combine(contentRootPath, PersistFolder);
        }

        /// <summary>
        /// Returns the absolute path to userSettings.json.
        /// Used by Program.cs (AddJsonFile), SettingsMigration, and this service.
        /// </summary>
        public static string GetSettingsFilePath(string contentRootPath)
            => Path.Combine(GetPersistPath(contentRootPath), SettingsFileName);

        public string PersistPath { get; }
        private string SettingsFilePath { get; }

        public SettingsPersistenceService(
            IServiceProvider serviceProvider,
            IWebHostEnvironment env,
            ILogger<SettingsPersistenceService> log)
        {
            _serviceProvider = serviceProvider;
            _log = log;
            PersistPath = GetPersistPath(env.ContentRootPath);
            SettingsFilePath = Path.Combine(PersistPath, SettingsFileName);

            // Ensure persist directory exists
            Directory.CreateDirectory(PersistPath);
            _log.LogInformation("Settings file: {Path}", SettingsFilePath);
        }

        /// <summary>
        /// A new instance of the section's settings, bound from the configuration as it is now. The Settings page
        /// edits this instance, so an edit stays on the page until it is saved, and every load of the page starts
        /// from the configuration again. (This returned IOptionsMonitor.CurrentValue, the live object that the
        /// services read: an edit changed it before Save, and later loads of the page showed the unsaved value.)
        /// </summary>
        public object GetSettings(Type settingsType)
        {
            var factoryType = typeof(IOptionsFactory<>).MakeGenericType(settingsType);
            var factory = _serviceProvider.GetRequiredService(factoryType);
            var create = factoryType.GetMethod(nameof(IOptionsFactory<object>.Create))!;
            return create.Invoke(factory, new object[] { Options.DefaultName })!;
        }

        public async Task SaveSettingsAsync(Type settingsType, object settings)
        {
            var sectionNameField = settingsType.GetField("SectionName", 
                System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Static);
            var sectionName = sectionNameField?.GetValue(null)?.ToString() ?? settingsType.Name;

            await _fileLock.WaitAsync();
            try
            {
                var rootSettings = await ReadSettingsFileAsync();
                rootSettings[sectionName] = settings;

                var options = new JsonSerializerOptions { WriteIndented = true };
                var updatedJson = JsonSerializer.Serialize(rootSettings, options);
                await File.WriteAllTextAsync(SettingsFilePath, updatedJson);

                _log.LogInformation("Saved settings for section {Section}", sectionName);

                // Apply the saved file now instead of waiting for the file watcher (reloadOnChange), which can be
                // slow or missing on some mounts; turning FAP commands off, for one, must not wait for it.
                (_serviceProvider.GetService<IConfiguration>() as IConfigurationRoot)?.Reload();
            }
            finally
            {
                _fileLock.Release();
            }
        }

        private async Task<Dictionary<string, object>> ReadSettingsFileAsync()
        {
            if (!File.Exists(SettingsFilePath))
                return new Dictionary<string, object>();

            try
            {
                var json = await File.ReadAllTextAsync(SettingsFilePath);
                
                if (string.IsNullOrWhiteSpace(json))
                    return new Dictionary<string, object>();

                return JsonSerializer.Deserialize<Dictionary<string, object>>(json) 
                    ?? new Dictionary<string, object>();
            }
            catch (JsonException ex)
            {
                _log.LogWarning(ex, "Invalid JSON in {File}, starting fresh", SettingsFilePath);
                return new Dictionary<string, object>();
            }
        }
    }
}