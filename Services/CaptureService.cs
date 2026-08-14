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

    /// <summary>
    /// Captures the game window, optionally cropped to a window-relative region.
    /// Returns PNG bytes plus the pixel size actually delivered.
    /// </summary>
    public (byte[] Png, int Width, int Height) Capture(
        int? regionX, int? regionY, int? regionWidth, int? regionHeight, int maxWidth) {
        // Screen capture reads whatever is drawn on top, so the game has to be
        // in front or we photograph whichever window is covering it.
        if (windows.EnsureFocused() is { } focusError) {
            throw new InvalidOperationException(focusError);
        }

        Thread.Sleep(150);

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
