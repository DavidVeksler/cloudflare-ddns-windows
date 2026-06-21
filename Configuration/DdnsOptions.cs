namespace CloudflareDdns.Configuration;

public enum HostnameSourceKind
{
    Iis,
    Static,
    Both
}

/// <summary>
/// Strongly-typed view of the "Ddns" section of appsettings.json.
/// </summary>
public sealed class DdnsOptions
{
    public const string SectionName = "Ddns";

    public string ApiToken { get; set; } = "";

    public TimeSpan Interval { get; set; } = TimeSpan.FromHours(1);

    public HostnameSourceKind HostnameSource { get; set; } = HostnameSourceKind.Iis;

    public List<string> Hostnames { get; set; } = new();

    public List<string> ExcludeHostnames { get; set; } = new();

    public List<string> IpProviders { get; set; } = new();

    public int RecordTtl { get; set; } = 300;

    public bool Proxied { get; set; }

    public bool CreateIfMissing { get; set; }

    /// <summary>When true, log intended changes but never write to Cloudflare. Set via --dry-run too.</summary>
    public bool DryRun { get; set; }

    public string StateFile { get; set; } =
        Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData),
            "CloudflareDdns", "state.json");

    /// <summary>Throws if the config is unusable, so the service fails fast & loud.</summary>
    public void Validate()
    {
        if (string.IsNullOrWhiteSpace(ApiToken) ||
            ApiToken.Contains("PUT-YOUR", StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException(
                "Ddns:ApiToken is not set. Provide a Cloudflare API token in appsettings.json " +
                "(or via environment variable Ddns__ApiToken).");
        }

        if (Interval < TimeSpan.FromMinutes(1))
            throw new InvalidOperationException("Ddns:Interval must be at least 1 minute.");

        if (IpProviders.Count == 0)
            throw new InvalidOperationException("Ddns:IpProviders must contain at least one URL.");

        if (RecordTtl is not (1 or >= 60))
            throw new InvalidOperationException("Ddns:RecordTtl must be 1 (automatic) or >= 60 seconds.");
    }
}
