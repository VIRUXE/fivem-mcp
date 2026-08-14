using System;
using System.Collections.Generic;
using System.Linq;
using CitizenFX.Core;
using static CitizenFX.Core.Native.API;

namespace McpBridge.Server {
    /// <summary>
    /// Console commands invoked over RCON by the fivem-mcp server, so an agent driving
    /// the client can surface what it is doing inside the game.
    /// </summary>
    public class McpBridgeServer : BaseScript {
        public McpBridgeServer() {
            // Payloads are rebuilt from args rather than the raw command: RCON delivers
            // rawCommand with a leading space, which is what breaks the stock runcode
            // "run" command (its rawCommand:sub(4) leaves a stray "n" behind).
            RegisterCommand("mcp_notify", new Action<int, List<object>, string>((source, args, raw) => {
                var message = string.Join(" ", args);

                if (string.IsNullOrWhiteSpace(message)) {
                    Debug.WriteLine("mcp_notify: no message given");
                    return;
                }

                // Passing a player handle here would bind to the broadcast overload
                // TriggerClientEvent(eventName, args) and send an event named "1", so
                // broadcast explicitly instead.
                TriggerClientEvent("mcpNotificationRequested", message);

                Debug.WriteLine($"mcp_notify: sent \"{message}\" to {Players.Count()} player(s)");
            }), true);

            // Lets the MCP server confirm someone is actually in-game before acting.
            RegisterCommand("mcp_players", new Action<int, List<object>, string>((source, args, raw) => {
                Debug.WriteLine($"mcp_players: {Players.Count()} online");

                foreach (var player in Players) {
                    Debug.WriteLine($"  [{player.Handle}] {player.Name}");
                }
            }), true);
        }
    }
}
