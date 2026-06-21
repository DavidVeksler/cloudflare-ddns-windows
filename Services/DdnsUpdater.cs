using System.Net;
using CloudflareDdns.Configuration;
using Microsoft.Extensions.Options;

namespace CloudflareDdns.Services;

/// <summary>
/// One full sync pass: resolve the public IP, discover managed hostnames, map each to its
/// Cloudflare zone, and update any A record whose content differs from the current IP.
/// </summary>
public sealed class DdnsUpdater
{
    private readonly IPublicIpProvider _ip;
    private readonly IHostnameProvider _hostnames;
    private readonly ICloudflareClient _cf;
    private readonly StateStore _state;
    private readonly DdnsOptions _options;
    private readonly ILogger<DdnsUpdater> _log;

    public DdnsUpdater(
        IPublicIpProvider ip,
        IHostnameProvider hostnames,
        ICloudflareClient cf,
        StateStore state,
        IOptions<DdnsOptions> options,
        ILogger<DdnsUpdater> log)
    {
        _ip = ip;
        _hostnames = hostnames;
        _cf = cf;
        _state = state;
        _options = options.Value;
        _log = log;
    }

    public async Task RunOnceAsync(CancellationToken ct)
    {
        var ip = await _ip.GetPublicIpAsync(ct);
        if (ip is null)
        {
            _log.LogWarning("Skipping this run: public IP could not be determined.");
            return;
        }
        var ipText = ip.ToString();

        var hostnames = _hostnames.GetHostnames();
        if (hostnames.Count == 0)
        {
            _log.LogWarning("No managed hostnames found; nothing to do.");
            return;
        }

        var state = _state.Load();
        state.LastCheckedUtc = DateTimeOffset.UtcNow;

        if (_options.DryRun)
            _log.LogInformation("[DRY RUN] No changes will be written to Cloudflare.");

        // Fast path: IP unchanged since last run -> assume records are still correct.
        // (We still persist the "last checked" timestamp so monitoring shows liveness.)
        // Skipped in dry-run so you can see the full hostname -> zone -> record mapping.
        if (!_options.DryRun && string.Equals(state.LastIp, ipText, StringComparison.Ordinal))
        {
            _log.LogInformation("Public IP unchanged ({Ip}); no Cloudflare calls needed.", ipText);
            _state.Save(state);
            return;
        }

        _log.LogInformation("Public IP is {Ip} (was {Old}); reconciling {Count} hostname(s).",
            ipText, state.LastIp ?? "unknown", hostnames.Count);

        var zones = await _cf.GetZonesAsync(ct);
        if (zones.Count == 0)
            _log.LogWarning("The API token can't see any zones. Check token permissions.");

        var anyFailed = false;
        var changed = 0;

        foreach (var host in hostnames)
        {
            try
            {
                var didChange = await ReconcileHostAsync(host, ipText, zones, ct);
                if (didChange) changed++;
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                anyFailed = true;
                _log.LogError(ex, "Failed to reconcile {Host}.", host);
            }
        }

        if (_options.DryRun)
        {
            // Read-only: never advance the cache or write the state file.
            _log.LogInformation("[DRY RUN] Reconcile preview complete: {Changed} record(s) would change.",
                changed);
            return;
        }

        // Only advance the cached IP if every host reconciled cleanly, so a partial
        // failure is retried next run instead of being masked by the fast path.
        if (!anyFailed)
        {
            state.LastIp = ipText;
            state.LastUpdatedUtc = DateTimeOffset.UtcNow;
            _log.LogInformation("Reconcile complete: {Changed} record(s) changed, IP cached as {Ip}.",
                changed, ipText);
        }
        else
        {
            _log.LogWarning("Reconcile finished with errors; IP not cached so it retries next run.");
        }

        _state.Save(state);
    }

    /// <summary>Returns true if a record was created or updated.</summary>
    private async Task<bool> ReconcileHostAsync(
        string host, string ipText, IReadOnlyList<CfZone> zones, CancellationToken ct)
    {
        var zone = FindZone(host, zones);
        if (zone is null)
        {
            _log.LogWarning("No Cloudflare zone owns '{Host}'; skipping.", host);
            return false;
        }

        var record = await _cf.GetARecordAsync(zone.Id, host, ct);

        if (record is null)
        {
            if (!_options.CreateIfMissing)
            {
                _log.LogInformation(
                    "No A record for '{Host}' in zone '{Zone}' and CreateIfMissing=false; skipping.",
                    host, zone.Name);
                return false;
            }

            if (_options.DryRun)
            {
                _log.LogInformation("[DRY RUN] Would create A record {Host} -> {Ip} (zone {Zone}).",
                    host, ipText, zone.Name);
                return true;
            }

            await _cf.CreateARecordAsync(zone.Id, host, ipText, _options.RecordTtl, _options.Proxied, ct);
            _log.LogInformation("Created A record {Host} -> {Ip} (zone {Zone}).", host, ipText, zone.Name);
            return true;
        }

        if (string.Equals(record.Content, ipText, StringComparison.Ordinal))
        {
            _log.LogInformation("A record {Host} already points to {Ip} (zone {Zone}); no change.",
                host, ipText, zone.Name);
            return false;
        }

        if (_options.DryRun)
        {
            _log.LogInformation(
                "[DRY RUN] Would update A record {Host}: {Old} -> {Ip} (zone {Zone}, proxied={Proxied} preserved).",
                host, record.Content, ipText, zone.Name, record.Proxied);
            return true;
        }

        // Only the IP changes. Preserve the record's existing proxied flag and TTL so we never
        // silently flip the orange cloud or retime a record the user configured in the dashboard.
        // (Ddns:Proxied / Ddns:RecordTtl apply only to records this service *creates*.)
        await _cf.UpdateARecordAsync(zone.Id, record, ipText, record.Ttl, record.Proxied, ct);
        _log.LogInformation("Updated A record {Host}: {Old} -> {Ip} (zone {Zone}, proxied={Proxied}).",
            host, record.Content, ipText, zone.Name, record.Proxied);
        return true;
    }

    /// <summary>
    /// Picks the zone whose name is the longest suffix of the hostname, so
    /// "a.b.example.co.uk" matches zone "example.co.uk" over "co.uk".
    /// </summary>
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
