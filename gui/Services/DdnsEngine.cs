using System.IO;
using System.Net.Http.Headers;
using CloudflareDdns.Configuration;
using CloudflareDdns.Gui.Models;
using CloudflareDdns.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using Serilog;

namespace CloudflareDdns.Gui.Services;

/// <summary>Outcome of a live dashboard inspection pass.</summary>
public sealed record InspectResult(
    string? PublicIp,
    DdnsState State,
    IReadOnlyList<HostRecordStatus> Hosts,
    string? Error);

/// <summary>One hostname's outcome from <see cref="DdnsEngine.CreateMissingRecordsAsync"/>.</summary>
public sealed record CreateMissingOutcome(string Hostname, bool Created, string Detail);

/// <summary>
/// Hosts the *same* dependency-injected services the Windows Service runs (PublicIpProvider,
/// HostnameProvider, CloudflareClient, DdnsUpdater, StateStore), so the control panel drives
/// real syncs and dry-runs through identical code. A fresh host is built per operation so edits
/// to appsettings*.json take effect immediately, while all log output is fanned into the shared
/// <see cref="ObservableLogSink"/> for the live panel.
/// </summary>
public sealed class DdnsEngine
{
    private readonly ObservableLogSink _sink;

    public DdnsEngine(ObservableLogSink sink) => _sink = sink;

    /// <summary>Directory the config is read from (re-resolved each call so it tracks install state).</summary>
    public string ConfigDir => AppPaths.ResolveConfigDir();

    private IHost BuildHost(bool? dryRunOverride = null)
    {
        var settings = new HostApplicationBuilderSettings
        {
            ContentRootPath = ConfigDir,
            Args = Array.Empty<string>()
        };
        var builder = Host.CreateApplicationBuilder(settings);

        // Local overrides (token, real hostnames) win over appsettings.json — same as the service.
        builder.Configuration.AddJsonFile("appsettings.local.json", optional: true, reloadOnChange: false);

        if (dryRunOverride is bool dry)
        {
            builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
            {
                ["Ddns:DryRun"] = dry ? "true" : "false"
            });
        }

        var options = builder.Configuration.GetSection(DdnsOptions.SectionName).Get<DdnsOptions>()
                      ?? new DdnsOptions();

        // Serilog -> live UI sink + a control-panel rolling log file (separate from the service's own log).
        builder.Logging.ClearProviders();
        builder.Services.AddSerilog((_, lc) => lc
            .MinimumLevel.Debug()
            // Keep the live panel readable: mute the framework's per-request HTTP chatter,
            // matching the service's own appsettings.json overrides.
            .MinimumLevel.Override("Microsoft", Serilog.Events.LogEventLevel.Warning)
            .MinimumLevel.Override("System.Net.Http.HttpClient", Serilog.Events.LogEventLevel.Warning)
            .Enrich.FromLogContext()
            .WriteTo.Sink(_sink)
            .WriteTo.File(
                Path.Combine(AppPaths.LogsDir, "gui-.log"),
                rollingInterval: Serilog.RollingInterval.Day,
                retainedFileCountLimit: 14,
                outputTemplate: "{Timestamp:yyyy-MM-dd HH:mm:ss.fff zzz} [{Level:u3}] {Message:lj}{NewLine}{Exception}"));

        // NOTE: no ValidateOnStart — we want the panel to open and show config even with a placeholder
        // token. Sync/token-test paths validate explicitly and surface a friendly message.
        builder.Services.AddOptions<DdnsOptions>()
            .Bind(builder.Configuration.GetSection(DdnsOptions.SectionName));

        builder.Services.AddHttpClient("ip", c =>
        {
            c.Timeout = TimeSpan.FromSeconds(15);
            c.DefaultRequestHeaders.UserAgent.ParseAdd("CloudflareDdns-GUI/1.0");
        });

        builder.Services.AddHttpClient("cloudflare", c =>
        {
            c.BaseAddress = new Uri("https://api.cloudflare.com/client/v4/");
            c.Timeout = TimeSpan.FromSeconds(30);
            if (!string.IsNullOrWhiteSpace(options.ApiToken))
                c.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", options.ApiToken);
            c.DefaultRequestHeaders.UserAgent.ParseAdd("CloudflareDdns-GUI/1.0");
            c.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
        });

        builder.Services.AddSingleton<IPublicIpProvider, PublicIpProvider>();
        builder.Services.AddSingleton<IHostnameProvider, HostnameProvider>();
        builder.Services.AddSingleton<ICloudflareClient, CloudflareClient>();
        builder.Services.AddSingleton<StateStore>();
        builder.Services.AddSingleton<DdnsUpdater>();

        return builder.Build();
    }

    /// <summary>Reads the strongly-typed options exactly as the service would bind them.</summary>
    public DdnsOptions GetOptions()
    {
        using var host = BuildHost();
        return host.Services.GetRequiredService<IOptions<DdnsOptions>>().Value;
    }

    /// <summary>Loads the persisted last-known-IP state file.</summary>
    public DdnsState ReadState()
    {
        using var host = BuildHost();
        return host.Services.GetRequiredService<StateStore>().Load();
    }

    /// <summary>Resolves the current public IPv4 using the configured providers.</summary>
    public async Task<string?> GetPublicIpAsync(CancellationToken ct)
    {
        using var host = BuildHost();
        var ip = await host.Services.GetRequiredService<IPublicIpProvider>().GetPublicIpAsync(ct);
        return ip?.ToString();
    }

    /// <summary>Runs one full reconcile pass. When <paramref name="dryRun"/> is true nothing is written.</summary>
    public async Task RunSyncAsync(bool dryRun, CancellationToken ct)
    {
        using var host = BuildHost(dryRunOverride: dryRun);
        var options = host.Services.GetRequiredService<IOptions<DdnsOptions>>().Value;
        options.Validate(); // throws a friendly message if the token is missing / placeholder
        await host.Services.GetRequiredService<DdnsUpdater>().RunOnceAsync(ct);
    }

    /// <summary>Verifies the API token by listing visible zones. Returns the zone names.</summary>
    public async Task<IReadOnlyList<string>> TestTokenAsync(CancellationToken ct)
    {
        using var host = BuildHost();
        var options = host.Services.GetRequiredService<IOptions<DdnsOptions>>().Value;
        if (string.IsNullOrWhiteSpace(options.ApiToken) ||
            options.ApiToken.Contains("PUT-YOUR", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("No API token is set. Add one on the Configuration tab first.");

        var zones = await host.Services.GetRequiredService<ICloudflareClient>().GetZonesAsync(ct);
        return zones.Select(z => z.Name).OrderBy(n => n).ToArray();
    }

    /// <summary>
    /// Live read-only snapshot for the dashboard: public IP, cached state, and each managed
    /// hostname's current Cloudflare A record vs. the current IP. Never writes anything.
    /// </summary>
    public async Task<InspectResult> InspectAsync(CancellationToken ct)
    {
        using var host = BuildHost();
        var sp = host.Services;
        var state = sp.GetRequiredService<StateStore>().Load();

        var hostnames = sp.GetRequiredService<IHostnameProvider>().GetHostnames().ToArray();
        var rows = hostnames.Select(h => new HostRecordStatus { Hostname = h }).ToArray();

        string? ip;
        try
        {
            ip = (await sp.GetRequiredService<IPublicIpProvider>().GetPublicIpAsync(ct))?.ToString();
        }
        catch (Exception ex)
        {
            return new InspectResult(null, state, rows, $"Could not resolve public IP: {ex.Message}");
        }

        var options = sp.GetRequiredService<IOptions<DdnsOptions>>().Value;
        if (string.IsNullOrWhiteSpace(options.ApiToken) ||
            options.ApiToken.Contains("PUT-YOUR", StringComparison.OrdinalIgnoreCase))
        {
            foreach (var r in rows) { r.Status = "Unknown"; r.Detail = "No API token set"; }
            return new InspectResult(ip, state, rows, "No Cloudflare API token set — showing hostnames only.");
        }

        IReadOnlyList<CfZone> zones;
        var cf = sp.GetRequiredService<ICloudflareClient>();
        try
        {
            zones = await cf.GetZonesAsync(ct);
        }
        catch (Exception ex)
        {
            foreach (var r in rows) { r.Status = "Error"; r.Detail = "Zone lookup failed"; }
            return new InspectResult(ip, state, rows, $"Cloudflare zone lookup failed: {ex.Message}");
        }

        foreach (var r in rows)
        {
            ct.ThrowIfCancellationRequested();
            var zone = FindZone(r.Hostname, zones);
            if (zone is null)
            {
                r.Status = "Missing";
                r.Detail = "No matching zone";
                continue;
            }

            r.Zone = zone.Name;
            try
            {
                var rec = await cf.GetARecordAsync(zone.Id, r.Hostname, ct);
                if (rec is null)
                {
                    r.Status = "Missing";
                    r.Detail = "No A record";
                }
                else
                {
                    r.CurrentIp = rec.Content;
                    r.Proxied = rec.Proxied;
                    var matches = string.Equals(rec.Content, ip, StringComparison.Ordinal);
                    r.Status = matches ? "Match" : "Mismatch";
                    r.Detail = matches ? "Up to date" : $"Points to {rec.Content}";
                }
            }
            catch (Exception ex)
            {
                r.Status = "Error";
                r.Detail = ex.Message;
            }
        }

        return new InspectResult(ip, state, rows, null);
    }

    /// <summary>
    /// Creates an A record for every managed hostname that has an owning Cloudflare zone but no
    /// existing A record yet, pointing it at the current public IP using the configured TTL/Proxied
    /// settings. Existing records (even mismatched ones) are left untouched — this only fills gaps.
    /// </summary>
    public async Task<IReadOnlyList<CreateMissingOutcome>> CreateMissingRecordsAsync(CancellationToken ct)
    {
        using var host = BuildHost(dryRunOverride: false);
        var sp = host.Services;
        var options = sp.GetRequiredService<IOptions<DdnsOptions>>().Value;
        options.Validate();

        var ip = await sp.GetRequiredService<IPublicIpProvider>().GetPublicIpAsync(ct)
                  ?? throw new InvalidOperationException("Could not resolve the public IP.");
        var ipText = ip.ToString();

        var hostnames = sp.GetRequiredService<IHostnameProvider>().GetHostnames();
        var cf = sp.GetRequiredService<ICloudflareClient>();
        var zones = await cf.GetZonesAsync(ct);

        var results = new List<CreateMissingOutcome>();
        foreach (var hostname in hostnames)
        {
            ct.ThrowIfCancellationRequested();
            var zone = FindZone(hostname, zones);
            if (zone is null)
            {
                results.Add(new CreateMissingOutcome(hostname, false, "No matching Cloudflare zone"));
                continue;
            }

            try
            {
                var existing = await cf.GetARecordAsync(zone.Id, hostname, ct);
                if (existing is not null)
                {
                    results.Add(new CreateMissingOutcome(hostname, false, $"Already exists ({existing.Content})"));
                    continue;
                }

                await cf.CreateARecordAsync(zone.Id, hostname, ipText, options.RecordTtl, options.Proxied, ct);
                results.Add(new CreateMissingOutcome(hostname, true, $"Created -> {ipText} (zone {zone.Name})"));
            }
            catch (Exception ex)
            {
                results.Add(new CreateMissingOutcome(hostname, false, $"Failed: {ex.Message}"));
            }
        }

        return results;
    }

    /// <summary>Longest-suffix zone match — mirrors DdnsUpdater.FindZone so the preview matches reality.</summary>
    private static CfZone? FindZone(string host, IReadOnlyList<CfZone> zones)
    {
        CfZone? best = null;
        foreach (var z in zones)
        {
            var zn = z.Name;
            var matches = host.Equals(zn, StringComparison.OrdinalIgnoreCase) ||
                          host.EndsWith("." + zn, StringComparison.OrdinalIgnoreCase);
            if (matches && (best is null || zn.Length > best.Name.Length))
                best = z;
        }
        return best;
    }
}
