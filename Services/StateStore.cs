using System.Text.Json;
using CloudflareDdns.Configuration;
using Microsoft.Extensions.Options;

namespace CloudflareDdns.Services;

public sealed class DdnsState
{
    public string? LastIp { get; set; }
    public DateTimeOffset? LastUpdatedUtc { get; set; }
    public DateTimeOffset? LastCheckedUtc { get; set; }
}

/// <summary>
/// Persists the last-known public IP to a JSON file so a restart doesn't re-push
/// unchanged records, and so the IP survives between hourly runs.
/// </summary>
public sealed class StateStore
{
    private static readonly JsonSerializerOptions Json = new() { WriteIndented = true };

    private readonly string _path;
    private readonly ILogger<StateStore> _log;

    public StateStore(IOptions<DdnsOptions> options, ILogger<StateStore> log)
    {
        _path = options.Value.StateFile;
        _log = log;
    }

    public DdnsState Load()
    {
        try
        {
            if (File.Exists(_path))
            {
                var json = File.ReadAllText(_path);
                return JsonSerializer.Deserialize<DdnsState>(json, Json) ?? new DdnsState();
            }
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not read state file {Path}; starting fresh.", _path);
        }
        return new DdnsState();
    }

    public void Save(DdnsState state)
    {
        try
        {
            var dir = Path.GetDirectoryName(_path);
            if (!string.IsNullOrEmpty(dir))
                Directory.CreateDirectory(dir);

            File.WriteAllText(_path, JsonSerializer.Serialize(state, Json));
        }
        catch (Exception ex)
        {
            _log.LogWarning(ex, "Could not write state file {Path}.", _path);
        }
    }
}
