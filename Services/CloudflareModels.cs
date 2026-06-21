using System.Text.Json.Serialization;

namespace CloudflareDdns.Services;

// Minimal subset of the Cloudflare API v4 response shapes we care about.

public sealed class CfResponse<T>
{
    [JsonPropertyName("success")] public bool Success { get; set; }
    [JsonPropertyName("errors")] public List<CfMessage> Errors { get; set; } = new();
    [JsonPropertyName("messages")] public List<CfMessage> Messages { get; set; } = new();
    [JsonPropertyName("result")] public T? Result { get; set; }
    [JsonPropertyName("result_info")] public CfResultInfo? ResultInfo { get; set; }
}

public sealed class CfMessage
{
    [JsonPropertyName("code")] public int Code { get; set; }
    [JsonPropertyName("message")] public string Message { get; set; } = "";
    public override string ToString() => $"{Code}: {Message}";
}

public sealed class CfResultInfo
{
    [JsonPropertyName("page")] public int Page { get; set; }
    [JsonPropertyName("total_pages")] public int TotalPages { get; set; }
}

public sealed class CfZone
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
}

public sealed class CfDnsRecord
{
    [JsonPropertyName("id")] public string Id { get; set; } = "";
    [JsonPropertyName("type")] public string Type { get; set; } = "";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("content")] public string Content { get; set; } = "";
    [JsonPropertyName("ttl")] public int Ttl { get; set; }
    [JsonPropertyName("proxied")] public bool Proxied { get; set; }
}

public sealed class CfDnsRecordWrite
{
    [JsonPropertyName("type")] public string Type { get; set; } = "A";
    [JsonPropertyName("name")] public string Name { get; set; } = "";
    [JsonPropertyName("content")] public string Content { get; set; } = "";
    [JsonPropertyName("ttl")] public int Ttl { get; set; }
    [JsonPropertyName("proxied")] public bool Proxied { get; set; }
}
