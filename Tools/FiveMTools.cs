using System.ComponentModel;
using FiveMMcp.Services;
using ModelContextProtocol.Protocol;
using ModelContextProtocol.Server;

namespace FiveMMcp.Tools;

[McpServerToolType]
public sealed class FiveMTools(
    WindowManager windows,
    InputService input,
    CaptureService capture,
    LogService logs,
    LauncherService launcher,
    RconService rcon,
    DevConService devcon,
    ConsoleTapService consoleTap) {
    [McpServerTool(Name = "launch"), Description(
        "Launches the FiveM client, optionally connecting straight to a server address " +
        "(host:port) via the fivem:// URI scheme. No-op if the client is already running.")]
    public async Task<string> Launch(
        [Description("Server address to connect to, e.g. \"127.0.0.1:30120\". Omit to open FiveM without connecting.")]
        string? serverAddress = null,
        [Description("Seconds to wait for the game window to appear. 0 returns immediately. Default 90.")]
        int waitSeconds = 90,
        CancellationToken cancellationToken = default)
        => await launcher.LaunchAsync(serverAddress, waitSeconds, cancellationToken);

    [McpServerTool(Name = "get_window_status"), Description(
        "Reports whether the FiveM game window exists, its title, size, position, and whether it is focused.")]
    public string GetWindowStatus() {
        var info = windows.GetWindow();
        if (!info.Found) {
            return "FiveM game window not found. The client is not running, or it is still on the launcher/updater screen.";
        }

        var state = info.IsMinimized ? "minimized" : info.IsForeground ? "focused" : "background";
        return $"FiveM window found: \"{info.Title}\" (process {info.ProcessName}, pid {info.ProcessId}), " +
               $"{info.Width}x{info.Height} at ({info.X},{info.Y}), currently {state}.";
    }

    [McpServerTool(Name = "quit_game"), Description(
        "Closes the FiveM client. Asks the window to close cleanly by default; force terminates every " +
        "FiveM process, which is what works when the game is hung.")]
    public async Task<string> QuitGame(
        [Description("Terminate the FiveM processes instead of asking the window to close. Default false.")]
        bool force = false,
        [Description("Seconds to wait for a graceful close before giving up. Default 15.")]
        int waitSeconds = 15,
        CancellationToken cancellationToken = default)
        => await launcher.QuitAsync(force, waitSeconds, cancellationToken);

    [McpServerTool(Name = "restore_focus"), Description(
        "Hands the foreground back to whatever window had focus before a tool brought FiveM forward. " +
        "Call this when finished driving the game so the user gets their own window back.")]
    public string RestoreFocus() => windows.RestorePreviousFocus();

    [McpServerTool(Name = "minimize_window"), Description(
        "Minimises the FiveM window so it stops covering the screen.")]
    public string MinimizeWindow() => windows.Minimize();

    [McpServerTool(Name = "rcon_command"), Description(
        "Runs a command on the FiveM *server* over RCON, with no need to touch the game client or its console. " +
        "Use for \"restart <resource>\", \"refresh\", \"status\", and companion-resource commands.")]
    public async Task<string> RconCommand(
        [Description("Server console command to run, e.g. \"restart mcp_bridge\".")] string command,
        [Description("Milliseconds to wait for the first response packet. Default 2000.")] int timeoutMs = 2000,
        CancellationToken cancellationToken = default) {
        try {
            var output = await rcon.ExecuteAsync(command, timeoutMs, cancellationToken);
            return string.IsNullOrWhiteSpace(output) ? "(command ran, no output)" : output;
        } catch (Exception ex) {
            return $"RCON error: {ex.Message}";
        }
    }

    [McpServerTool(Name = "get_position"), Description(
        "Reports the player's world position and heading as vec4(x, y, z, heading), plus interior and vehicle. " +
        "Requires the mcp_bridge companion resource, which registers the client-side mcp_position command.")]
    public async Task<string> GetPosition(CancellationToken cancellationToken = default) {
        try {
            var cursor = logs.Read(null, 1, null, 1).Cursor;
            await devcon.SendCommandAsync("mcp_position", cancellationToken);

            // The command prints on the game thread, so give it a frame or two to land.
            for (var attempt = 0; attempt < 10; attempt++) {
                await Task.Delay(150, cancellationToken);

                var since = logs.Read(cursor, 0, "mcp_position:", 20);
                if (since.Lines.Length > 0) {
                    return since.Lines[^1][(since.Lines[^1].IndexOf("mcp_position:", StringComparison.Ordinal))..];
                }
            }

            return "Sent mcp_position but saw no reply in the client log. Is mcp_bridge running on this server?";
        } catch (Exception ex) {
            return $"Error reading position: {ex.Message}";
        }
    }

    [McpServerTool(Name = "notify"), Description(
        "Shows a notification inside the game, so the player can see what the agent is doing. " +
        "Goes over the client's devcon socket, so it needs no RCON password and no special permissions. " +
        "Requires the mcp_bridge companion resource. Supports GTA colour codes like ~g~ and ~b~.")]
    public async Task<string> Notify(
        [Description("Message to show in-game.")] string message,
        [Description("Show it to every player on the server instead of just this client. Needs RCON. Default false.")]
        bool everyone = false,
        CancellationToken cancellationToken = default) {
        try {
            if (everyone) {
                var output = await rcon.ExecuteAsync($"mcp_notify_all {message}", 2000, cancellationToken);
                return string.IsNullOrWhiteSpace(output) ? $"Sent notification to everyone: {message}" : output;
            }

            await devcon.SendCommandAsync($"mcp_notify {message}", cancellationToken);
            return $"Sent notification: {message}";
        } catch (Exception ex) {
            return $"Error sending notification: {ex.Message}";
        }
    }

    [McpServerTool(Name = "focus_window"), Description(
        "Brings the FiveM window to the foreground. Input tools do this automatically; call it explicitly to verify focus.")]
    public string FocusWindow()
        => windows.EnsureFocused() ?? "FiveM window is focused.";

    [McpServerTool(Name = "screenshot"), Description(
        "Captures the FiveM window as a PNG image. Optionally crops to a window-relative region.")]
    public CallToolResult Screenshot(
        [Description("Left edge of the crop region, in window-relative pixels. Omit to capture the whole window.")]
        int? x = null,
        [Description("Top edge of the crop region, in window-relative pixels.")]
        int? y = null,
        [Description("Width of the crop region in pixels.")]
        int? width = null,
        [Description("Height of the crop region in pixels.")]
        int? height = null,
        [Description("Downscale so the image is at most this wide. Default 1280; use 0 for native resolution.")]
        int maxWidth = CaptureService.DefaultMaxWidth) {
        try {
            var (png, w, h) = capture.Capture(x, y, width, height, maxWidth);
            return new CallToolResult {
                Content =
                [
                    new TextContentBlock { Text = $"FiveM window capture, {w}x{h}." },
                    ImageContentBlock.FromBytes(png, "image/png"),
                ],
            };
        } catch (Exception ex) {
            return Error(ex.Message);
        }
    }

    [McpServerTool(Name = "press_key"), Description(
        "Presses and releases a key in the FiveM window. Accepts names like \"W\", \"F8\", \"Space\", \"Enter\", \"Esc\", \"LShift\", \"Left\".")]
    public string PressKey(
        [Description("Key name, e.g. \"F8\", \"W\", \"Enter\".")] string key,
        [Description("How long to hold the key down, in milliseconds. Default 60.")] int holdMs = 60) {
        if (!KeyMap.TryResolve(key, out var vk)) {
            return UnknownKey(key);
        }

        if (windows.EnsureFocused() is { } err) {
            return err;
        }

        input.PressKey(vk, holdMs);
        return $"Pressed {key} for {holdMs}ms.";
    }

    [McpServerTool(Name = "hold_key"), Description(
        "Holds a key down without releasing it - use for sustained movement (W to walk forward). Always pair with release_key.")]
    public string HoldKey([Description("Key name, e.g. \"W\", \"LShift\".")] string key) {
        if (!KeyMap.TryResolve(key, out var vk)) {
            return UnknownKey(key);
        }

        if (windows.EnsureFocused() is { } err) {
            return err;
        }

        input.KeyDown(vk);
        return $"Holding {key} down. Call release_key to let go.";
    }

    [McpServerTool(Name = "release_key"), Description("Releases a key previously held with hold_key.")]
    public string ReleaseKey([Description("Key name to release. Use \"all\" to release every held key.")] string key) {
        if (key.Equals("all", StringComparison.OrdinalIgnoreCase)) {
            input.ReleaseAll();
            return "Released all held keys.";
        }

        if (!KeyMap.TryResolve(key, out var vk)) {
            return UnknownKey(key);
        }

        input.KeyUp(vk);
        return $"Released {key}.";
    }

    [McpServerTool(Name = "type_text"), Description(
        "Types literal text into the focused FiveM input field (F8 console, chat box). Does not press Enter.")]
    public string TypeText(
        [Description("Text to type.")] string text,
        [Description("Delay between characters in milliseconds. Default 8.")] int perCharDelayMs = 8) {
        if (windows.EnsureFocused() is { } err) {
            return err;
        }

        input.TypeText(text, perCharDelayMs);
        return $"Typed {text.Length} characters.";
    }

    [McpServerTool(Name = "console_command"), Description(
        "Runs a client console command WITHOUT opening the F8 console, over the client's local devcon IPC socket. " +
        "Needs no RCON password and works on any server. A command that is not client-side is forwarded to the " +
        "server with the player's own permissions, so a locally connected player can run server commands too. " +
        "Use for \"connect <address>\", \"quit\", \"restart <resource>\", and admin commands.")]
    public async Task<string> ConsoleCommand(
        [Description("Console command to run, e.g. \"connect 127.0.0.1:30120\".")] string command,
        [Description("Type the command through the visible F8 console instead of the IPC socket. Default false.")]
        bool useKeystrokes = false,
        CancellationToken cancellationToken = default) {
        if (!useKeystrokes) {
            try {
                return await devcon.SendCommandAsync(command, cancellationToken);
            } catch (Exception ex) {
                return $"devcon error: {ex.Message}";
            }
        }

        if (windows.EnsureFocused() is { } err) {
            return err;
        }

        const ushort vkF8 = 0x77;
        const ushort vkEnter = 0x0D;

        input.PressKey(vkF8, 60);
        Thread.Sleep(400);
        input.TypeText(command);
        Thread.Sleep(120);
        input.PressKey(vkEnter, 60);
        Thread.Sleep(250);

        input.PressKey(vkF8, 60);

        return $"Typed console command through the F8 console: {command}.";
    }

    [McpServerTool(Name = "read_console"), Description(
        "Reads the live client console as text, tagged with the channel that emitted each line " +
        "(script:my_resource and friends). This attribution is exactly what the log file drops, so prefer this " +
        "over read_log when you need to know which resource produced a message. Nothing is opened on screen.")]
    public string ReadConsole(
        [Description("Cursor from a previous read_console call. Returns only lines printed since that point.")]
        long? sinceCursor = null,
        [Description("Case-insensitive regex matched against the message and the channel, e.g. \"error|mcp_bridge\".")]
        string? filter = null,
        [Description("Maximum lines to return. Default 200.")]
        int maxLines = 200) {
        var (lines, cursor, connected) = consoleTap.Read(sinceCursor, filter, Math.Clamp(maxLines, 1, 2000));

        if (!connected && lines.Length == 0) {
            return "Not attached to the client console. Is the FiveM client running? " +
                   "The tap reconnects automatically every few seconds.";
        }

        var header = $"cursor={cursor} lines={lines.Length}" + (connected ? "" : " (tap disconnected, showing buffered lines)");

        return lines.Length == 0
            ? $"{header}\n(nothing new)"
            : header + "\n" + string.Join('\n', lines.Select(l => $"[{l.Channel}] {l.Text}"));
    }

    [McpServerTool(Name = "mouse_move"), Description(
        "Moves the mouse. Relative deltas drive the in-game camera; absolute window coordinates position the cursor over NUI/menu elements.")]
    public string MouseMove(
        [Description("Horizontal movement. Relative delta by default, or window-relative X when absolute is true.")]
        int x,
        [Description("Vertical movement. Relative delta by default, or window-relative Y when absolute is true.")]
        int y,
        [Description("True to treat x/y as window-relative coordinates instead of deltas. Default false (camera control).")]
        bool absolute = false) {
        if (windows.EnsureFocused() is { } err) {
            return err;
        }

        if (!absolute) {
            input.MoveRelative(x, y);
            return $"Moved mouse by ({x},{y}).";
        }

        var info = windows.GetWindow();
        var screen = windows.ClientToScreen(info, x, y);
        if (screen is null) {
            return "Could not translate window coordinates to screen coordinates.";
        }

        input.MoveAbsolute(screen.Value.X, screen.Value.Y);
        return $"Moved cursor to window position ({x},{y}).";
    }

    [McpServerTool(Name = "click"), Description(
        "Clicks a mouse button, optionally moving the cursor to a window-relative position first.")]
    public string Click(
        [Description("Window-relative X to click at. Omit to click wherever the cursor already is.")] int? x = null,
        [Description("Window-relative Y to click at.")] int? y = null,
        [Description("Button to click: left, right, or middle. Default left.")] string button = "left",
        [Description("How long to hold the button down, in milliseconds. Default 40.")] int holdMs = 40) {
        if (windows.EnsureFocused() is { } err) {
            return err;
        }

        if (x is { } px && y is { } py) {
            var info = windows.GetWindow();
            var screen = windows.ClientToScreen(info, px, py);
            if (screen is null) {
                return "Could not translate window coordinates to screen coordinates.";
            }

            input.MoveAbsolute(screen.Value.X, screen.Value.Y);
            Thread.Sleep(60);
        }

        input.Click(button, holdMs);
        return x is null ? $"Clicked {button} at the current cursor position." : $"Clicked {button} at ({x},{y}).";
    }

    [McpServerTool(Name = "scroll"), Description("Scrolls the mouse wheel. Positive scrolls up, negative scrolls down.")]
    public string Scroll([Description("Number of wheel clicks. Positive = up, negative = down.")] int clicks) {
        if (windows.EnsureFocused() is { } err) {
            return err;
        }

        input.Scroll(clicks);
        return $"Scrolled {clicks} click(s).";
    }

    [McpServerTool(Name = "wait"), Description(
        "Waits before the next action - for loading screens, animations, or holding a movement key for a set duration.")]
    public async Task<string> Wait(
        [Description("Milliseconds to wait, capped at 30000.")] int milliseconds,
        CancellationToken cancellationToken = default) {
        var ms = Math.Clamp(milliseconds, 0, 30_000);
        await Task.Delay(ms, cancellationToken);
        return $"Waited {ms}ms.";
    }

    [McpServerTool(Name = "read_log"), Description(
        "Reads the FiveM client log - the same stream the F8 console shows: connection progress, resource loading, " +
        "script prints, and Lua errors. Pass the cursor from a previous call to get only what was logged since then.")]
    public string ReadLog(
        [Description("Cursor from a previous read_log call. Returns only lines logged since that point.")]
        long? sinceCursor = null,
        [Description("When no cursor is given, return only the last N lines. Default 100.")]
        int tailLines = 100,
        [Description("Case-insensitive regex; only matching lines are returned. e.g. \"error|warning|script:\".")]
        string? filter = null,
        [Description("Hard cap on returned lines. Default 300.")]
        int maxLines = 300) {
        try {
            var result = logs.Read(sinceCursor, tailLines, filter, Math.Clamp(maxLines, 1, 2000));
            var header = $"[{Path.GetFileName(result.File)}] cursor={result.Cursor} lines={result.Lines.Length}" +
                         (result.Truncated ? " (older lines truncated)" : "");

            return result.Lines.Length == 0
                ? $"{header}\n(no matching lines)"
                : header + "\n" + string.Join('\n', result.Lines);
        } catch (Exception ex) {
            return $"Error reading log: {ex.Message}";
        }
    }

    private static string UnknownKey(string key) =>
        $"Unknown key \"{key}\". Use a letter, digit, F1-F24, or one of: {string.Join(", ", KeyMap.KnownNames.Order())}.";

    private static CallToolResult Error(string message) => new() {
        IsError = true,
        Content = [new TextContentBlock { Text = message }],
    };
}
