using System.Diagnostics;
using System.IO;

namespace LithicBackup.Services;

/// <summary>
/// Manages the LithicBackup Worker Windows Service — install, uninstall,
/// start, stop, and status queries. Uses <c>sc.exe</c> so no extra NuGet
/// packages are needed.
/// </summary>
internal static class WorkerServiceHelper
{
    public const string ServiceName = "LithicBackup";

    /// <summary>
    /// Find the Worker executable. Looks next to the running app first
    /// (deployed layout), then falls back to the sibling project build
    /// output (development layout).
    /// </summary>
    public static string? FindWorkerExe()
    {
        var appDir = AppContext.BaseDirectory;

        // Deployed: same directory.
        var sameDir = Path.Combine(appDir, "LithicBackup.Worker.exe");
        if (File.Exists(sameDir))
            return sameDir;

        // Development: sibling project output.
        var devPath = Path.GetFullPath(
            Path.Combine(appDir, "..", "..", "..", "..",
                "LithicBackup.Worker", "bin", "Debug", "net8.0-windows",
                "LithicBackup.Worker.exe"));
        if (File.Exists(devPath))
            return devPath;

        // Release build.
        var relPath = Path.GetFullPath(
            Path.Combine(appDir, "..", "..", "..", "..",
                "LithicBackup.Worker", "bin", "Release", "net8.0-windows",
                "LithicBackup.Worker.exe"));
        if (File.Exists(relPath))
            return relPath;

        return null;
    }

    /// <summary>
    /// Query the current state of the service.
    /// </summary>
    public static ServiceState GetStatus()
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = $"query {ServiceName}",
                RedirectStandardOutput = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            };

            using var proc = Process.Start(psi);
            if (proc is null) return ServiceState.Unknown;

            string output = proc.StandardOutput.ReadToEnd();
            proc.WaitForExit(5000);

            if (proc.ExitCode != 0)
                return ServiceState.NotInstalled;

            if (output.Contains("RUNNING", StringComparison.OrdinalIgnoreCase))
                return ServiceState.Running;
            if (output.Contains("STOPPED", StringComparison.OrdinalIgnoreCase))
                return ServiceState.Stopped;
            if (output.Contains("START_PENDING", StringComparison.OrdinalIgnoreCase))
                return ServiceState.StartPending;
            if (output.Contains("STOP_PENDING", StringComparison.OrdinalIgnoreCase))
                return ServiceState.StopPending;

            return ServiceState.Stopped;
        }
        catch
        {
            return ServiceState.Unknown;
        }
    }

    /// <summary>
    /// Install the service. Requires elevation (UAC prompt).
    /// </summary>
    public static bool Install(string workerExePath)
    {
        return RunElevated("create", $"{ServiceName} binPath=\"{workerExePath}\" start=auto DisplayName=\"LithicBackup Worker\"");
    }

    /// <summary>
    /// Uninstall the service. Requires elevation.
    /// </summary>
    public static bool Uninstall()
    {
        // Stop first (ignore failure if already stopped).
        RunElevated("stop", ServiceName);
        return RunElevated("delete", ServiceName);
    }

    /// <summary>
    /// Start the service. Requires elevation.
    /// </summary>
    public static bool Start()
    {
        return RunElevated("start", ServiceName);
    }

    /// <summary>
    /// Stop the service. Requires elevation.
    /// </summary>
    public static bool Stop()
    {
        return RunElevated("stop", ServiceName);
    }

    private static bool RunElevated(string scVerb, string scArgs)
    {
        try
        {
            var psi = new ProcessStartInfo
            {
                FileName = "sc.exe",
                Arguments = $"{scVerb} {scArgs}",
                Verb = "runas",
                UseShellExecute = true,
            };

            using var proc = Process.Start(psi);
            if (proc is null) return false;

            proc.WaitForExit(15000);
            return proc.ExitCode == 0;
        }
        catch
        {
            // User declined UAC, or other error.
            return false;
        }
    }
}

public enum ServiceState
{
    Unknown,
    NotInstalled,
    Stopped,
    Running,
    StartPending,
    StopPending,
}
