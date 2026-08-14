using System.Buffers.Binary;
using System.Net.Sockets;
using System.Text;

namespace FiveMMcp.Services;

/// <summary>
/// Speaks the FiveM client's "devcon" protocol, a TCP server the client hosts on
/// localhost (see components/devcon/src/DevConServer.cpp). A CMND packet is fed
/// straight into the console context, so commands run without the F8 console opening.
///
/// This is the most capable channel available: it needs no RCON password, works
/// against servers you do not control, and because a client console command that is
/// not client-side is forwarded to the server with the player's own permissions, a
/// locally connected player can drive server commands through it too.
/// </summary>
public sealed class DevConService {
    // Retail client listens on 29200; the CL2 (second instance) build uses 29300.
    private static readonly int[] DefaultPorts = [29200, 29300];

    // The client binds 127.0.0.1 unless it was launched with "-devcon", which binds
    // 0.0.0.0 instead. Point this at another machine to drive a client running there.
    internal static string Host =>
        Environment.GetEnvironmentVariable("FIVEM_DEVCON_HOST") is { Length: > 0 } host ? host : "127.0.0.1";

    internal static int[] CandidatePorts =>
        int.TryParse(Environment.GetEnvironmentVariable("FIVEM_DEVCON_PORT"), out var port) ? [port] : DefaultPorts;

    private static ReadOnlySpan<byte> CmndMagic => "CMND"u8;

    public async Task<string> SendCommandAsync(string command, CancellationToken ct) {
        if (string.IsNullOrWhiteSpace(command)) {
            throw new ArgumentException("No command given.", nameof(command));
        }

        var errors = new List<string>();

        foreach (var port in CandidatePorts) {
            try {
                await SendToPortAsync(command, port, ct);
                return $"Ran client console command via devcon on {Host}:{port}: {command}";
            } catch (SocketException ex) {
                errors.Add($"port {port}: {ex.SocketErrorCode}");
            }
        }

        throw new InvalidOperationException(
            $"Could not reach the FiveM devcon socket on {Host} ({string.Join(", ", errors)}). " +
            "Is the client running and past the launcher? For a remote client it must be launched with -devcon.");
    }

    private static async Task SendToPortAsync(string command, int port, CancellationToken ct) {
        using var client = new TcpClient();
        await client.ConnectAsync(Host, port, ct);

        var packet = BuildCommandPacket(command);
        await client.GetStream().WriteAsync(packet, ct);
        await client.GetStream().FlushAsync(ct);

        // The client reads on its own loop and sends nothing back for CMND, so give it
        // a moment to drain before the socket closes.
        await Task.Delay(120, ct);
    }

    private static byte[] BuildCommandPacket(string command) {
        var body = Encoding.UTF8.GetBytes(command);

        // magic + protocol + length + reserved + body + terminator. The client reads
        // one byte fewer than it has left, so the trailing NUL is required padding.
        var packet = new byte[4 + 2 + 4 + 2 + body.Length + 1];
        var span = packet.AsSpan();

        CmndMagic.CopyTo(span);
        BinaryPrimitives.WriteUInt16BigEndian(span[4..], 211);
        BinaryPrimitives.WriteUInt32BigEndian(span[6..], (uint)(body.Length + 1));
        BinaryPrimitives.WriteUInt16BigEndian(span[10..], 0);
        body.CopyTo(span[12..]);

        return packet;
    }
}
