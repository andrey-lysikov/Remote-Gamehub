//  Copyright © AndreyLysikov
//  SPDX-License-Identifier: Apache-2.0

using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using RemoteGameHub.App;

namespace RemoteGameHub.Media;

// A small picture of the captured screen for the status page. GDI, not a second duplication,
// so a running stream is left alone.
internal static class ScreenSnapshot
{
    private const int MaxWidth = 1280;
    private const long JpegQuality = 75;

    // A JPEG, or null while Windows withholds the screen (the lock screen, a prompt for rights).
    internal static byte[]? Take(AppConfig config)
    {
        try
        {
            var output = DisplayInventory.Select(DisplayInventory.Enumerate(), config.Output,
                                                 config.VirtualDisplay, out _);
            var area = output is null
                ? Screen.PrimaryScreen?.Bounds ?? Rectangle.Empty
                : new Rectangle(output.Bounds.Left, output.Bounds.Top, output.Bounds.Width, output.Bounds.Height);

            if (area.Width <= 0 || area.Height <= 0) return null;

            using var full = new Bitmap(area.Width, area.Height, PixelFormat.Format24bppRgb);
            using (var canvas = Graphics.FromImage(full))
                canvas.CopyFromScreen(area.Location, System.Drawing.Point.Empty, area.Size);

            var scale = Math.Min(1.0, (double)MaxWidth / area.Width);
            using var small = new Bitmap(Math.Max(1, (int)(area.Width * scale)),
                                         Math.Max(1, (int)(area.Height * scale)),
                                         PixelFormat.Format24bppRgb);
            using (var canvas = Graphics.FromImage(small))
            {
                canvas.InterpolationMode = InterpolationMode.HighQualityBilinear;
                canvas.DrawImage(full, 0, 0, small.Width, small.Height);
            }

            var jpeg = ImageCodecInfo.GetImageEncoders().First(codec => codec.FormatID == ImageFormat.Jpeg.Guid);
            using var settings = new EncoderParameters(1);
            settings.Param[0] = new EncoderParameter(System.Drawing.Imaging.Encoder.Quality, JpegQuality);

            using var bytes = new MemoryStream();
            small.Save(bytes, jpeg, settings);
            return bytes.ToArray();
        }
        catch (Exception)
        {
            return null;
        }
    }
}
