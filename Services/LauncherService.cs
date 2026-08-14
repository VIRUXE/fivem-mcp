using System.Diagnostics;
using FiveMMcp.Native;

namespace FiveMMcp.Services;

/// <summary>
/// Starts the client. The FiveM installer registers the fivem:// URI scheme, so
/// "fivem://connect/host:port" launches straight into a server without menu navigation.
/// </summary>
public sealed class LauncherService(WindowManager windows) {
    public async Task<string> LaunchAsync(string? serverAddress, int waitSeconds, CancellationToken ct) {
        var existing = windows.GetWindow();
        if (existing.Found) {
            return $"FiveM is already running (pid {existing.ProcessId}, \"{existing.Title}\"). " +
                   "Use console_command \"connect <address>\" to switch servers.";
        }

        var uri = string.IsNullOrWhiteSpace(serverAddress)
            ? "fivem://"
            : $"fivem://connect/{serverAddress.Trim()}";

        Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });

        var deadline = DateTime.UtcNow.AddSeconds(Math.Clamp(waitSeconds, 0, 300));
        while (DateTime.UtcNow < deadline) {
            await Task.Delay(1000, ct);
            var info = windows.GetWindow();
            if (info.Found) {
                return $"Launched {uri}; game window is up (pid {info.ProcessId}, \"{info.Title}\"). " +
                       "Loading into the server can still take a while - poll read_log to follow progress.";
            }
        }

        return waitSeconds <= 0
            ? $"Launched {uri} (not waiting for the game window)."
            : $"Launched {uri}, but the game window did not appear within {waitSeconds}s. " +
              "The launcher/updater may still be working - check get_window_status again shortly.";
    }

    /// <summary>
    /// Closes the client. A graceful close asks the game window to shut down, which lets
    /// the client disconnect cleanly; force terminates the whole FiveM process family,
    /// which is what actually works when the game is hung.
    /// </summary>
    public async Task<string> QuitAsync(bool force, int waitSeconds, CancellationToken ct) {
        var info = windows.GetWindow();

        if (!info.Found && !AnyFiveMProcess()) {
            return "FiveM is not running.";
        }

        if (!force && info.Found) {
            User32.PostMessage(info.Handle, User32.WM_CLOSE, 0, 0);

            var deadline = DateTime.UtcNow.AddSeconds(Math.Clamp(waitSeconds, 1, 120));
            while (DateTime.UtcNow < deadline) {
                await Task.Delay(500, ct);

                if (!windows.GetWindow().Found) {
                    return "FiveM closed gracefully.";
                }
            }

            return $"FiveM did not close within {waitSeconds}s. Call quit_game again with force: true to terminate it.";
        }

        var killed = KillFiveMProcesses();

        return killed == 0
            ? "No FiveM processes found to terminate."
            : $"Terminated {killed} FiveM process(es).";
    }

    // Everything the client spawns is named FiveM or FiveM_*: the launcher/master,
    // the versioned game process, the CEF browser hosts, and the ROS helpers.
    private static IEnumerable<Process> FiveMProcesses() =>
        Process.GetProcesses().Where(p =>
            p.ProcessName.Equals("FiveM", StringComparison.OrdinalIgnoreCase) ||
            p.ProcessName.StartsWith("FiveM_", StringComparison.OrdinalIgnoreCase));

    private static bool AnyFiveMProcess() => FiveMProcesses().Any();

    private static int KillFiveMProcesses() {
        var killed = 0;

        // Game process first, so the master does not treat the exit as a crash and
        // pop the error reporter.
        foreach (var proc in FiveMProcesses().OrderByDescending(p => p.ProcessName.Contains("GTAProcess", StringComparison.OrdinalIgnoreCase))) {
            try {
                proc.Kill(entireProcessTree: true);
                killed++;
            } catch {
                // Already gone, or killed as part of another process's tree.
            }
        }

        return killed;
    }
}
