fx_version 'cerulean'
game 'gta5'

description 'Bridge that lets the fivem-mcp server surface its actions in-game via RCON.'

-- Built by src/McpBridge.sln into this folder. Targets the mono v1 runtime: the v2
-- runtime (mono_rt2) is a time-limited prerelease that expired 2026-06-30 on this build.
client_script 'McpBridge.Client.net.dll'
server_script 'McpBridge.Server.net.dll'
