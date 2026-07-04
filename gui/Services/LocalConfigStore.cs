using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;
using CloudflareDdns.Configuration;
using Microsoft.Extensions.Configuration;

namespace CloudflareDdns.Gui.Services;

/// <summary>Plain, editable view of the Ddns config section (what the form binds to).</summary>
public sealed class ConfigModel
{
    public string ApiToken { get; set; } = "";
    public string HostnameSource { get; set; } = nameof(HostnameSourceKind.Iis);
    public string Interval { get; set; } = "01:00:00";
    public List<string> Hostnames { get; set; } = new();
    public List<string> ExcludeHostnames { get; set; } = new();
    public List<string> IpProviders { get; set; } = new();
    public int RecordTtl { get; set; } = 300;
    public bool Proxied { get; set; }
    public bool CreateIfMissing { get; set; }
    public bool DryRun { get; set; }
    public string StateFile { get; set; } = AppPaths.DefaultStateFile;
}

/// <summary>
/// Loads the *effective* Ddns config (appsettings.json + appsettings.local.json + env, local wins) for
/// the editor, and writes changes back to appsettings.local.json — the git-ignored override file where
/// secrets belong. Also exposes raw text access to both files for the advanced JSON editors.
/// </summary>
public sealed class LocalConfigStore
{
    private static readonly JsonSerializerOptions Pretty = new()
    {
        WriteIndented = true,
        // Keep hostnames/URLs readable (no / etc.).
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
        // JsonObject.ToJsonString(options) requires an explicit resolver on .NET 8 — without this
        // it throws "must specify a TypeInfoResolver setting before being marked as read-only".
        TypeInfoResolver = new System.Text.Json.Serialization.Metadata.DefaultJsonTypeInfoResolver()
    };

    public string ConfigDir { get; }
    public string LocalFilePath => AppPaths.LocalConfigFile(ConfigDir);
    public string BaseFilePath => AppPaths.BaseConfigFile(ConfigDir);

    public LocalConfigStore(string configDir) => ConfigDir = configDir;

    /// <summary>Reads the effective, merged config the service would actually use.</summary>
    public ConfigModel Load()
    {
        var cfg = new ConfigurationBuilder()
            .SetBasePath(ConfigDir)
            .AddJsonFile("appsettings.json", optional: true, reloadOnChange: false)
            .AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: false)
            .AddEnvironmentVariables()
            .Build();

        var o = cfg.GetSection(DdnsOptions.SectionName).Get<DdnsOptions>() ?? new DdnsOptions();

        return new ConfigModel
        {
            ApiToken = o.ApiToken,
            HostnameSource = o.HostnameSource.ToString(),
            Interval = o.Interval.ToString(),
            Hostnames = o.Hostnames ?? new(),
            ExcludeHostnames = o.ExcludeHostnames ?? new(),
            IpProviders = o.IpProviders is { Count: > 0 } ? o.IpProviders : DefaultIpProviders(),
            RecordTtl = o.RecordTtl,
            Proxied = o.Proxied,
            CreateIfMissing = o.CreateIfMissing,
            DryRun = o.DryRun,
            StateFile = string.IsNullOrWhiteSpace(o.StateFile) ? AppPaths.DefaultStateFile : o.StateFile
        };
    }

    /// <summary>
    /// Writes the managed Ddns section into appsettings.local.json, preserving any other top-level
    /// sections already present in that file. Creates the file if it doesn't exist.
    /// </summary>
    public void Save(ConfigModel m)
    {
        JsonObject root;
        if (File.Exists(LocalFilePath))
        {
            try
            {
                root = JsonNode.Parse(File.ReadAllText(LocalFilePath)) as JsonObject ?? new JsonObject();
            }
            catch
            {
                root = new JsonObject();
            }
        }
        else
        {
            root = new JsonObject();
        }

        var ddns = new JsonObject
        {
            ["ApiToken"] = m.ApiToken ?? "",
            ["Interval"] = NormalizeInterval(m.Interval),
            ["HostnameSource"] = m.HostnameSource,
            ["Hostnames"] = ToArray(m.Hostnames),
            ["ExcludeHostnames"] = ToArray(m.ExcludeHostnames),
            ["IpProviders"] = ToArray(m.IpProviders),
            ["RecordTtl"] = m.RecordTtl,
            ["Proxied"] = m.Proxied,
            ["CreateIfMissing"] = m.CreateIfMissing,
            ["DryRun"] = m.DryRun,
            ["StateFile"] = m.StateFile ?? AppPaths.DefaultStateFile
        };

        root["Ddns"] = ddns;

        var dir = Path.GetDirectoryName(LocalFilePath);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(LocalFilePath, root.ToJsonString(Pretty));
    }

    public string ReadRaw(string path) => File.Exists(path) ? File.ReadAllText(path) : "";

    /// <summary>Validates JSON then writes it verbatim. Throws JsonException on malformed input.</summary>
    public void WriteRaw(string path, string json)
    {
        using var _ = JsonDocument.Parse(json); // throws on invalid JSON before we touch the file
        var dir = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
        File.WriteAllText(path, json);
    }

    private static string NormalizeInterval(string raw) =>
        TimeSpan.TryParse(raw, out var ts) ? ts.ToString() : "01:00:00";

    private static JsonArray ToArray(IEnumerable<string>? items)
    {
        var arr = new JsonArray();
        if (items is null) return arr;
        foreach (var s in items)
            if (!string.IsNullOrWhiteSpace(s))
                arr.Add(s.Trim());
        return arr;
    }

    public static List<string> DefaultIpProviders() => new()
    {
        "https://api.ipify.org",
        "https://checkip.amazonaws.com",
        "https://icanhazip.com",
        "https://ifconfig.me/ip"
    };
}
