# fivem-mcp

An MCP server that lets an AI agent drive a FiveM client: launch it, connect
to a server, press keys and move the mouse, take screenshots, run console
commands, read the client log, and ask the game where the player is. It
speaks plain MCP over stdio, so any MCP client can use it; Claude Code is
what it was built and tested against. The server runs on the same machine
as the game, since it drives the client through local Windows APIs and
sockets.

## Contents

- [Install (no build required)](#install-no-build-required)
- [How it works](#how-it-works)
- [Build from source](#build-from-source)
- [Register with an MCP client](#register-with-an-mcp-client)
- [Tools](#tools)
- [Launching the client](#launching-the-client)
- [The console: devcon, read_console and read_log](#the-console-devcon-read_console-and-read_log)
- [When RCON is worth configuring](#when-rcon-is-worth-configuring)
- [The mcp_bridge companion resource](#the-mcp_bridge-companion-resource)
- [Things worth knowing](#things-worth-knowing)
- [Troubleshooting](#troubleshooting)
- [Development](#development)

## Install (no build required)

This section is for people who just want to use fivem-mcp. If you want to
build it yourself, see [Build from source](#build-from-source).

1. Download the latest release zip, `fivem-mcp-v<version>-win-x64.zip`, from
   the [releases page](https://github.com/VIRUXE/fivem-mcp/releases).
   Unzip it somewhere permanent, for example `%LOCALAPPDATA%\fivem-mcp`.

2. Install the .NET 11 runtime if you do not already have it: get the
   Windows x64 **.NET Runtime** (not the SDK) from
   [dotnet.microsoft.com/download/dotnet/11.0](https://dotnet.microsoft.com/download/dotnet/11.0).
   Check what is installed with:

   ```
   dotnet --list-runtimes
   ```

3. Register the server with your MCP client. For Claude Code:

   ```
   claude mcp add fivem -- %LOCALAPPDATA%\fivem-mcp\FiveMMcp.exe
   ```

   For other MCP clients, see the JSON snippet under
   [Register with an MCP client](#register-with-an-mcp-client).

4. Optional: the companion resource. Two of the tools, `notify` and
   `get_position`, need `resources/mcp_bridge` running on the FiveM server;
   everything else works without it. Download `mcp_bridge-v<version>.zip`
   from the same release and unzip it into the server's resources folder,
   for example `resources/[dev]/mcp_bridge`. The zip is source, not a
   built resource: build the two projects inside it once with `dotnet
   build`, then `ensure mcp_bridge`. See
   [The mcp_bridge companion resource](#the-mcp_bridge-companion-resource)
   for the build commands and what it registers.

5. Optional: RCON. Most tools do not need it; it only matters when no
   client is running or your player lacks permissions for a command. See
   [When RCON is worth configuring](#when-rcon-is-worth-configuring).

6. First run: launch FiveM, either through the `launch` tool or by hand,
   and connect to a server. Then check that everything works:

   ```
   get_window_status
   screenshot
   read_log
   ```

   If the launcher refuses to start FiveM, set `FIVEM_LAUNCH=explorer`; see
   [Launching the client](#launching-the-client).

7. Updating: download the new release zip and replace the contents of your
   install folder with it. The MCP client keeps the old binary loaded until
   it reconnects, so it needs to reconnect to pick up the new one (in
   Claude Code: `/mcp`, then reconnect).

## How it works

```
  MCP client (Claude Code, ...)
        |  stdio, MCP
        v
  FiveMMcp.exe ---------------------------------------------------+
   |  SendInput (scan codes, Unicode)   -> keyboard and mouse     |
   |  Win32 window APIs + screen capture-> screenshot, focus      |
   |  TCP 127.0.0.1:29200 "devcon"      -> console_command,       |
   |                                        notify, get_position, |
   |                                        read_console (tap)    |
   |  file tail                          -> read_log              |
   |  UDP/TCP rcon to the game server   -> rcon_command           |
   +-----------------------------------------------------------+
        |                                           |
        v                                           v
  FiveM client (this machine)               FXServer (anywhere)
   devcon socket, F8 console, CitizenFX log   rcon, resources incl. mcp_bridge
```

Console commands (`connect`, `quit`, `restart <resource>`, admin commands) go
over the client's local **devcon** socket, so nothing appears on screen and
the F8 console never opens. That path needs no RCON password and works on
servers you do not administer, because a command the client does not handle
is forwarded to the server with the player's own permissions.

Keyboard and mouse input is synthesised with `SendInput`, because that is
the only way to actually play: to move, and to work NUI dialogs. Those tools
bring the game to the foreground; `restore_focus` hands your window back
afterwards.

## Build from source

This section is for developers building fivem-mcp itself. If you just want
to use it, see [Install (no build required)](#install-no-build-required).

- Windows
- .NET 11 SDK (pinned in `global.json`)
- FiveM installed at `%LOCALAPPDATA%\FiveM\FiveM.exe`, or `FIVEM_EXECUTABLE`
  pointing at it

Debug build with `dotnet build`, or `dotnet publish -c Release` for faster
startup in daily use. While an MCP client has the server running, the
binaries are locked; see [Development](#development).

## Register with an MCP client

The server is a stdio MCP server, configured like any other. For Claude
Code, point it at the built executable with
`claude mcp add fivem -- <repo>\bin\Release\net11.0-windows\FiveMMcp.exe`,
or run from source with `claude mcp add fivem -- dotnet run --project <repo>`.

For clients configured through JSON, the equivalent entry is:

```json
{
  "mcpServers": {
    "fivem": {
      "command": "C:\\path\\to\\fivem-mcp\\bin\\Release\\net11.0-windows\\FiveMMcp.exe",
      "env": {
        "FIVEM_RCON_PASSWORD": "optional, only for rcon_command",
        "FIVEM_RCON_ADDRESS": "127.0.0.1:30120"
      }
    }
  }
}
```

## Tools

| Tool | What it does |
|---|---|
| `launch` | Starts FiveM, optionally straight into a server; see [Launching the client](#launching-the-client) |
| `quit_game` | Closes the client; `force` terminates every FiveM process when it is hung |
| `get_window_status` | Whether the game window exists, its size and position, and focus state |
| `focus_window` | Brings the game to the foreground |
| `restore_focus` | Hands focus back to your previous window, falling back to minimising |
| `minimize_window` | Sends the game to the background |
| `screenshot` | PNG of the game window, optional crop and downscale |
| `record` | A burst of frames back to back, optionally clicking after the first, for things a single screenshot misses |
| `press_key` | Press and release a key (`W`, `F8`, `Enter`, `Esc`, `LShift`, `Left`, ...) |
| `hold_key` / `release_key` | Sustained input for movement; `release_key all` clears everything |
| `type_text` | Types literal text into the console or chat |
| `mouse_move` | Relative deltas drive the camera; absolute coordinates position the cursor |
| `click` / `scroll` | Mouse buttons and wheel |
| `console_command` | Runs a client console command over the devcon socket, nothing on screen |
| `read_log` | Reads `CitizenFX_log_*.log`, with tail, regex filter, and an incremental cursor |
| `read_console` | Reads the live client console with the emitting resource on each line; attaches on demand |
| `get_position` | Player position and heading as `vec4(x, y, z, heading)`, plus interior and vehicle |
| `notify` | In-game toast over devcon (no credentials); `everyone: true` broadcasts via RCON |
| `rcon_command` | Runs a command on the *server* over RCON, without touching the client at all |
| `wait` | Pause between actions, for loading screens and held keys |

## Launching the client

FiveM's launcher checks its parent process and refuses anything that is
not Explorer or a browser: "This application should be launched directly
from the shell or a web browser". A process that starts `FiveM.exe`
itself, with or without the `fivem://` link, is that parent and gets
refused. `launch` therefore has two routes, tried in order:

1. **uri**: hand `fivem://connect/host:port` to the shell, the way a
   browser does. Needs the scheme registered (a normal FiveM install does
   that).
2. **explorer**: start through `explorer.exe`, so Explorer is the parent.
   With the scheme registered the link is passed along; without it the bare
   executable is started and the caller connects from the menu with
   `console_command "connect host:port"`. This route does not work from an
   elevated process, where the request lands on the unelevated desktop
   shell.

`FIVEM_LAUNCH=uri|explorer|auto` forces a route; `auto` (the default) tries
uri when the scheme is registered and falls back to explorer. If the uri
route brings up the refusal dialog on your machine, set
`FIVEM_LAUNCH=explorer`.

The launcher then runs the Rockstar Games Launcher and the game; `launch`
returns once the game window exists, which is well before the server join
completes. Follow progress with `read_log` (`OnConnectionProgress` lines)
and take a screenshot when it goes quiet.

## The console: devcon, read_console and read_log

`read_log` and `read_console` carry the same stream, but the log file drops
the channel tag: a line reads `MainThrd/ All client systems loaded` with no
indication that `devhub_lib` printed it. `read_console` reports
`[script:devhub_lib]`, because the devcon stream carries the channel with
every message. Lua warnings and errors carry `(@resource/file.lua:line)` in
both.

`read_console` is attached on demand: the first call opens the tap and
mostly returns nothing, later calls return what was printed since, and the
tap lets go 60 s after the last call. That is deliberate. Every devcon
handshake races the client's console print-drain thread over state the
client does not synchronise (`DevConServer.cpp`, `HandleConsoleMessage`
against `FlushKnownCommands`; an upstream fix,
[citizenfx/fivem#4206](https://github.com/citizenfx/fivem/pull/4206), was
not merged), and a client whose console is busy can crash in `devcon.dll`
on that race. A tap that is always on, reconnecting forever, keeps rolling
that dice; one that exists only while somebody is reading rolls it as
rarely as they do. Prefer `read_log` unless the channel attribution is what
you need, and do not attach the tap right before a script is about to print
a lot.

`console_command`, `notify` and `get_position` each open one short devcon
connection; that is the same handshake, so the same advice applies in a
weaker form: keep them off the moment a resource is spamming the console.

## When RCON is worth configuring

Most things do not need it. `notify` and `get_position` draw and read on the
client, so they go over devcon and need no credentials at all. Console
commands go the same way, and on a server where your player has
permissions that already includes server-side commands, since the client
forwards what it cannot handle.

RCON (`rcon_command`, and `notify` with `everyone: true`) earns its place in
the cases devcon cannot reach:

- **No client is running**, or none is connected to that server. devcon is
  a socket *inside* the game client, so it disappears with it; RCON only
  needs the server.
- **Your in-game player lacks the permissions** for a command, but you
  administer the server and hold its RCON password.

Where the game server lives does not matter. devcon only receives the
command; the client is what executes it and forwards anything server-side
over its own connection, so a client on your machine drives a server on the
other side of the world just as well as a local one. The `127.0.0.1`
binding limits who can reach the *socket*, not which server the command
lands on.

Nor is the client necessarily local. It binds `127.0.0.1` only by default;
launched with `-devcon` it binds `0.0.0.0`, and `FIVEM_DEVCON_HOST` (plus
`FIVEM_DEVCON_PORT`) points this server at it. That socket has no
authentication whatsoever, so anyone who can reach the port gets arbitrary
console execution in that client: only do this on a network you trust.

Configure RCON with two environment variables on the MCP server process:
`FIVEM_RCON_PASSWORD` (required, matches `rcon_password` in the server
config) and `FIVEM_RCON_ADDRESS` (optional, defaults to `127.0.0.1:30120`).
Without the password the RCON-only paths report that it is unconfigured and
everything else keeps working.

## The mcp_bridge companion resource

`notify` and `get_position` need `resources/mcp_bridge`, which ships in this
repo so the server and its in-game half stay in step. Build it, then make it
visible to your FiveM server; a junction avoids keeping a second copy that
drifts:

```powershell
dotnet build resources/mcp_bridge/src/Client/McpBridge.Client.csproj
dotnet build resources/mcp_bridge/src/Server/McpBridge.Server.csproj

New-Item -ItemType Junction `
  -Path "<server-data>\resources\[dev]\mcp_bridge" `
  -Target "<this-repo>\resources\mcp_bridge"
```

A junction needs no administrator rights, unlike a symlink. Then `ensure
mcp_bridge` on the server; `get_position` does this itself over RCON when
the password is configured. After editing `fxmanifest.lua` run `refresh`
before `restart`, or the server keeps using the cached manifest.

The resource registers the client-side `mcp_notify`, `mcp_position` and
`mcp_indicator` (a persistent "MCP Connected" corner text that the console
tap switches on while attached), plus `mcp_notify_all` and `mcp_players` on
the server for the RCON path. Notifications render as native feed messages
and support GTA colour codes such as `~g~` and `~b~`.

## Things worth knowing

**The game must be in the foreground.** Every input tool and `screenshot`
calls `EnsureFocused()` first. Screen capture reads whatever is drawn on
top, so capturing a background window would photograph whatever covers it.
This does take focus away from whatever you were doing.

**Keys are sent as scan codes, not virtual keys.** GTA V reads the keyboard
through DirectInput and raw input, which ignore virtual-key-only injection.
`type_text` is the exception: it uses Unicode key events, which the console
and chat NUI read normally.

**Camera versus cursor.** `mouse_move` with relative deltas is what the
in-game camera consumes. Absolute positioning (`absolute: true`, and the
coordinates on `click`) is for NUI and menus. Window-relative coordinates
are in the window's own pixels; a 1280 px wide screenshot of a 1920 px
window is scaled by 1.5.

**Elevation.** If FiveM runs elevated and the MCP client does not,
`SendInput` is silently blocked by UIPI. The tools report this rather than
failing quietly.

**Held keys.** `hold_key` tracks what is down and releases everything on
process exit, so a crash mid-session will not leave you sprinting into a
wall.

**Streaming assets under a connected client.** Restarting a resource that
streams map data (navmesh cells especially) while the player stands in that
area crashes or hangs the client. Restart with nobody nearby, or reconnect
afterwards. Not an MCP limitation, just something an agent with `rcon_command`
can easily do to itself.

## Troubleshooting

| Symptom | Cause | Do |
|---|---|---|
| "This application should be launched directly from the shell or a web browser" | the launcher refused the parent process | set `FIVEM_LAUNCH=explorer`; see [Launching the client](#launching-the-client) |
| FiveM crashes at `devcon.dll+...` (`ntdll` heap fault above it) | the devcon handshake race | avoid `read_console` while the console is busy; make sure only one MCP server process is running (a stale one from an earlier session keeps its own tap) |
| `Could not reach the FiveM devcon socket` | the client is not past the launcher, or a remote client was not started with `-devcon` | wait, then retry; check `get_window_status` |
| `get_position` says mcp_bridge is not running and RCON is unconfigured | the companion resource is not started | `ensure mcp_bridge` on the server, or configure `FIVEM_RCON_PASSWORD` |
| Input tools report UIPI | the game is elevated and the MCP client is not | run both at the same level |
| Build fails with "file is being used by another process" | an MCP client still runs the old binary | see [Development](#development) |

## Development

```
Program.cs                 host setup and DI
Tools/FiveMTools.cs        every MCP tool: name, description, parameters, and the call into a service
Services/
  LauncherService.cs       finding and starting the client, quitting it
  WindowManager.cs         window lookup, focus, foreground rules
  InputService.cs, KeyMap  SendInput, scan codes, held-key tracking
  CaptureService.cs        screenshots and frame bursts
  DevConService.cs         one-shot commands over the devcon socket
  ConsoleTapService.cs     the on-demand console tap
  LogService.cs            CitizenFX log discovery and tailing
  RconService.cs           RCON
resources/mcp_bridge/      the companion FiveM resource (C#, client and server halves)
```

A tool is a method on `FiveMTools` with `[McpServerTool]` and a
`[Description]`; the description is what the agent reads, so it should say
what the tool is for, what it needs, and what to do instead when it does not
apply.

Rebuilding while a client holds the server open fails on the copy step,
because Windows locks a running executable and its DLL. Windows does allow
renaming them: move `FiveMMcp.exe` and `FiveMMcp.dll` aside, build, and
delete the old copies after the client has reconnected. The client picks up
the new binary only when it restarts the server (in Claude Code, `/mcp`
then reconnect; `/reload-plugins` does not restart user-level servers).

## License

[Unlicense](LICENSE), public domain.
