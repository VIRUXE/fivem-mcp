using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using CitizenFX.Core;
using static CitizenFX.Core.Native.API;

namespace McpBridge.Client {
    /// <summary>
    /// Client half of the bridge: renders what the fivem-mcp server is doing as a native
    /// feed notification, and reports world state the agent cannot otherwise see.
    /// </summary>
    public class McpBridgeClient : BaseScript {
        // Toggled by mcp_indicator; drawn every tick while true. Persistent (unlike the
        // feed toast below), so it reflects live connection state rather than a one-shot
        // event - the MCP server flips it on connect/disconnect of its devcon console tap.
        private bool showIndicator;

        public McpBridgeClient() {
            EventHandlers["mcpNotificationRequested"] += new Action<string>(OnNotificationRequested);
            Tick += OnTick;

            // Drawing the toast is purely client-side, so the MCP server calls this
            // straight over the devcon socket. No RCON password, no server round trip,
            // and it works even where the player has no special permissions.
            RegisterCommand("mcp_notify", new Action<int, List<object>, string>((source, args, raw) => {
                var message = string.Join(" ", args);

                if (!string.IsNullOrWhiteSpace(message)) {
                    OnNotificationRequested(message);
                }
            }), false);

            // "mcp_indicator on|off" - same devcon channel as mcp_notify, driven by the
            // MCP server's console-tap connection state rather than a one-shot event.
            RegisterCommand("mcp_indicator", new Action<int, List<object>, string>((source, args, raw) => {
                showIndicator = args.Count > 0 && string.Equals(
                    args[0]?.ToString(), "on", StringComparison.OrdinalIgnoreCase);
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

        private Task OnTick() {
            if (showIndicator) {
                SetTextFont(4);
                SetTextScale(0.35f, 0.35f);
                SetTextColour(120, 220, 120, 220);
                SetTextOutline();
                BeginTextCommandDisplayText("STRING");
                AddTextComponentSubstringPlayerName("MCP Connected");
                EndTextCommandDisplayText(0.01f, 0.01f);
            }

            return Task.FromResult(0);
        }
    }
}
