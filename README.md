# fivem-mcp

An MCP server that lets an AI agent drive a FiveM client on this machine: keyboard, mouse,
screenshots, the F8 console, and the client log. It speaks plain MCP over stdio, so any MCP
client can use it — Claude Code is simply what it was built and tested against.

Everything is external OS-level control (`SendInput` plus screen capture), so no in-game Lua
resource is required and it works against any server. The F8 console covers the higher-level
operations (`connect`, `quit`, `restart <resource>`) as ordinary keystrokes.

## Requirements

- Windows
- .NET 11 SDK (pinned in `global.json`)
- FiveM installed — the installer registers the `fivem://` URI scheme that `launch` uses

## Build

Debug build with `dotnet build`, or `dotnet publish -c Release` for faster startup in daily use.

## Register with an MCP client

The server is a stdio MCP server, configured like any other. For Claude Code, point it at the
published executable with `claude mcp add fivem -- C:\dev\gta\v\fivem\fivem-mcp\bin\Release\net11.0-windows\FiveMMcp.exe`,
or run from source with `claude mcp add fivem -- dotnet run --project C:\dev\gta\v\fivem\fivem-mcp`.

For clients configured through JSON, the equivalent entry is:

```json
{
  "mcpServers": {
    "fivem": {
      "command": "C:\\dev\\gta\\v\\fivem\\fivem-mcp\\bin\\Release\\net11.0-windows\\FiveMMcp.exe"
    }
  }
}
```

## Tools

| Tool | What it does |
|---|---|
| `launch` | Starts FiveM, optionally straight into a server via `fivem://connect/host:port` |
| `quit_game` | Closes the client; `force` terminates every FiveM process when it is hung |
| `get_window_status` | Whether the game window exists, its size and position, and focus state |
| `focus_window` | Brings the game to the foreground |
| `restore_focus` | Hands focus back to your previous window, falling back to minimising |
| `minimize_window` | Sends the game to the background |
| `rcon_command` | Runs a command on the *server* over RCON, without touching the client at all |
| `notify` | Shows an in-game toast so you can see what the agent is doing |
| `screenshot` | PNG of the game window, optional crop and downscale |
| `press_key` | Press and release a key (`W`, `F8`, `Enter`, `Esc`, `LShift`, `Left`, …) |
| `hold_key` / `release_key` | Sustained input for movement; `release_key all` clears everything |
| `type_text` | Types literal text into the console or chat |
| `console_command` | Opens F8, types a command, presses Enter, closes F8 |
| `read_console` | Reads the live client console as text, tagged with the emitting resource |
| `get_position` | Player position and heading as `vec4(x, y, z, heading)`, plus interior and vehicle |
| `mouse_move` | Relative deltas drive the camera; absolute coordinates position the cursor |
| `click` / `scroll` | Mouse buttons and wheel |
| `wait` | Pause between actions, for loading screens and held keys |
| `read_log` | Reads `CitizenFX_log_*.log`, with tail, regex filter, and an incremental cursor |

## RCON and in-game notifications

`rcon_command` and `notify` talk to the FiveM **server** over RCON (UDP), which is the one
channel that needs neither the game client nor its console. Configure it with two
environment variables on the MCP server process: `FIVEM_RCON_PASSWORD` (required, matches
`rcon_password` in the server config) and `FIVEM_RCON_ADDRESS` (optional, defaults to
`127.0.0.1:30120`). Without the password those two tools report that RCON is unconfigured
and everything else keeps working.

`notify` needs the `mcp_bridge` companion resource, a C# resource that registers the
`mcp_notify` and `mcp_players` console commands and renders messages as native feed
notifications. Because RCON reaches the server rather than the client, the resource needs
both halves: the server script receives the command, and the client script draws the toast,
since the feed natives only exist client-side. Messages support GTA colour codes such as
`~g~` and `~b~`.

## Things worth knowing

**The game must be in the foreground.** Every input tool and `screenshot` calls
`EnsureFocused()` first. Screen capture reads whatever is drawn on top, so capturing a
background window would photograph whatever covers it. This does take focus away from
whatever you were doing.

**Keys are sent as scan codes, not virtual keys.** GTA V reads the keyboard through
DirectInput and raw input, which ignore virtual-key-only injection. `type_text` is the
exception: it uses Unicode key events, which the console and chat NUI read normally.

**Camera versus cursor.** `mouse_move` with relative deltas is what the in-game camera
consumes. Absolute positioning (`absolute: true`, and the coordinates on `click`) is for NUI
and menus.

**Log versus console.** `read_log` and the F8 console carry the same stream, but the log file
drops the channel tag: a line reads `MainThrd/ All client systems loaded` with no indication
that `devhub_lib` printed it. The console shows `script:devhub_lib` in colour. Use `read_log`
for cheap text and `read_console` when you need to know which resource is responsible. Lua
warnings and errors are the exception — they carry `(@resource/file.lua:line)` in both.

**Elevation.** If FiveM runs elevated and the MCP client does not, `SendInput` is silently
blocked by UIPI. The tools report this rather than failing quietly.

**Held keys.** `hold_key` tracks what is down and releases everything on process exit, so a
crash mid-session will not leave you sprinting into a wall.
