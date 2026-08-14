using System.Net;
using System.Net.Sockets;
using System.Text;

namespace FiveMMcp.Services;

/// <summary>
/// FXServer RCON, which is UDP: an out-of-band packet prefixed with four 0xFF bytes,
/// answered by one or more "print" packets. Same protocol qrcon speaks.
/// </summary>
public sealed class RconService {
    private static readonly byte[] Preamble = [0xFF, 0xFF, 0xFF, 0xFF];

    public string Address { get; } =
        Environment.GetEnvironmentVariable("FIVEM_RCON_ADDRESS") ?? "127.0.0.1:30120";

    private string? Password { get; } = Environment.GetEnvironmentVariable("FIVEM_RCON_PASSWORD");

    public bool IsConfigured => !string.IsNullOrEmpty(Password);

    public async Task<string> ExecuteAsync(string command, int timeoutMs, CancellationToken ct) {
        if (string.IsNullOrEmpty(Password)) {
            throw new InvalidOperationException(
                "RCON is not configured. Set FIVEM_RCON_PASSWORD (and optionally FIVEM_RCON_ADDRESS, " +
                "default 127.0.0.1:30120) in the MCP server's environment.");
        }

        var endpoint = ParseEndpoint(Address);

        using var udp = new UdpClient(endpoint.AddressFamily);
        udp.Connect(endpoint);

        var packet = Preamble.Concat(Encoding.UTF8.GetBytes($"rcon {Password} {command}")).ToArray();
        await udp.SendAsync(packet, ct);

        // The server answers in as many packets as it needs, with no terminator, so
        // read until a gap rather than after the first datagram.
        var response = new StringBuilder();

        while (true) {
            using var window = CancellationTokenSource.CreateLinkedTokenSource(ct);
            window.CancelAfter(response.Length == 0 ? timeoutMs : 350);

            UdpReceiveResult result;

            try {
                result = await udp.ReceiveAsync(window.Token);
            } catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
                break;
            }

            // Strip the out-of-band header off the raw bytes: 0xFF does not survive a
            // UTF-8 decode, so this cannot be matched on the decoded string.
            var body = result.Buffer.AsSpan();

            if (body.StartsWith(Preamble)) {
                body = body[Preamble.Length..];

                if (body.StartsWith("print"u8)) {
                    body = body[5..];
                }
            }

            response.Append(Encoding.UTF8.GetString(body));
        }

        if (response.Length == 0) {
            throw new TimeoutException(
                $"No RCON response from {Address} within {timeoutMs}ms. Is the server running, and is rcon_password set?");
        }

        return StripColorCodes(response.ToString()).TrimEnd();
    }

    private static IPEndPoint ParseEndpoint(string address) {
        var parts = address.Split(':');

        if (parts.Length != 2 || !int.TryParse(parts[1], out var port)) {
            throw new InvalidOperationException($"RCON address \"{address}\" is not in host:port form.");
        }

        var ip = IPAddress.TryParse(parts[0], out var parsed)
            ? parsed
            : Dns.GetHostAddresses(parts[0]).First(a => a.AddressFamily == AddressFamily.InterNetwork);

        return new IPEndPoint(ip, port);
    }

    private static string StripColorCodes(string text) {
        var sb = new StringBuilder(text.Length);

        for (var i = 0; i < text.Length; i++) {
            if (text[i] == '^' && i + 1 < text.Length && char.IsDigit(text[i + 1])) {
                i++;
                continue;
            }

            sb.Append(text[i]);
        }

        return sb.ToString();
    }
}
