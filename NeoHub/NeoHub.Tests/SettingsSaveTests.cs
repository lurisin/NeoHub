using Microsoft.AspNetCore.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.FileProviders;
using Microsoft.Extensions.Logging.Abstractions;
using NeoHub.Services;
using NeoHub.Services.Settings;

namespace NeoHub.Tests;

/// <summary>Saving settings applies them at once, also without a file watcher.</summary>
public class SettingsSaveTests
{
    private sealed class Env : IWebHostEnvironment
    {
        public string WebRootPath { get; set; } = "";
        public IFileProvider WebRootFileProvider { get; set; } = new NullFileProvider();
        public string ApplicationName { get; set; } = "NeoHub";
        public IFileProvider ContentRootFileProvider { get; set; } = new NullFileProvider();
        public string ContentRootPath { get; set; } = "";
        public string EnvironmentName { get; set; } = "Production";
    }

    [Fact]
    public async Task Saving_AllowFapCommands_applies_without_waiting_for_the_file_watcher()
    {
        if (!string.IsNullOrEmpty(Environment.GetEnvironmentVariable("NEOHUB_PERSIST_PATH")))
            return;   // the persistence service would use that folder instead of the test's own

        var root = Directory.CreateTempSubdirectory("neohub-settings-test-");
        try
        {
            var file = SettingsPersistenceService.GetSettingsFilePath(root.FullName);
            Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            await File.WriteAllTextAsync(file, """{"Application":{"ListenPort":3072}}""");

            // No reloadOnChange: only an explicit reload can pick up the saved file.
            var configuration = new ConfigurationBuilder().AddJsonFile(file, optional: true, reloadOnChange: false).Build();
            var services = new ServiceCollection();
            services.AddSingleton<IConfiguration>(configuration);
            services.Configure<ApplicationSettings>(configuration.GetSection(ApplicationSettings.SectionName));
            var provider = services.BuildServiceProvider();
            var persistence = new SettingsPersistenceService(provider, new Env { ContentRootPath = root.FullName },
                NullLogger<SettingsPersistenceService>.Instance);
            using var fap = new FapCommandSettings(configuration, NullLogger<FapCommandSettings>.Instance);
            var changes = 0;
            fap.Changed += () => changes++;

            await persistence.SaveSettingsAsync(typeof(ApplicationSettings), new ApplicationSettings { AllowFapCommands = true });
            Assert.True(fap.AllowFapCommands);
            Assert.Contains("\"AllowFapCommands\": true", await File.ReadAllTextAsync(file));

            await persistence.SaveSettingsAsync(typeof(ApplicationSettings), new ApplicationSettings { AllowFapCommands = false });
            Assert.False(fap.AllowFapCommands);
            Assert.Equal(2, changes);
        }
        finally
        {
            root.Delete(recursive: true);
        }
    }
}
