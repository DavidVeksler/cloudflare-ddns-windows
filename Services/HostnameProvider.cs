using CloudflareDdns.Configuration;
using Microsoft.Extensions.Options;
using Microsoft.Web.Administration;

namespace CloudflareDdns.Services;

public interface IHostnameProvider
{
    /// <summary>Distinct, lower-cased hostnames the service should keep pointed at our IP.</summary>
    IReadOnlyCollection<string> GetHostnames();
}

/// <summary>
/// Builds the managed-hostname set from IIS site bindings and/or a static config list,
/// then strips wildcards, duplicates, and anything on the exclude list.
/// </summary>
public sealed class HostnameProvider : IHostnameProvider
{
    private readonly DdnsOptions _options;
    private readonly ILogger<HostnameProvider> _log;

    public HostnameProvider(IOptions<DdnsOptions> options, ILogger<HostnameProvider> log)
    {
        _options = options.Value;
        _log = log;
    }

    public IReadOnlyCollection<string> GetHostnames()
    {
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        if (_options.HostnameSource is HostnameSourceKind.Iis or HostnameSourceKind.Both)
        {
            foreach (var h in ReadFromIis())
                names.Add(h);
        }

        if (_options.HostnameSource is HostnameSourceKind.Static or HostnameSourceKind.Both)
        {
            foreach (var h in _options.Hostnames)
                if (!string.IsNullOrWhiteSpace(h))
                    names.Add(h.Trim());
        }

        var exclude = new HashSet<string>(
            _options.ExcludeHostnames.Select(e => e.Trim()),
            StringComparer.OrdinalIgnoreCase);

        var result = names
            .Select(Normalize)
            .Where(IsManageable)
            .Where(h => !exclude.Contains(h))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(h => h, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        _log.LogInformation("Managing {Count} hostname(s): {Hostnames}",
            result.Length, string.Join(", ", result));

        return result;
    }

    private List<string> ReadFromIis()
    {
        // ServerManager reads %windir%\System32\inetsrv\config\applicationHost.config (and
        // redirection.config). Reading these requires elevation — the service runs as
        // LocalSystem, which has access. A non-elevated console run will fail here; that's
        // expected, so we degrade to a warning rather than tearing down the run.
        var hosts = new List<string>();
        try
        {
            using var mgr = new ServerManager();
            foreach (var site in mgr.Sites)
            {
                foreach (var binding in site.Bindings)
                {
                    // Host header is empty for IP-only bindings; skip those.
                    var host = binding.Host;
                    if (!string.IsNullOrWhiteSpace(host))
                    {
                        _log.LogDebug("IIS site '{Site}' binding host '{Host}'", site.Name, host);
                        hosts.Add(host);
                    }
                }
            }
        }
        catch (UnauthorizedAccessException ex)
        {
            _log.LogWarning(ex,
                "Insufficient permissions to read IIS configuration. The service must run elevated " +
                "(it runs as LocalSystem once installed). Falling back to any static hostnames.");
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex,
                "Could not read IIS configuration (is IIS installed?). Falling back to any static hostnames.");
        }

        return hosts;
    }

    private static string Normalize(string host) =>
        host.Trim().TrimEnd('.').ToLowerInvariant();

    /// <summary>Excludes wildcards and obviously non-public names we should never push to Cloudflare.</summary>
    private static bool IsManageable(string host)
    {
        if (string.IsNullOrWhiteSpace(host)) return false;
        if (host.StartsWith("*")) return false;          // wildcard bindings
        if (!host.Contains('.')) return false;           // single-label (e.g. "localhost", machine names)
        return true;
    }
}
