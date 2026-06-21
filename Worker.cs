using CloudflareDdns.Configuration;
using CloudflareDdns.Services;
using Microsoft.Extensions.Options;

namespace CloudflareDdns;

/// <summary>
/// Background service that runs a sync immediately on start, then once per configured
/// interval (default hourly). Each pass is isolated so a transient failure never kills
/// the service — it just retries on the next tick.
/// </summary>
public sealed class Worker : BackgroundService
{
    private readonly DdnsUpdater _updater;
    private readonly DdnsOptions _options;
    private readonly IHostApplicationLifetime _lifetime;
    private readonly ILogger<Worker> _log;

    public Worker(
        DdnsUpdater updater,
        IOptions<DdnsOptions> options,
        IHostApplicationLifetime lifetime,
        ILogger<Worker> log)
    {
        _updater = updater;
        _options = options.Value;
        _lifetime = lifetime;
        _log = log;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _log.LogInformation("CloudflareDdns started. Interval: {Interval}. Source: {Source}.",
            _options.Interval, _options.HostnameSource);

        // Run once at startup so a fresh deploy / boot updates immediately.
        await SafeRunAsync(stoppingToken);

        // Dry-run is a one-shot preview: do a single pass, then exit cleanly.
        if (_options.DryRun)
        {
            _log.LogInformation("[DRY RUN] Single preview pass complete; exiting.");
            _lifetime.StopApplication();
            return;
        }

        using var timer = new PeriodicTimer(_options.Interval);
        try
        {
            while (await timer.WaitForNextTickAsync(stoppingToken))
                await SafeRunAsync(stoppingToken);
        }
        catch (OperationCanceledException)
        {
            // Normal shutdown.
        }

        _log.LogInformation("CloudflareDdns stopping.");
    }

    private async Task SafeRunAsync(CancellationToken ct)
    {
        try
        {
            await _updater.RunOnceAsync(ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception ex)
        {
            // Never let a single bad run tear down the service.
            _log.LogError(ex, "Sync pass failed; will retry at the next interval.");
        }
    }
}
