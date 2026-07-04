using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.ServiceProcess;

namespace CloudflareDdns.Gui.Services;

public enum ServiceState { NotInstalled, Stopped, Running, StartPending, StopPending, Paused, Unknown }

/// <summary>Result of an elevated control action.</summary>
public sealed record ServiceActionResult(bool Ok, string Message);

/// <summary>
/// Thin wrapper around the "CloudflareDdns" Windows Service. Status is read without elevation;
/// start/stop/restart/install/uninstall are performed through a per-action UAC prompt so the whole
/// control panel doesn't have to run as admin.
/// </summary>
public sealed class WindowsServiceController
{
    private readonly string _serviceName;

    public WindowsServiceController(string serviceName = AppPaths.ServiceName) => _serviceName = serviceName;

    public ServiceState GetState()
    {
        try
        {
            using var sc = new ServiceController(_serviceName);
            return sc.Status switch
            {
                ServiceControllerStatus.Running => ServiceState.Running,
                ServiceControllerStatus.Stopped => ServiceState.Stopped,
                ServiceControllerStatus.StartPending => ServiceState.StartPending,
                ServiceControllerStatus.StopPending => ServiceState.StopPending,
                ServiceControllerStatus.Paused => ServiceState.Paused,
                _ => ServiceState.Unknown
            };
        }
        catch (InvalidOperationException)
        {
            // ServiceController throws this when the named service doesn't exist.
            return ServiceState.NotInstalled;
        }
        catch
        {
            return ServiceState.Unknown;
        }
    }

    public bool IsInstalled => GetState() != ServiceState.NotInstalled;

    public Task<ServiceActionResult> StartAsync() =>
        RunElevatedPowerShellAsync($"Start-Service -Name '{_serviceName}'", "start the service");

    public Task<ServiceActionResult> StopAsync() =>
        RunElevatedPowerShellAsync($"Stop-Service -Name '{_serviceName}' -Force", "stop the service");

    public Task<ServiceActionResult> RestartAsync() =>
        RunElevatedPowerShellAsync($"Restart-Service -Name '{_serviceName}' -Force", "restart the service");

    /// <summary>Runs install.ps1 elevated to publish + register + start the service.</summary>
    public Task<ServiceActionResult> InstallAsync()
    {
        var script = AppPaths.FindScript("install.ps1");
        if (script is null)
            return Task.FromResult(new ServiceActionResult(false, "install.ps1 not found next to the app or in the repo."));
        return RunElevatedScriptAsync(script, "install the service");
    }

    /// <summary>Runs uninstall.ps1 elevated to stop + remove the service.</summary>
    public Task<ServiceActionResult> UninstallAsync()
    {
        var script = AppPaths.FindScript("uninstall.ps1");
        if (script is null)
            return Task.FromResult(new ServiceActionResult(false, "uninstall.ps1 not found next to the app or in the repo."));
        return RunElevatedScriptAsync(script, "uninstall the service");
    }

    private static Task<ServiceActionResult> RunElevatedPowerShellAsync(string command, string what)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -Command \"{command}\"",
            UseShellExecute = true,
            Verb = "runas",
            WindowStyle = ProcessWindowStyle.Hidden
        };
        return RunAsync(psi, what);
    }

    private static Task<ServiceActionResult> RunElevatedScriptAsync(string scriptPath, string what)
    {
        var psi = new ProcessStartInfo
        {
            FileName = "powershell.exe",
            Arguments = $"-NoProfile -ExecutionPolicy Bypass -File \"{scriptPath}\"",
            UseShellExecute = true,
            Verb = "runas",
            WorkingDirectory = Path.GetDirectoryName(scriptPath) ?? Environment.CurrentDirectory
            // Visible window so the user can watch publish/install progress.
        };
        return RunAsync(psi, what);
    }

    private static async Task<ServiceActionResult> RunAsync(ProcessStartInfo psi, string what)
    {
        try
        {
            using var proc = Process.Start(psi);
            if (proc is null)
                return new ServiceActionResult(false, $"Could not start the elevated process to {what}.");

            await proc.WaitForExitAsync();
            return proc.ExitCode == 0
                ? new ServiceActionResult(true, $"Succeeded: {what}.")
                : new ServiceActionResult(false, $"The elevated command to {what} exited with code {proc.ExitCode}.");
        }
        catch (Win32Exception ex) when (ex.NativeErrorCode == 1223)
        {
            // ERROR_CANCELLED — user declined the UAC prompt.
            return new ServiceActionResult(false, "Elevation was cancelled (UAC prompt declined).");
        }
        catch (Exception ex)
        {
            return new ServiceActionResult(false, $"Failed to {what}: {ex.Message}");
        }
    }
}
