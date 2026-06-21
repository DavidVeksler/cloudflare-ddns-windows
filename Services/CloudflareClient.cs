using System.Net.Http.Json;
using System.Text.Json;

namespace CloudflareDdns.Services;

public interface ICloudflareClient
{
    Task<IReadOnlyList<CfZone>> GetZonesAsync(CancellationToken ct);
    Task<CfDnsRecord?> GetARecordAsync(string zoneId, string name, CancellationToken ct);
    Task UpdateARecordAsync(string zoneId, CfDnsRecord existing, string ip, int ttl, bool proxied, CancellationToken ct);
    Task<CfDnsRecord> CreateARecordAsync(string zoneId, string name, string ip, int ttl, bool proxied, CancellationToken ct);
}

/// <summary>
/// Thin wrapper over the Cloudflare API v4. Auth (bearer token) and base address are
/// configured on the named HttpClient in Program.cs.
/// </summary>
public sealed class CloudflareClient : ICloudflareClient
{
    private static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _http;
    private readonly ILogger<CloudflareClient> _log;

    public CloudflareClient(IHttpClientFactory factory, ILogger<CloudflareClient> log)
    {
        _http = factory.CreateClient("cloudflare");
        _log = log;
    }

    public async Task<IReadOnlyList<CfZone>> GetZonesAsync(CancellationToken ct)
    {
        var zones = new List<CfZone>();
        var page = 1;

        while (true)
        {
            var body = await GetAsync<List<CfZone>>($"zones?per_page=50&page={page}", ct);
            if (body.Result is { Count: > 0 })
                zones.AddRange(body.Result);

            var info = body.ResultInfo;
            if (info is null || info.Page >= info.TotalPages || info.TotalPages == 0)
                break;
            page++;
        }

        return zones;
    }

    public async Task<CfDnsRecord?> GetARecordAsync(string zoneId, string name, CancellationToken ct)
    {
        var url = $"zones/{zoneId}/dns_records?type=A&name={Uri.EscapeDataString(name)}";
        var body = await GetAsync<List<CfDnsRecord>>(url, ct);
        return body.Result?.FirstOrDefault();
    }

    public async Task UpdateARecordAsync(
        string zoneId, CfDnsRecord existing, string ip, int ttl, bool proxied, CancellationToken ct)
    {
        var payload = new CfDnsRecordWrite
        {
            Type = "A",
            Name = existing.Name,
            Content = ip,
            Ttl = ttl,
            Proxied = proxied
        };

        using var resp = await _http.PutAsJsonAsync(
            $"zones/{zoneId}/dns_records/{existing.Id}", payload, Json, ct);
        await EnsureSuccess<CfDnsRecord>(resp, "update DNS record", ct);
    }

    public async Task<CfDnsRecord> CreateARecordAsync(
        string zoneId, string name, string ip, int ttl, bool proxied, CancellationToken ct)
    {
        var payload = new CfDnsRecordWrite
        {
            Type = "A",
            Name = name,
            Content = ip,
            Ttl = ttl,
            Proxied = proxied
        };

        using var resp = await _http.PostAsJsonAsync(
            $"zones/{zoneId}/dns_records", payload, Json, ct);
        var body = await EnsureSuccess<CfDnsRecord>(resp, "create DNS record", ct);
        return body.Result!;
    }

    private async Task<CfResponse<T>> GetAsync<T>(string relativeUrl, CancellationToken ct)
    {
        using var resp = await _http.GetAsync(relativeUrl, ct);
        return await EnsureSuccess<T>(resp, $"GET {relativeUrl}", ct);
    }

    private async Task<CfResponse<T>> EnsureSuccess<T>(
        HttpResponseMessage resp, string what, CancellationToken ct)
    {
        var content = await resp.Content.ReadAsStringAsync(ct);

        CfResponse<T>? body = null;
        try { body = JsonSerializer.Deserialize<CfResponse<T>>(content, Json); }
        catch (JsonException) { /* fall through to raw error below */ }

        if (!resp.IsSuccessStatusCode || body is null || !body.Success)
        {
            var errors = body is null
                ? content
                : string.Join("; ", body.Errors.Select(e => e.ToString()));
            throw new CloudflareApiException(
                $"Cloudflare call failed ({what}): HTTP {(int)resp.StatusCode}. {errors}");
        }

        return body;
    }
}

public sealed class CloudflareApiException : Exception
{
    public CloudflareApiException(string message) : base(message) { }
}
