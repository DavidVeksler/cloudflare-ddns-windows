using CloudflareDdns.Gui.Infrastructure;

namespace CloudflareDdns.Gui.Models;

/// <summary>A single rendered log line for the live activity panel.</summary>
public sealed class LogEntry
{
    public DateTimeOffset Timestamp { get; init; }
    public string Level { get; init; } = "Information";
    public string Message { get; init; } = "";
    public string? Exception { get; init; }

    public string Time => Timestamp.ToLocalTime().ToString("HH:mm:ss");
    public string ShortLevel => Level.Length >= 3 ? Level[..3].ToUpperInvariant() : Level.ToUpperInvariant();
    public string Display => Exception is null ? Message : $"{Message}\n{Exception}";
}

/// <summary>A single editable string row (hostname / exclude / IP provider) for the config lists.</summary>
public sealed class EditableString : ObservableObject
{
    private string _value = "";
    public string Value { get => _value; set => SetProperty(ref _value, value); }
    public EditableString() { }
    public EditableString(string value) => _value = value;
}

/// <summary>Live status of one managed hostname on the dashboard grid.</summary>
public sealed class HostRecordStatus : ObservableObject
{
    private string _status = "Unknown";
    private string _detail = "";
    private string _zone = "";
    private string _currentIp = "";
    private bool _proxied;

    public string Hostname { get; init; } = "";

    /// <summary>One of: Match, Mismatch, Missing, Error, Unknown — drives the colored dot.</summary>
    public string Status { get => _status; set => SetProperty(ref _status, value); }
    public string Detail { get => _detail; set => SetProperty(ref _detail, value); }
    public string Zone { get => _zone; set => SetProperty(ref _zone, value); }
    public string CurrentIp { get => _currentIp; set => SetProperty(ref _currentIp, value); }
    public bool Proxied { get => _proxied; set => SetProperty(ref _proxied, value); }

    public string ProxiedText => Proxied ? "Proxied" : "DNS only";
}
