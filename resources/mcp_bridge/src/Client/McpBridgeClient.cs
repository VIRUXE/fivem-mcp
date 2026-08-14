using System;
using System.Collections.Generic;
using CitizenFX.Core;
using static CitizenFX.Core.Native.API;

namespace McpBridge.Client {
    /// <summary>
    /// Client half of the bridge: renders what the fivem-mcp server is doing as a native
    /// feed notification, and reports world state the agent cannot otherwise see.
    /// </summary>
    public class McpBridgeClient : BaseScript {
        public McpBridgeClient() {
            EventHandlers["mcpNotificationRequested"] += new Action<string>(OnNotificationRequested);

            // Drawing the toast is purely client-side, so the MCP server calls this
            // straight over the devcon socket. No RCON password, no server round trip,
            // and it works even where the player has no special permissions.
            RegisterCommand("mcp_notify", new Action<int, List<object>, string>((source, args, raw) => {
                var message = string.Join(" ", args);

                if (!string.IsNullOrWhiteSpace(message)) {
                    OnNotificationRequested(message);
                }
            }), false);

            // A client-side command, so the MCP server can invoke it straight over the
            // devcon socket without a server round trip. Printing goes to the client
            // console, which is mirrored into CitizenFX_log_*.log for the agent to read.
            RegisterCommand("mcp_position", new Action<int, List<object>, string>((source, args, raw) => {
                var ped = PlayerPedId();
                var pos = GetEntityCoords(ped, true);
                var heading = GetEntityHeading(ped);
                var vehicle = GetVehiclePedIsIn(ped, false);

                Debug.WriteLine(
                    $"mcp_position: vec4({pos.X:F2}, {pos.Y:F2}, {pos.Z:F2}, {heading:F2}) " +
                    $"interior={GetInteriorFromEntity(ped)} vehicle={vehicle}");
            }), false);
        }

        private static void OnNotificationRequested(string message) {
            BeginTextCommandThefeedPost("STRING");
            AddTextComponentSubstringPlayerName(message);
            EndTextCommandThefeedPostTicker(false, true);
        }
    }
}
