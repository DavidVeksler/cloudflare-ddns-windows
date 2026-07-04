using System.IO;

namespace CloudflareDdns.Gui.Services;

/// <summary>
/// Resolves the on-disk locations the control panel works with. The service reads its config from
/// the folder next to its exe and writes state/logs under %ProgramData%\CloudflareDdns, so we mirror
/// that here and prefer the *installed* copy when it exists (that's what the running service actually uses).
/// </summary>
public static class AppPaths
{
    public const string ServiceName = "CloudflareDdns";

    /// <summary>%ProgramData%\CloudflareDdns — state + logs live here (matches the service defaults).</summary>
    public static string DataDir { get; } = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "CloudflareDdns");

    public static string LogsDir { get; } = Path.Combine(DataDir, "logs");

    public static string DefaultStateFile { get; } = Path.Combine(DataDir, "state.json");

    /// <summary>The default install target used by install.ps1.</summary>
    public static string InstalledDir { get; } =
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "CloudflareDdns");

    /// <summary>
    /// Folder the config editor reads/writes. Order of preference:
    /// explicit env override -> installed service dir (if it has config) -> the app's own base dir (dev).
    /// </summary>
    public static string ResolveConfigDir()
    {
        var env = Environment.GetEnvironmentVariable("CLOUDFLAREDDNS_CONFIGDIR");
        if (!string.IsNullOrWhiteSpace(env) && Directory.Exists(env))
            return env;

        if (File.Exists(Path.Combine(InstalledDir, "appsettings.json")))
            return InstalledDir;

        return AppContext.BaseDirectory;
    }

    public static string BaseConfigFile(string configDir) => Path.Combine(configDir, "appsettings.json");
    public static string LocalConfigFile(string configDir) => Path.Combine(configDir, "appsettings.local.json");

    /// <summary>Finds install.ps1 / uninstall.ps1 — next to the exe first, else two dirs up (repo dev layout).</summary>
    public static string? FindScript(string fileName)
    {
        var candidates = new[]
        {
            Path.Combine(AppContext.BaseDirectory, fileName),
            Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "..", fileName),
            Path.Combine(InstalledDir, fileName),
        };
        foreach (var c in candidates)
        {
            var full = Path.GetFullPath(c);
            if (File.Exists(full)) return full;
        }
        return null;
    }
}
