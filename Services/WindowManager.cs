using System.Diagnostics;
using FiveMMcp.Native;

namespace FiveMMcp.Services;

public sealed record WindowInfo(
    bool Found,
    nint Handle,
    int ProcessId,
    string ProcessName,
    string Title,
    bool IsForeground,
    bool IsMinimized,
    int X,
    int Y,
    int Width,
    int Height);

public sealed record ScreenRect(int Left, int Top, int Right, int Bottom);

/// <summary>
/// Locates and focuses the FiveM game window. The game renders in
/// FiveM_b&lt;build&gt;_GTAProcess; the launcher process ("FiveM") has no game window.
/// </summary>
public sealed class WindowManager {
    // Whatever the user was looking at before a tool first stole focus, so it can be
    // handed back when the agent is done driving the game.
    private nint windowBeforeFocusSteal;

    public WindowInfo GetWindow() {
        var proc = FindGameProcess();
        if (proc is null) {
            return new WindowInfo(false, 0, 0, "", "", false, false, 0, 0, 0, 0);
        }

        var hWnd = proc.MainWindowHandle;
        User32.GetWindowRect(hWnd, out var rect);
        var minimized = User32.IsIconic(hWnd);

        return new WindowInfo(
            Found: true,
            Handle: hWnd,
            ProcessId: proc.Id,
            ProcessName: proc.ProcessName,
            Title: proc.MainWindowTitle,
            IsForeground: User32.GetForegroundWindow() == hWnd,
            IsMinimized: minimized,
            X: rect.Left,
            Y: rect.Top,
            Width: rect.Right - rect.Left,
            Height: rect.Bottom - rect.Top);
    }

    private static Process? FindGameProcess() {
        // Preferred: the versioned game process, e.g. FiveM_b3751_GTAProcess.
        var byName = Process.GetProcesses()
            .Where(p => p.ProcessName.StartsWith("FiveM_b", StringComparison.OrdinalIgnoreCase)
                        && p.ProcessName.EndsWith("GTAProcess", StringComparison.OrdinalIgnoreCase))
            .FirstOrDefault(p => p.MainWindowHandle != 0);
        if (byName is not null) {
            return byName;
        }

        // Fallback: any process whose main window looks like the FiveM game window.
        return Process.GetProcesses()
            .FirstOrDefault(p => p.MainWindowHandle != 0
                                 && p.MainWindowTitle.Contains("FiveM", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// Brings the game window to the foreground. Returns null on success,
    /// or a human-readable error describing what went wrong.
    /// </summary>
    public string? EnsureFocused() {
        var info = GetWindow();
        if (!info.Found) {
            return "FiveM game window not found - is the client running and past the launcher?";
        }

        if (info.IsForeground && !info.IsMinimized) {
            return null;
        }

        var current = User32.GetForegroundWindow();
        if (current != 0 && current != info.Handle) {
            windowBeforeFocusSteal = current;
        }

        if (info.IsMinimized) {
            User32.ShowWindow(info.Handle, User32.SW_RESTORE);
        }

        if (User32.SetForegroundWindow(info.Handle)) {
            Thread.Sleep(120);
            if (User32.GetForegroundWindow() == info.Handle) {
                return null;
            }
        }

        ForceForeground(info.Handle);
        Thread.Sleep(150);
        return User32.GetForegroundWindow() == info.Handle
            ? null
            : "Could not bring the FiveM window to the foreground (Windows foreground lock). Click the game window once and retry.";
    }

    /// <summary>
    /// Windows only lets the foreground process change the foreground window, so attach
    /// to the target's input queue for the duration of the call to get permission.
    /// </summary>
    private static void ForceForeground(nint hWnd) {
        var ourThread = User32.GetCurrentThreadId();
        var targetThread = User32.GetWindowThreadProcessId(hWnd, out _);
        var foregroundThread = User32.GetWindowThreadProcessId(User32.GetForegroundWindow(), out _);

        // Permission comes from sharing an input queue with the thread that currently
        // owns the foreground; the target's queue alone is not enough when the window
        // being demoted belongs to someone else.
        var attached = new List<uint>();

        foreach (var thread in new[] { foregroundThread, targetThread }) {
            if (thread != 0 && thread != ourThread && !attached.Contains(thread) &&
                User32.AttachThreadInput(ourThread, thread, true)) {
                attached.Add(thread);
            }
        }

        try {
            User32.ShowWindow(hWnd, User32.SW_RESTORE);
            User32.BringWindowToTop(hWnd);
            User32.SetForegroundWindow(hWnd);
        } finally {
            foreach (var thread in attached) {
                User32.AttachThreadInput(ourThread, thread, false);
            }
        }
    }

    /// <summary>
    /// Hands the foreground back to whatever the user was on before a tool stole focus,
    /// so they get their terminal back when the agent is done with the game.
    /// </summary>
    public string RestorePreviousFocus() {
        var target = windowBeforeFocusSteal;
        windowBeforeFocusSteal = 0;

        if (target != 0 && User32.IsWindowVisible(target)) {
            ForceForeground(target);
            Thread.Sleep(150);

            if (User32.GetForegroundWindow() == target) {
                return "Focus returned to the window that had it before FiveM was brought forward.";
            }
        }

        // Either nothing was recorded, that window is gone, or Windows refused the
        // switch. Getting the game off the screen is the point, so minimise instead.
        var minimized = Minimize();

        return minimized.StartsWith("FiveM window minimised", StringComparison.Ordinal)
            ? "Could not restore the previous window, so FiveM was minimised to get it out of the way."
            : minimized;
    }

    /// <summary>Minimises the game so it stops covering everything else.</summary>
    public string Minimize() {
        var info = GetWindow();

        if (!info.Found) {
            return "FiveM game window not found - is the client running?";
        }

        User32.ShowWindow(info.Handle, User32.SW_MINIMIZE);

        return "FiveM window minimised.";
    }

    /// <summary>Converts window-relative client coordinates to absolute screen coordinates.</summary>
    public (int X, int Y)? ClientToScreen(WindowInfo info, int x, int y) {
        if (!info.Found) {
            return null;
        }

        var pt = new User32.POINT { X = x, Y = y };
        return User32.ClientToScreen(info.Handle, ref pt) ? (pt.X, pt.Y) : null;
    }

    /// <summary>The window's client area expressed in absolute screen coordinates.</summary>
    public ScreenRect? GetClientRectOnScreen(WindowInfo info) {
        if (!info.Found || !User32.GetClientRect(info.Handle, out var client)) {
            return null;
        }

        var topLeft = new User32.POINT { X = client.Left, Y = client.Top };
        if (!User32.ClientToScreen(info.Handle, ref topLeft)) {
            return null;
        }

        return new ScreenRect(
            topLeft.X,
            topLeft.Y,
            topLeft.X + (client.Right - client.Left),
            topLeft.Y + (client.Bottom - client.Top));
    }
}
