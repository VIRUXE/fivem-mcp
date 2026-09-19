using System.Diagnostics;
using System.Drawing;
using System.Drawing.Drawing2D;
using System.Drawing.Imaging;

namespace FiveMMcp.Services;

/// <summary>
/// Captures the FiveM window's screen region. FiveM runs borderless/windowed, so
/// copying from the screen rectangle is sufficient and avoids the DXGI plumbing
/// that exclusive-fullscreen capture would need.
/// </summary>
public sealed class CaptureService(WindowManager windows) {
    public const int DefaultMaxWidth = 1280;

    /// <summary>A single frame of a burst, with when it was taken relative to the first.</summary>
    public sealed record BurstFrame(byte[] Png, int Width, int Height, int AtMs);

    /// <summary>
    /// Captures the game window, optionally cropped to a window-relative region.
    /// Returns PNG bytes plus the pixel size actually delivered.
    /// </summary>
    public (byte[] Png, int Width, int Height) Capture(
        int? regionX, int? regionY, int? regionWidth, int? regionHeight, int maxWidth) {
        // Screen capture reads whatever is drawn on top, so the game has to be
        // in front or we photograph whichever window is covering it.
        if (windows.EnsureFocused(out var focus) is { } focusError) {
            throw new InvalidOperationException(focusError);
        }

        SettleAfterFocus(focus);

        var info = windows.GetWindow();
        if (!info.Found) {
            throw new InvalidOperationException("FiveM game window not found - is the client running?");
        }

        var client = windows.GetClientRectOnScreen(info)
            ?? throw new InvalidOperationException("Could not resolve the FiveM window's client area.");

        var fullWidth = client.Right - client.Left;
        var fullHeight = client.Bottom - client.Top;
        if (fullWidth <= 0 || fullHeight <= 0) {
            throw new InvalidOperationException("The FiveM window has no visible client area (minimized?).");
        }

        var x = Math.Clamp(regionX ?? 0, 0, fullWidth - 1);
        var y = Math.Clamp(regionY ?? 0, 0, fullHeight - 1);
        var w = Math.Clamp(regionWidth ?? fullWidth, 1, fullWidth - x);
        var h = Math.Clamp(regionHeight ?? fullHeight, 1, fullHeight - y);

        using var shot = new Bitmap(w, h, PixelFormat.Format32bppArgb);
        using (var g = Graphics.FromImage(shot)) {
            g.CopyFromScreen(client.Left + x, client.Top + y, 0, 0, new Size(w, h), CopyPixelOperation.SourceCopy);
        }

        var scaled = Downscale(shot, maxWidth);
        try {
            using var ms = new MemoryStream();
            scaled.Save(ms, ImageFormat.Png);
            return (ms.ToArray(), scaled.Width, scaled.Height);
        } finally {
            if (!ReferenceEquals(scaled, shot)) {
                scaled.Dispose();
            }
        }
    }

    /// <summary>
    /// Captures several frames back to back. Focusing, locating the window and
    /// measuring its client area all happen once, up front, and the frames are
    /// encoded only after the loop - so the loop itself does nothing but copy
    /// pixels. That is what makes it fast enough to catch something that only
    /// appears for a few frames.
    /// </summary>
    /// <param name="onFirstFrame">
    /// Ran immediately after frame 0 is captured, from inside the loop. This is how
    /// you record the consequence of an action: a separate tool call to trigger it
    /// would be seconds too late.
    /// </param>
    public BurstFrame[] CaptureBurst(
        int frames, int intervalMs, int maxWidth, Action? onFirstFrame, CancellationToken ct) {
        if (windows.EnsureFocused(out var focus) is { } focusError) {
            throw new InvalidOperationException(focusError);
        }

        SettleAfterFocus(focus);

        var info = windows.GetWindow();
        if (!info.Found) {
            throw new InvalidOperationException("FiveM game window not found - is the client running?");
        }

        var client = windows.GetClientRectOnScreen(info)
            ?? throw new InvalidOperationException("Could not resolve the FiveM window's client area.");

        var width = client.Right - client.Left;
        var height = client.Bottom - client.Top;
        if (width <= 0 || height <= 0) {
            throw new InvalidOperationException("The FiveM window has no visible client area (minimized?).");
        }

        // Allocated up front so the capture loop never waits on the allocator or the GC.
        var shots = new Bitmap[frames];
        var graphics = new Graphics[frames];
        var takenAt = new int[frames];

        try {
            for (var i = 0; i < frames; i++) {
                shots[i] = new Bitmap(width, height, PixelFormat.Format32bppArgb);
                graphics[i] = Graphics.FromImage(shots[i]);
            }

            var clock = Stopwatch.StartNew();
            for (var i = 0; i < frames; i++) {
                ct.ThrowIfCancellationRequested();

                if (intervalMs > 0 && i > 0) {
                    var due = (long)intervalMs * i;
                    var remaining = due - clock.ElapsedMilliseconds;
                    if (remaining > 0) {
                        Thread.Sleep((int)remaining);
                    }
                }

                takenAt[i] = (int)clock.ElapsedMilliseconds;
                graphics[i].CopyFromScreen(
                    client.Left, client.Top, 0, 0, new Size(width, height), CopyPixelOperation.SourceCopy);

                if (i == 0) {
                    onFirstFrame?.Invoke();
                }
            }

            // Encoding is far slower than copying, so it happens once the window of
            // interest has already been captured.
            var result = new BurstFrame[frames];
            for (var i = 0; i < frames; i++) {
                var scaled = Downscale(shots[i], maxWidth);
                try {
                    using var ms = new MemoryStream();
                    scaled.Save(ms, ImageFormat.Png);
                    result[i] = new BurstFrame(ms.ToArray(), scaled.Width, scaled.Height, takenAt[i]);
                } finally {
                    if (!ReferenceEquals(scaled, shots[i])) {
                        scaled.Dispose();
                    }
                }
            }

            return result;
        } finally {
            foreach (var g in graphics) {
                g?.Dispose();
            }

            foreach (var shot in shots) {
                shot?.Dispose();
            }
        }
    }

    /// <summary>
    /// EnsureFocused already proved the window is foreground, so there is nothing left
    /// to wait for unless we changed something. When we did, what remains is the
    /// desktop composing a frame - and, after un-minimizing, the game rebuilding its
    /// swap chain, which takes considerably longer.
    /// </summary>
    private static void SettleAfterFocus(FocusOutcome outcome) {
        switch (outcome) {
            case FocusOutcome.Activated:
                Thread.Sleep(30);
                break;
            case FocusOutcome.RestoredFromMinimized:
                Thread.Sleep(400);
                break;
        }
    }

    private static Bitmap Downscale(Bitmap source, int maxWidth) {
        if (maxWidth <= 0 || source.Width <= maxWidth) {
            return source;
        }

        var height = (int)Math.Round(source.Height * (maxWidth / (double)source.Width));
        var target = new Bitmap(maxWidth, Math.Max(1, height), PixelFormat.Format32bppArgb);
        using var g = Graphics.FromImage(target);
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        g.DrawImage(source, 0, 0, target.Width, target.Height);
        return target;
    }
}
