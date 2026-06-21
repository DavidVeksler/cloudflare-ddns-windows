using System.Net;
using CloudflareDdns.Configuration;
using Microsoft.Extensions.Options;

namespace CloudflareDdns.Services;

public interface IPublicIpProvider
{
    /// <summary>Returns the current public IPv4 address, or null if every provider failed.</summary>
    Task<IPAddress?> GetPublicIpAsync(CancellationToken ct);
}

/// <summary>
/// Resolves the current public IPv4 by querying a list of "what is my IP" endpoints
/// in order. The first endpoint that returns a valid IPv4 wins.
/// </summary>
public sealed class PublicIpProvider : IPublicIpProvider
{
    private readonly IHttpClientFactory _httpFactory;
    private readonly DdnsOptions _options;
    private readonly ILogger<PublicIpProvider> _log;

    public PublicIpProvider(
        IHttpClientFactory httpFactory,
        IOptions<DdnsOptions> options,
        ILogger<PublicIpProvider> log)
    {
        _httpFactory = httpFactory;
        _options = options.Value;
        _log = log;
    }

    public async Task<IPAddress?> GetPublicIpAsync(CancellationToken ct)
    {
        var client = _httpFactory.CreateClient("ip");

        foreach (var url in _options.IpProviders)
        {
            try
            {
                var raw = await client.GetStringAsync(url, ct);
                var text = raw.Trim();

                if (IPAddress.TryParse(text, out var ip) &&
                    ip.AddressFamily == System.Net.Sockets.AddressFamily.InterNetwork)
                {
                    _log.LogDebug("Public IP {Ip} resolved via {Url}", ip, url);
                    return ip;
                }

                _log.LogWarning("Provider {Url} returned an unparseable IPv4: '{Raw}'", url, text);
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested)
            {
                throw;
            }
            catch (Exception ex)
            {
                _log.LogWarning(ex, "Provider {Url} failed; trying the next one.", url);
            }
        }

        _log.LogError("Could not resolve the public IP from any provider.");
        return null;
    }
}
