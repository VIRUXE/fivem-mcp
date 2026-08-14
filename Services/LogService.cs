using System.Text;
using System.Text.RegularExpressions;

namespace FiveMMcp.Services;

public sealed record LogRead(string File, long Cursor, long FileLength, string[] Lines, bool Truncated);

/// <summary>
/// Reads the FiveM client log, which carries the same stream the F8 console renders:
/// connection progress, resource loading, script prints, Lua errors and warnings.
/// </summary>
public sealed partial class LogService {
    private static readonly string LogDirectory = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "FiveM", "FiveM.app", "logs");

    [GeneratedRegex(@"\^[0-9]")]
    private static partial Regex ColorCodes { get; }

    public string? FindLatestLog() {
        if (!Directory.Exists(LogDirectory)) {
            return null;
        }

        return new DirectoryInfo(LogDirectory)
            .GetFiles("CitizenFX_log_*.log")
            .OrderByDescending(f => f.LastWriteTimeUtc)
            .FirstOrDefault()?.FullName;
    }

    /// <summary>
    /// Reads from the newest log file. With <paramref name="sinceCursor"/> set, returns only
    /// bytes appended since that offset; otherwise returns the last <paramref name="tailLines"/> lines.
    /// The returned cursor can be passed to the next call to follow the log incrementally.
    /// </summary>
    public LogRead Read(long? sinceCursor, int tailLines, string? filterRegex, int maxLines) {
        var path = FindLatestLog()
            ?? throw new InvalidOperationException($"No FiveM client logs found under {LogDirectory}.");

        // The client keeps the log open for writing, so share both read and write.
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite);
        var length = stream.Length;

        long start;
        if (sinceCursor is { } cursor) {
            // A shorter file means the client restarted and rolled to a new log.
            start = cursor > length ? 0 : cursor;
        } else {
            start = 0;
        }

        stream.Seek(start, SeekOrigin.Begin);
        using var reader = new StreamReader(stream, Encoding.UTF8, detectEncodingFromByteOrderMarks: false);
        var text = reader.ReadToEnd();

        var lines = text.Split('\n', StringSplitOptions.RemoveEmptyEntries)
            .Select(l => ColorCodes.Replace(l.TrimEnd('\r'), string.Empty))
            .ToArray();

        if (sinceCursor is null && tailLines > 0 && lines.Length > tailLines) {
            lines = lines[^tailLines..];
        }

        if (!string.IsNullOrWhiteSpace(filterRegex)) {
            var rx = new Regex(filterRegex, RegexOptions.IgnoreCase);
            lines = [.. lines.Where(l => rx.IsMatch(l))];
        }

        var truncated = lines.Length > maxLines;
        if (truncated) {
            lines = lines[^maxLines..];
        }

        return new LogRead(path, length, length, lines, truncated);
    }
}
