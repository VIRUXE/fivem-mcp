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

        // Two ways in, tried in order (FIVEM_LAUNCH=uri|explorer|auto picks one; auto is
        // the default):
        //
        //  uri      - hand the fivem:// link to the shell, the way a browser does. Needs the
        //             scheme registered. FiveM's launcher checks its *parent process* and
        //             refuses anything that is not Explorer or a browser ("This application
        //             should be launched directly from the shell or a web browser"); when
        //             the caller runs from an ordinary terminal the shell resolves the
        //             handler and this is enough, when it is refused set
        //             FIVEM_LAUNCH=explorer.
        //  explorer - start through explorer.exe so Explorer is the parent, with the link
        //             when the scheme is registered and the bare executable otherwise (the
        //             caller then connects from the menu with console_command). This is
        //             the fallback when the scheme is not registered or the uri way is
        //             refused, and it cannot be used from an elevated process, where
        //             explorer.exe hands the request to the unelevated desktop shell and
        //             the parent check sees a different session.
        var mode = (Environment.GetEnvironmentVariable("FIVEM_LAUNCH") ?? "auto").Trim().ToLowerInvariant();
        var exe = FindClientExecutable();
        var registered = SchemeRegistered();
        var how = uri;

        if (mode != "explorer" && registered) {
            try {
                Process.Start(new ProcessStartInfo(uri) { UseShellExecute = true });
                how = $"{uri} via the fivem:// scheme";
            } catch (Exception ex) when (mode == "auto") {
                registered = false; // fall through to explorer
                how = $"{uri} (shell refused the fivem:// scheme: {ex.Message}); ";
            }
        }
        if (mode == "explorer" || !registered) {
            var target = registered ? uri : (exe ?? throw new InvalidOperationException(
                "FiveM.exe not found (set FIVEM_EXECUTABLE) and the fivem:// scheme is not registered."));
            Process.Start(new ProcessStartInfo("explorer.exe") { UseShellExecute = false, ArgumentList = { target } });
            how = registered
                ? $"{uri} via explorer.exe"
                : $"{exe} via explorer.exe (fivem:// scheme not registered; run console_command \"connect {serverAddress}\" once the menu is up)";
        }
        uri = how;

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

    private static bool SchemeRegistered() {
        try {
            using var key = Microsoft.Win32.Registry.ClassesRoot.OpenSubKey(@"fivem\shell\open\command");
            return key?.GetValue(null) is string cmd && cmd.Length > 0;
        } catch {
            return false;
        }
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

    /// <summary>
    /// FiveM installs per-user, so the launcher normally sits in LocalAppData. Honour an
    /// explicit override for non-default installs.
    /// </summary>
    private static string? FindClientExecutable() {
        var configured = Environment.GetEnvironmentVariable("FIVEM_EXECUTABLE");

        if (!string.IsNullOrWhiteSpace(configured) && File.Exists(configured)) {
            return configured;
        }

        var candidate = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "FiveM", "FiveM.exe");

        return File.Exists(candidate) ? candidate : null;
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
