using System.Buffers.Binary;
using System.Collections.Concurrent;
using System.Net.Sockets;
using System.Text;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace FiveMMcp.Services;

public sealed record ConsoleLine(long Seq, DateTime At, string Channel, string Text);

/// <summary>
/// Keeps a subscription to the client console over the devcon socket, but only while
/// someone is reading it.
///
/// Sending "PPCR" makes the client stream every console print as a PRNT packet, which
/// carries the emitting channel - "script:my_resource" and friends. That attribution is
/// the thing CitizenFX_log_*.log throws away, so this is strictly better than tailing
/// the log, and it needs no screenshots.
///
/// The tap is attached on demand: every PPCR handshake races the client's console
/// print-drain thread over unsynchronised state (DevConServer.cpp
/// HandleConsoleMessage/FlushKnownCommands; upstream fix in citizenfx/fivem#4206 never
/// shipped), and a client whose console is busy can crash in devcon.dll on that race. A
/// tap that is always on, reconnecting forever and toggling mcp_indicator on every
/// attach, keeps rolling that dice; one that exists only between a read_console call and
/// <see cref="IdleTimeout"/> later rolls it as rarely as the caller does.
/// </summary>
public sealed class ConsoleTapService(ILogger<ConsoleTapService> logger, LogService logs, DevConService devcon) : IHostedService {
    private const int MaxLines = 4000;
    // Shares the endpoint settings with DevConService: same socket, same client.
    private static int[] CandidatePorts => DevConService.CandidatePorts;

    private readonly ConcurrentQueue<ConsoleLine> buffer = new();
    private readonly ConcurrentDictionary<uint, string> channels = new();
    private CancellationTokenSource? stopping;
    private long sequence;
    private readonly object gate = new();
    private Task? runner;
    private DateTime lastReadAt = DateTime.MinValue;

    /// <summary>How long after the last read_console call the tap lets go of the socket.</summary>
    public static readonly TimeSpan IdleTimeout = TimeSpan.FromSeconds(60);

    public bool Connected { get; private set; }

    /// <summary>Whether the tap loop is currently running (attached or trying to attach).</summary>
    public bool Active { get { lock (gate) { return runner is { IsCompleted: false }; } } }

    // Hosted only so shutdown can cancel a running tap; nothing starts at boot.
    public Task StartAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public Task StopAsync(CancellationToken cancellationToken) {
        stopping?.Cancel();
        return Task.CompletedTask;
    }

    /// <summary>
    /// Starts the tap loop if it is not running, and marks now as the last read so the
    /// idle timer starts from here. Returns true when the loop was started by this call.
    /// </summary>
    public bool EnsureAttached() {
        lock (gate) {
            lastReadAt = DateTime.UtcNow;
            if (runner is { IsCompleted: false }) {
                return false;
            }
            stopping = new CancellationTokenSource();
            var token = stopping.Token;
            runner = Task.Run(() => RunAsync(token), CancellationToken.None);
            return true;
        }
    }

    private bool IdleExpired => DateTime.UtcNow - lastReadAt > IdleTimeout;

    /// <summary>
    /// Returns buffered console lines. Pass the cursor from a previous call to get only
    /// what has been printed since.
    /// </summary>
    public (ConsoleLine[] Lines, long Cursor, bool Connected) Read(long? sinceSeq, string? filter, int maxLines) {
        IEnumerable<ConsoleLine> query = buffer.ToArray();

        if (sinceSeq is { } since) {
            query = query.Where(l => l.Seq > since);
        }

        if (!string.IsNullOrWhiteSpace(filter)) {
            var rx = new System.Text.RegularExpressions.Regex(
                filter, System.Text.RegularExpressions.RegexOptions.IgnoreCase);
            query = query.Where(l => rx.IsMatch(l.Text) || rx.IsMatch(l.Channel));
        }

        var lines = query.ToArray();

        if (lines.Length > maxLines) {
            lines = lines[^maxLines..];
        }

        return (lines, Interlocked.Read(ref sequence), Connected);
    }

    // Base and ceiling for the reconnect backoff below. A flaky or repeatedly-dropping
    // devcon connection must not turn into a reconnect storm: every PPCR handshake races
    // the client's console print-drain thread over shared state that (as of writing) is
    // not synchronised on the client side, and hammering reconnects widens that race
    // window on every attempt. See DevConServer.cpp HandleConsoleMessage/FlushKnownCommands.
    private static readonly TimeSpan MinReconnectDelay = TimeSpan.FromSeconds(3);
    private static readonly TimeSpan MaxReconnectDelay = TimeSpan.FromSeconds(30);

    // A connection only counts as "healthy" - resetting the backoff back to the minimum -
    // once it has stayed up this long. A connection that drops faster than this is treated
    // as still failing, so a client that accepts the handshake and then immediately closes
    // (e.g. mid-crash) does not reset us straight back to rapid retries.
    private static readonly TimeSpan HealthyConnectionThreshold = TimeSpan.FromSeconds(10);

    private async Task RunAsync(CancellationToken ct) {
        var delay = MinReconnectDelay;

        while (!ct.IsCancellationRequested && !IdleExpired) {
            if (!IsFiveMRunning()) {
                // Nothing to connect to yet; there is no handshake to pace here, just
                // avoid a tight poll loop while the client is closed or still launching.
                delay = MinReconnectDelay;

                try {
                    await Task.Delay(MaxReconnectDelay, ct);
                } catch (OperationCanceledException) {
                    return;
                }

                continue;
            }

            var connectedAt = DateTime.UtcNow;
            var wasHealthy = false;

            try {
                await TapAsync(ct);
                wasHealthy = DateTime.UtcNow - connectedAt >= HealthyConnectionThreshold;
            } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
                return;
            } catch (Exception ex) {
                wasHealthy = DateTime.UtcNow - connectedAt >= HealthyConnectionThreshold;
                logger.LogDebug(ex, "console tap disconnected");
            }

            Connected = false;
            if (IdleExpired) {
                break;
            }

            delay = wasHealthy
                ? MinReconnectDelay
                : TimeSpan.FromSeconds(Math.Min(delay.TotalSeconds * 2, MaxReconnectDelay.TotalSeconds));

            try {
                await Task.Delay(delay, ct);
            } catch (OperationCanceledException) {
                return;
            }
        }
    }

    private async Task TapAsync(CancellationToken ct) {
        using var client = await ConnectAsync(ct);
        Connected = true;
        try {
            await TapStreamAsync(client, ct);
        } finally {
            Connected = false;
            await SendIndicatorAsync(on: false, CancellationToken.None);
        }
    }

    private async Task TapStreamAsync(TcpClient client, CancellationToken ct) {

        var stream = client.GetStream();
        await stream.WriteAsync("PPCR"u8.ToArray(), ct);
        await stream.FlushAsync(ct);

        SeedChannelNamesFromLog();
        await SendIndicatorAsync(on: true, ct);

        var pending = new List<byte>();
        var chunk = new byte[16384];

        while (!ct.IsCancellationRequested && !IdleExpired) {
            using var idle = CancellationTokenSource.CreateLinkedTokenSource(ct);
            idle.CancelAfter(IdleTimeout);
            int read;
            try {
                read = await stream.ReadAsync(chunk, idle.Token);
            } catch (OperationCanceledException) when (!ct.IsCancellationRequested) {
                continue; // quiet console; loop re-checks the idle timer
            }

            if (read == 0) {
                return;
            }

            pending.AddRange(chunk.AsSpan(0, read).ToArray());
            Consume(pending);
        }
    }

    /// <summary>
    /// Toggles the mcp_bridge on-screen "MCP Connected" indicator (mcp_indicator client
    /// command) to reflect this tap's actual connection state. Best-effort: a failure here
    /// (e.g. the client just closed, or mcp_bridge is not installed on the connected server)
    /// should not tear down the tap itself.
    /// </summary>
    private async Task SendIndicatorAsync(bool on, CancellationToken ct) {
        try {
            await devcon.SendCommandAsync($"mcp_indicator {(on ? "on" : "off")}", ct);
        } catch (OperationCanceledException) when (ct.IsCancellationRequested) {
            throw;
        } catch (Exception ex) {
            logger.LogDebug(ex, "could not update mcp_indicator");
        }
    }

    // Mirrors LauncherService's process detection: everything the client spawns is named
    // FiveM or FiveM_*.
    private static bool IsFiveMRunning() =>
        System.Diagnostics.Process.GetProcesses().Any(p =>
            p.ProcessName.Equals("FiveM", StringComparison.OrdinalIgnoreCase) ||
            p.ProcessName.StartsWith("FiveM_", StringComparison.OrdinalIgnoreCase));

    private async Task<TcpClient> ConnectAsync(CancellationToken ct) {
        foreach (var port in CandidatePorts) {
            var client = new TcpClient();

            try {
                await client.ConnectAsync(DevConService.Host, port, ct);
                return client;
            } catch (SocketException) {
                client.Dispose();
            }
        }

        throw new SocketException((int)SocketError.ConnectionRefused);
    }

    /// <summary>Frames whatever complete packets are buffered, leaving any partial tail.</summary>
    private void Consume(List<byte> pending) {
        var offset = 0;

        while (pending.Count - offset >= 12) {
            var span = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(pending)[offset..];
            var magic = Encoding.ASCII.GetString(span[..4]);
            var length = (int)BinaryPrimitives.ReadUInt32BigEndian(span[6..]);

            int size;

            switch (magic) {
                case "CHAN":
                    if (span.Length < 16) {
                        return;
                    }

                    // The client writes 14 + count*58 here but actually emits 16 + count*58,
                    // so size this one from the count rather than trusting the header.
                    var count = (int)BinaryPrimitives.ReadUInt32BigEndian(span[12..]);
                    size = 16 + (count * 58);
                    break;

                case "PRNT":
                case "AINF":
                case "CVAR":
                    size = length;
                    break;

                default:
                    // Unknown magic: resync onto the next packet we recognise.
                    var next = FindNextMagic(pending, offset + 1);

                    if (next < 0) {
                        pending.RemoveRange(0, pending.Count);
                        return;
                    }

                    offset = next;
                    continue;
            }

            if (size <= 0 || size > 1_000_000) {
                var resync = FindNextMagic(pending, offset + 1);

                if (resync < 0) {
                    pending.RemoveRange(0, pending.Count);
                    return;
                }

                offset = resync;
                continue;
            }

            if (pending.Count - offset < size) {
                break;
            }

            if (magic == "PRNT") {
                HandlePrint(span[..size]);
            } else if (magic == "CHAN") {
                HandleChannels(span[..size]);
            }

            offset += size;
        }

        if (offset > 0) {
            pending.RemoveRange(0, offset);
        }
    }

    private static int FindNextMagic(List<byte> pending, int from) {
        string[] magics = ["PRNT", "CHAN", "CVAR", "AINF"];

        for (var i = from; i <= pending.Count - 4; i++) {
            var candidate = Encoding.ASCII.GetString([pending[i], pending[i + 1], pending[i + 2], pending[i + 3]]);

            if (magics.Contains(candidate)) {
                return i;
            }
        }

        return -1;
    }

    private void HandlePrint(ReadOnlySpan<byte> packet) {
        // magic(4) protocol(2) length(4) reserved(2) channelHash(4) padding(24) message
        var hash = BinaryPrimitives.ReadUInt32LittleEndian(packet[12..]);
        var message = Encoding.UTF8.GetString(packet[40..]).TrimEnd('\0', '\n', '\r');

        if (message.Length == 0) {
            return;
        }

        var channel = channels.TryGetValue(hash, out var name) ? name : $"0x{hash:X8}";
        var seq = Interlocked.Increment(ref sequence);

        buffer.Enqueue(new ConsoleLine(seq, DateTime.Now, channel, StripColorCodes(message)));

        while (buffer.Count > MaxLines && buffer.TryDequeue(out _)) {
            // trim oldest
        }
    }

    private void HandleChannels(ReadOnlySpan<byte> packet) {
        var count = (int)BinaryPrimitives.ReadUInt32BigEndian(packet[12..]);

        for (var i = 0; i < count; i++) {
            var record = packet[(16 + (i * 58))..];

            if (record.Length < 58) {
                return;
            }

            var hash = BinaryPrimitives.ReadUInt32LittleEndian(record);
            var name = Encoding.ASCII.GetString(record[24..54]).TrimEnd('\0');

            if (name.Length > 0) {
                channels[hash] = name;
            }
        }
    }

    /// <summary>
    /// The client only names a channel in a CHAN packet when its set of known channels
    /// changes, so most PRNT hashes arrive unresolved. Channels are "script:&lt;resource&gt;",
    /// and the hash is a plain Joaat, so pre-compute names for every resource the log has
    /// mentioned starting.
    /// </summary>
    private void SeedChannelNamesFromLog() {
        try {
            var log = logs.Read(null, 0, "Creating script environments for", 5000);

            foreach (var line in log.Lines) {
                var idx = line.LastIndexOf(' ');

                if (idx < 0 || idx == line.Length - 1) {
                    continue;
                }

                var resource = line[(idx + 1)..].Trim();

                if (resource.Length > 0) {
                    channels.TryAdd(Joaat($"script:{resource}"), $"script:{resource}");
                }
            }

            foreach (var known in new[] { "Any", "font-renderer", "cmd", "mumble", "voip-mumble", "nui" }) {
                channels.TryAdd(Joaat(known), known);
            }
        } catch (Exception ex) {
            logger.LogDebug(ex, "could not seed channel names from the log");
        }
    }

    /// <summary>CitizenFX HashString: Joaat over the lowercased string (client/shared/Utils.h).</summary>
    private static uint Joaat(string text) {
        var hash = 0u;

        foreach (var ch in text.ToLowerInvariant()) {
            hash += ch;
            hash += hash << 10;
            hash ^= hash >> 6;
        }

        hash += hash << 3;
        hash ^= hash >> 11;
        hash += hash << 15;

        return hash;
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
