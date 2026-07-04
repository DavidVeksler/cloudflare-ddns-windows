using System.Collections.ObjectModel;
using System.Diagnostics;
using System.IO;
using System.Windows;
using System.Windows.Threading;
using CloudflareDdns.Gui.Infrastructure;
using CloudflareDdns.Gui.Models;
using CloudflareDdns.Gui.Services;

namespace CloudflareDdns.Gui.ViewModels;

/// <summary>
/// Single view model backing the whole control panel: dashboard state, the configuration editor,
/// the live log, and Windows-service management. It drives real syncs through <see cref="DdnsEngine"/>.
/// </summary>
public sealed class MainViewModel : ObservableObject
{
    private const int MaxLogLines = 2000;

    private readonly Dispatcher _ui;
    private readonly ObservableLogSink _sink;
    private readonly DdnsEngine _engine;
    private readonly WindowsServiceController _service;
    private LocalConfigStore _config;

    public MainViewModel()
    {
        _ui = Application.Current?.Dispatcher ?? Dispatcher.CurrentDispatcher;
        _sink = new ObservableLogSink();
        _engine = new DdnsEngine(_sink);
        _service = new WindowsServiceController();
        _config = new LocalConfigStore(_engine.ConfigDir);

        _sink.Emitted += OnLogEmitted;

        WireCommands();
        LoadConfigFromDisk();
        LoadRawFiles();
        ConfigPath = _config.LocalFilePath;
        _ = RefreshAllAsync();
    }

    // ─────────────────────────────── Dashboard state ───────────────────────────────

    private string _publicIp = "—";
    public string PublicIp { get => _publicIp; set => SetProperty(ref _publicIp, value); }

    private string _cachedIp = "unknown";
    public string CachedIp { get => _cachedIp; set => SetProperty(ref _cachedIp, value); }

    private string _lastChecked = "never";
    public string LastChecked { get => _lastChecked; set => SetProperty(ref _lastChecked, value); }

    private string _lastUpdated = "never";
    public string LastUpdated { get => _lastUpdated; set => SetProperty(ref _lastUpdated, value); }

    private string _serviceStatus = "Unknown";
    public string ServiceStatus { get => _serviceStatus; set => SetProperty(ref _serviceStatus, value); }

    /// <summary>Lower-cased key the StatusToBrush converter maps to a color dot.</summary>
    private string _serviceStatusKey = "unknown";
    public string ServiceStatusKey { get => _serviceStatusKey; set => SetProperty(ref _serviceStatusKey, value); }

    private bool _serviceInstalled;
    public bool ServiceInstalled { get => _serviceInstalled; set => SetProperty(ref _serviceInstalled, value); }

    public ObservableCollection<HostRecordStatus> Hosts { get; } = new();

    private int _matchCount;
    public int MatchCount { get => _matchCount; set => SetProperty(ref _matchCount, value); }

    private int _issueCount;
    public int IssueCount { get => _issueCount; set => SetProperty(ref _issueCount, value); }

    private string _dashboardHint = "";
    public string DashboardHint { get => _dashboardHint; set => SetProperty(ref _dashboardHint, value); }

    // ─────────────────────────────── Global status / busy ───────────────────────────────

    private bool _isBusy;
    public bool IsBusy { get => _isBusy; set => SetProperty(ref _isBusy, value); }

    private string _busyText = "";
    public string BusyText { get => _busyText; set => SetProperty(ref _busyText, value); }

    private string _statusMessage = "Ready.";
    public string StatusMessage { get => _statusMessage; set => SetProperty(ref _statusMessage, value); }

    private string _statusKey = "info";
    public string StatusKey { get => _statusKey; set => SetProperty(ref _statusKey, value); }

    private string _configPath = "";
    public string ConfigPath { get => _configPath; set => SetProperty(ref _configPath, value); }

    // ─────────────────────────────── Live log ───────────────────────────────

    public ObservableCollection<LogEntry> Logs { get; } = new();

    // ─────────────────────────────── Config editor ───────────────────────────────

    private string _apiToken = "";
    public string ApiToken { get => _apiToken; set { if (SetProperty(ref _apiToken, value)) OnPropertyChanged(nameof(TokenSummary)); } }

    public string TokenSummary
    {
        get
        {
            if (string.IsNullOrWhiteSpace(ApiToken)) return "No token set";
            if (ApiToken.Contains("PUT-YOUR", StringComparison.OrdinalIgnoreCase)) return "Placeholder token";
            var tail = ApiToken.Length > 4 ? ApiToken[^4..] : ApiToken;
            return $"Token set (…{tail}, {ApiToken.Length} chars)";
        }
    }

    private bool _showToken;
    public bool ShowToken { get => _showToken; set => SetProperty(ref _showToken, value); }

    public string[] HostnameSources { get; } = { "Iis", "Static", "Both" };

    private string _hostnameSource = "Iis";
    public string HostnameSource { get => _hostnameSource; set => SetProperty(ref _hostnameSource, value); }

    private string _interval = "01:00:00";
    public string Interval { get => _interval; set => SetProperty(ref _interval, value); }

    private int _recordTtl = 300;
    public int RecordTtl { get => _recordTtl; set => SetProperty(ref _recordTtl, value); }

    private bool _proxied;
    public bool Proxied { get => _proxied; set => SetProperty(ref _proxied, value); }

    private bool _createIfMissing;
    public bool CreateIfMissing { get => _createIfMissing; set => SetProperty(ref _createIfMissing, value); }

    private bool _dryRun;
    public bool DryRun { get => _dryRun; set => SetProperty(ref _dryRun, value); }

    private string _stateFile = AppPaths.DefaultStateFile;
    public string StateFile { get => _stateFile; set => SetProperty(ref _stateFile, value); }

    public ObservableCollection<EditableString> Hostnames { get; } = new();
    public ObservableCollection<EditableString> ExcludeHostnames { get; } = new();
    public ObservableCollection<EditableString> IpProviders { get; } = new();

    private string _tokenTestResult = "";
    public string TokenTestResult { get => _tokenTestResult; set => SetProperty(ref _tokenTestResult, value); }

    private string _tokenTestKey = "info";
    public string TokenTestKey { get => _tokenTestKey; set => SetProperty(ref _tokenTestKey, value); }

    private bool _configDirty;
    public bool ConfigDirty { get => _configDirty; set => SetProperty(ref _configDirty, value); }

    // ─────────────────────────────── Raw JSON editors ───────────────────────────────

    private string _rawLocalJson = "";
    public string RawLocalJson { get => _rawLocalJson; set => SetProperty(ref _rawLocalJson, value); }

    private string _rawBaseJson = "";
    public string RawBaseJson { get => _rawBaseJson; set => SetProperty(ref _rawBaseJson, value); }

    public string LocalFilePath => _config.LocalFilePath;
    public string BaseFilePath => _config.BaseFilePath;

    // ─────────────────────────────── Commands ───────────────────────────────

    public AsyncRelayCommand RefreshCommand { get; private set; } = null!;
    public AsyncRelayCommand CheckIpCommand { get; private set; } = null!;
    public AsyncRelayCommand SyncNowCommand { get; private set; } = null!;
    public AsyncRelayCommand DryRunCommand { get; private set; } = null!;
    public AsyncRelayCommand TestTokenCommand { get; private set; } = null!;
    public AsyncRelayCommand CreateMissingCommand { get; private set; } = null!;

    public RelayCommand SaveConfigCommand { get; private set; } = null!;
    public RelayCommand ReloadConfigCommand { get; private set; } = null!;
    public RelayCommand AddHostnameCommand { get; private set; } = null!;
    public RelayCommand RemoveHostnameCommand { get; private set; } = null!;
    public RelayCommand AddExcludeCommand { get; private set; } = null!;
    public RelayCommand RemoveExcludeCommand { get; private set; } = null!;
    public RelayCommand AddProviderCommand { get; private set; } = null!;
    public RelayCommand RemoveProviderCommand { get; private set; } = null!;

    public RelayCommand SaveRawLocalCommand { get; private set; } = null!;
    public RelayCommand SaveRawBaseCommand { get; private set; } = null!;
    public RelayCommand ReloadRawCommand { get; private set; } = null!;

    public AsyncRelayCommand StartServiceCommand { get; private set; } = null!;
    public AsyncRelayCommand StopServiceCommand { get; private set; } = null!;
    public AsyncRelayCommand RestartServiceCommand { get; private set; } = null!;
    public AsyncRelayCommand InstallServiceCommand { get; private set; } = null!;
    public AsyncRelayCommand UninstallServiceCommand { get; private set; } = null!;

    public RelayCommand OpenLogsFolderCommand { get; private set; } = null!;
    public RelayCommand OpenConfigFolderCommand { get; private set; } = null!;
    public RelayCommand OpenStateFileCommand { get; private set; } = null!;
    public RelayCommand ClearLogCommand { get; private set; } = null!;
    public RelayCommand CopyIpCommand { get; private set; } = null!;
    public RelayCommand OpenHostnameCommand { get; private set; } = null!;

    private void WireCommands()
    {
        RefreshCommand = new AsyncRelayCommand(RefreshAllAsync);
        CheckIpCommand = new AsyncRelayCommand(CheckIpAsync);
        SyncNowCommand = new AsyncRelayCommand(() => RunSyncAsync(dryRun: false));
        DryRunCommand = new AsyncRelayCommand(() => RunSyncAsync(dryRun: true));
        TestTokenCommand = new AsyncRelayCommand(TestTokenAsync);
        CreateMissingCommand = new AsyncRelayCommand(CreateMissingRecordsAsync);

        SaveConfigCommand = new RelayCommand(SaveConfig);
        ReloadConfigCommand = new RelayCommand(() => { LoadConfigFromDisk(); LoadRawFiles(); SetStatus("Reloaded config from disk.", "info"); });
        AddHostnameCommand = new RelayCommand(() => AddRow(Hostnames));
        RemoveHostnameCommand = new RelayCommand(p => RemoveRow(Hostnames, p));
        AddExcludeCommand = new RelayCommand(() => AddRow(ExcludeHostnames));
        RemoveExcludeCommand = new RelayCommand(p => RemoveRow(ExcludeHostnames, p));
        AddProviderCommand = new RelayCommand(() => AddRow(IpProviders));
        RemoveProviderCommand = new RelayCommand(p => RemoveRow(IpProviders, p));

        SaveRawLocalCommand = new RelayCommand(() => SaveRaw(_config.LocalFilePath, RawLocalJson));
        SaveRawBaseCommand = new RelayCommand(() => SaveRaw(_config.BaseFilePath, RawBaseJson));
        ReloadRawCommand = new RelayCommand(LoadRawFiles);

        StartServiceCommand = new AsyncRelayCommand(() => ServiceActionAsync(_service.StartAsync, "Starting service"));
        StopServiceCommand = new AsyncRelayCommand(() => ServiceActionAsync(_service.StopAsync, "Stopping service"));
        RestartServiceCommand = new AsyncRelayCommand(() => ServiceActionAsync(_service.RestartAsync, "Restarting service"));
        InstallServiceCommand = new AsyncRelayCommand(() => ServiceActionAsync(_service.InstallAsync, "Installing service"));
        UninstallServiceCommand = new AsyncRelayCommand(() => ServiceActionAsync(_service.UninstallAsync, "Uninstalling service"));

        OpenLogsFolderCommand = new RelayCommand(() => OpenInExplorer(AppPaths.LogsDir));
        OpenConfigFolderCommand = new RelayCommand(() => OpenInExplorer(_config.ConfigDir));
        OpenStateFileCommand = new RelayCommand(() => OpenInExplorer(Path.GetDirectoryName(StateFile) ?? AppPaths.DataDir));
        ClearLogCommand = new RelayCommand(() => Logs.Clear());
        CopyIpCommand = new RelayCommand(() =>
        {
            if (!string.IsNullOrWhiteSpace(PublicIp) && PublicIp != "—")
            {
                try { Clipboard.SetText(PublicIp); SetStatus($"Copied {PublicIp} to clipboard.", "ok"); }
                catch { /* clipboard may be locked by another app */ }
            }
        });
        OpenHostnameCommand = new RelayCommand(p => OpenHostname(p as string));
    }

    // ─────────────────────────────── Operations ───────────────────────────────

    private async Task RefreshAllAsync()
    {
        RefreshServiceStatus();
        await RefreshDashboardAsync();
    }

    private void RefreshServiceStatus()
    {
        var state = _service.GetState();
        ServiceInstalled = state != ServiceState.NotInstalled;
        (ServiceStatus, ServiceStatusKey) = state switch
        {
            ServiceState.Running => ("Running", "running"),
            ServiceState.Stopped => ("Stopped", "stopped"),
            ServiceState.StartPending => ("Starting…", "pending"),
            ServiceState.StopPending => ("Stopping…", "pending"),
            ServiceState.Paused => ("Paused", "warning"),
            ServiceState.NotInstalled => ("Not installed", "notinstalled"),
            _ => ("Unknown", "unknown")
        };
    }

    private async Task RefreshDashboardAsync()
    {
        await RunBusy("Reading Cloudflare records", async () =>
        {
            var result = await Task.Run(() => _engine.InspectAsync(CancellationToken.None));

            PublicIp = result.PublicIp ?? "unavailable";
            CachedIp = result.State.LastIp ?? "unknown";
            LastChecked = Format(result.State.LastCheckedUtc);
            LastUpdated = Format(result.State.LastUpdatedUtc);

            Hosts.Clear();
            foreach (var h in result.Hosts) Hosts.Add(h);
            MatchCount = result.Hosts.Count(h => h.Status == "Match");
            IssueCount = result.Hosts.Count(h => h.Status is "Mismatch" or "Missing" or "Error");
            DashboardHint = result.Error ?? "";

            if (result.Error is not null) SetStatus(result.Error, "warning");
            else SetStatus($"{Hosts.Count} hostname(s): {MatchCount} up to date, {IssueCount} need attention.",
                IssueCount == 0 ? "ok" : "warning");
        });
    }

    private async Task CheckIpAsync()
    {
        await RunBusy("Resolving public IP", async () =>
        {
            var ip = await Task.Run(() => _engine.GetPublicIpAsync(CancellationToken.None));
            PublicIp = ip ?? "unavailable";
            SetStatus(ip is null ? "Could not resolve public IP." : $"Public IP is {ip}.", ip is null ? "error" : "ok");
        });
    }

    private async Task RunSyncAsync(bool dryRun)
    {
        var label = dryRun ? "Dry-run preview" : "Syncing to Cloudflare";
        await RunBusy(label, async () =>
        {
            try
            {
                await Task.Run(() => _engine.RunSyncAsync(dryRun, CancellationToken.None));
                SetStatus(dryRun ? "Dry-run complete — see the log for what would change." : "Sync complete.",
                    "ok");
            }
            catch (Exception ex)
            {
                SetStatus($"{label} failed: {ex.Message}", "error");
            }
        });
        RefreshServiceStatus();
        await RefreshDashboardAsync();
    }

    private async Task CreateMissingRecordsAsync()
    {
        await RunBusy("Creating missing A records", async () =>
        {
            try
            {
                var results = await Task.Run(() => _engine.CreateMissingRecordsAsync(CancellationToken.None));
                var created = results.Count(r => r.Created);
                SetStatus(created == 0
                        ? "No missing records to create — everything already has an A record."
                        : $"Created {created} A record(s).",
                    "ok");
            }
            catch (Exception ex)
            {
                SetStatus($"Create missing records failed: {ex.Message}", "error");
            }
        });
        await RefreshDashboardAsync();
    }

    private async Task TestTokenAsync()
    {
        // Persist the token first so the test uses exactly what the service will.
        SaveConfig(silent: true);
        await RunBusy("Testing API token", async () =>
        {
            try
            {
                var zones = await Task.Run(() => _engine.TestTokenAsync(CancellationToken.None));
                TokenTestKey = "ok";
                TokenTestResult = zones.Count == 0
                    ? "Token works, but it can't see any zones. Check its zone scope."
                    : $"Token OK — {zones.Count} zone(s) visible: {string.Join(", ", zones.Take(8))}" +
                      (zones.Count > 8 ? ", …" : "");
                SetStatus("API token verified.", "ok");
            }
            catch (Exception ex)
            {
                TokenTestKey = "error";
                TokenTestResult = ex.Message;
                SetStatus("API token test failed.", "error");
            }
        });
    }

    private async Task ServiceActionAsync(Func<Task<ServiceActionResult>> action, string label)
    {
        await RunBusy(label, async () =>
        {
            var result = await action();
            SetStatus(result.Message, result.Ok ? "ok" : "error");
        });
        // Give the SCM a moment, then reflect the new state.
        await Task.Delay(700);
        RefreshServiceStatus();
        ConfigPath = _config.LocalFilePath;
    }

    // ─────────────────────────────── Config load/save ───────────────────────────────

    private void LoadConfigFromDisk()
    {
        try
        {
            var m = _config.Load();
            ApiToken = m.ApiToken;
            HostnameSource = NormalizeSource(m.HostnameSource);
            Interval = m.Interval;
            RecordTtl = m.RecordTtl;
            Proxied = m.Proxied;
            CreateIfMissing = m.CreateIfMissing;
            DryRun = m.DryRun;
            StateFile = m.StateFile;
            Fill(Hostnames, m.Hostnames);
            Fill(ExcludeHostnames, m.ExcludeHostnames);
            Fill(IpProviders, m.IpProviders.Count > 0 ? m.IpProviders : LocalConfigStore.DefaultIpProviders());
            ConfigDirty = false;
            OnPropertyChanged(nameof(TokenSummary));
        }
        catch (Exception ex)
        {
            SetStatus($"Could not load config: {ex.Message}", "error");
        }
    }

    private void SaveConfig() => SaveConfig(silent: false);

    private void SaveConfig(bool silent)
    {
        try
        {
            var m = new ConfigModel
            {
                ApiToken = ApiToken?.Trim() ?? "",
                HostnameSource = HostnameSource,
                Interval = Interval,
                RecordTtl = RecordTtl,
                Proxied = Proxied,
                CreateIfMissing = CreateIfMissing,
                DryRun = DryRun,
                StateFile = StateFile,
                Hostnames = Hostnames.Select(x => x.Value).Where(NotBlank).ToList(),
                ExcludeHostnames = ExcludeHostnames.Select(x => x.Value).Where(NotBlank).ToList(),
                IpProviders = IpProviders.Select(x => x.Value).Where(NotBlank).ToList()
            };
            _config.Save(m);
            ConfigDirty = false;
            LoadRawFiles();
            ConfigPath = _config.LocalFilePath;
            if (!silent)
                SetStatus($"Saved config to {_config.LocalFilePath}. Restart the service to apply.", "ok");
        }
        catch (UnauthorizedAccessException)
        {
            SetStatus($"Access denied writing {_config.LocalFilePath}. " +
                      "The config lives in a protected folder — run the panel as admin, or edit a local copy.", "error");
        }
        catch (Exception ex)
        {
            SetStatus($"Could not save config: {ex.Message}", "error");
        }
    }

    private void LoadRawFiles()
    {
        try { RawLocalJson = _config.ReadRaw(_config.LocalFilePath); } catch { RawLocalJson = ""; }
        try { RawBaseJson = _config.ReadRaw(_config.BaseFilePath); } catch { RawBaseJson = ""; }
        OnPropertyChanged(nameof(LocalFilePath));
        OnPropertyChanged(nameof(BaseFilePath));
    }

    private void SaveRaw(string path, string json)
    {
        try
        {
            _config.WriteRaw(path, json);
            SetStatus($"Saved {Path.GetFileName(path)}.", "ok");
            LoadConfigFromDisk();
        }
        catch (System.Text.Json.JsonException jex)
        {
            SetStatus($"Invalid JSON — not saved: {jex.Message}", "error");
        }
        catch (Exception ex)
        {
            SetStatus($"Could not save {Path.GetFileName(path)}: {ex.Message}", "error");
        }
    }

    // ─────────────────────────────── Helpers ───────────────────────────────

    private void OnLogEmitted(LogEntry entry)
    {
        // Marshal to the UI thread; the sink fires from background sync tasks.
        _ui.BeginInvoke(() =>
        {
            Logs.Add(entry);
            while (Logs.Count > MaxLogLines) Logs.RemoveAt(0);
        });
    }

    private async Task RunBusy(string label, Func<Task> work)
    {
        IsBusy = true;
        BusyText = label + "…";
        try { await work(); }
        catch (Exception ex) { SetStatus($"{label} failed: {ex.Message}", "error"); }
        finally { IsBusy = false; BusyText = ""; }
    }

    private void SetStatus(string message, string key)
    {
        StatusMessage = message;
        StatusKey = key;
    }

    private static void AddRow(ObservableCollection<EditableString> list) => list.Add(new EditableString(""));

    private static void RemoveRow(ObservableCollection<EditableString> list, object? item)
    {
        if (item is EditableString es) list.Remove(es);
    }

    private static void Fill(ObservableCollection<EditableString> list, IEnumerable<string> values)
    {
        list.Clear();
        foreach (var v in values) list.Add(new EditableString(v));
    }

    private static bool NotBlank(string s) => !string.IsNullOrWhiteSpace(s);

    private static string NormalizeSource(string s) =>
        s.Equals("Static", StringComparison.OrdinalIgnoreCase) ? "Static" :
        s.Equals("Both", StringComparison.OrdinalIgnoreCase) ? "Both" : "Iis";

    private static string Format(DateTimeOffset? ts) =>
        ts is null ? "never" : ts.Value.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss");

    private void OpenHostname(string? hostname)
    {
        if (string.IsNullOrWhiteSpace(hostname)) return;
        try
        {
            Process.Start(new ProcessStartInfo($"https://{hostname}") { UseShellExecute = true });
        }
        catch (Exception ex)
        {
            SetStatus($"Could not open {hostname}: {ex.Message}", "error");
        }
    }

    private void OpenInExplorer(string path)
    {
        try
        {
            if (Directory.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"\"{path}\"") { UseShellExecute = true });
            else if (File.Exists(path))
                Process.Start(new ProcessStartInfo("explorer.exe", $"/select,\"{path}\"") { UseShellExecute = true });
            else
                SetStatus($"Path does not exist yet: {path}", "warning");
        }
        catch (Exception ex)
        {
            SetStatus($"Could not open {path}: {ex.Message}", "error");
        }
    }
}
