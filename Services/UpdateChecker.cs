using System.Net.Http;
using System.Text.Json;
using System.Reflection;
using StarRuptureSaveFixer.Utils;

namespace StarRuptureSaveFixer.Services;

public class UpdateChecker
{
    private const string GITHUB_API_URL = "https://api.github.com/repos/AlienXAXS/StarRupture-Save-Fixer/releases/latest";
    private static readonly HttpClient _httpClient = new()
    {
        DefaultRequestHeaders = { { "User-Agent", "StarRupture-Save-Fixer" } }
    };

    public async Task<UpdateInfo?> CheckForUpdatesAsync()
    {
        try
        {
            string currentVersion = GetCurrentVersion();
            ConsoleLogger.Info($"Checking for updates (current version: v{currentVersion})...");
            ConsoleLogger.Info($"Querying repository: {GITHUB_API_URL}");

            var response = await _httpClient.GetStringAsync(GITHUB_API_URL);
            var release = JsonDocument.Parse(response);
            var root = release.RootElement;

            string latestVersion = root.GetProperty("tag_name").GetString()?.TrimStart('v') ?? "";
            ConsoleLogger.Info($"Latest version on repository: v{latestVersion}");

            bool updateAvailable = IsNewerVersion(latestVersion, currentVersion);

            if (updateAvailable)
            {
                ConsoleLogger.Success($"Update available: v{currentVersion} -> v{latestVersion}");
            }
            else
            {
                ConsoleLogger.Info($"You are running the latest version (v{currentVersion}).");
            }

            return new UpdateInfo
            {
                CurrentVersion = currentVersion,
                LatestVersion = latestVersion,
                UpdateAvailable = updateAvailable,
                DownloadUrl = root.GetProperty("html_url").GetString() ?? "",
                ReleaseNotes = root.GetProperty("body").GetString() ?? ""
            };
        }
        catch (Exception ex)
        {
            ConsoleLogger.Warning($"Update check failed: {ex.Message}");
            return null; // Fail quietly for the caller, but the failure is now logged
        }
    }

    private string GetCurrentVersion()
    {
        var version = Assembly.GetExecutingAssembly().GetName().Version;
        return version != null ? $"{version.Major}.{version.Minor}.{version.Build}" : "0.0.0";
    }

    private bool IsNewerVersion(string latest, string current)
    {
        try
        {
            var latestParts = latest.Split('.').Select(int.Parse).ToArray();
            var currentParts = current.Split('.').Select(int.Parse).ToArray();

            for (int i = 0; i < Math.Min(latestParts.Length, currentParts.Length); i++)
            {
                if (latestParts[i] > currentParts[i]) return true;
                if (latestParts[i] < currentParts[i]) return false;
            }

            return latestParts.Length > currentParts.Length;
        }
        catch
        {
            return false;
        }
    }
}

public class UpdateInfo
{
    public string CurrentVersion { get; set; } = "";
    public string LatestVersion { get; set; } = "";
    public bool UpdateAvailable { get; set; }
    public string DownloadUrl { get; set; } = "";
    public string ReleaseNotes { get; set; } = "";
}
