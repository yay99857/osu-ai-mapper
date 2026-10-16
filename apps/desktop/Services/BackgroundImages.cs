using Avalonia;
using Avalonia.Media.Imaging;

namespace OsuAi.Desktop.Services;

/// <summary>
/// osu! shows the background filling a 16:9 screen, cropping whatever does not fit and wasting the
/// pixels of a huge image. A picture of another shape, or wider than 2560 px, is cropped at the
/// centre to 16:9, scaled to at most 1920×1080 and saved as a JPEG before it goes into the project.
/// </summary>
public static class BackgroundImages
{
    /// <summary>The path of a fitted JPEG in the temporary folder, or null when the image is fine as it is.</summary>
    public static string? FitTo169(string imagePath)
    {
        using var source = new Bitmap(imagePath);
        var size = source.PixelSize;
        double aspect = (double)size.Width / size.Height;
        if (Math.Abs(aspect / (16.0 / 9) - 1) < 0.02 && size.Width <= 2560)
            return null;

        // The largest centred 16:9 rectangle inside the picture.
        int cropWidth = Math.Min(size.Width, (int)Math.Round(size.Height * 16.0 / 9));
        int cropHeight = (int)Math.Round(cropWidth * 9.0 / 16);
        var crop = new Rect((size.Width - cropWidth) / 2.0, (size.Height - cropHeight) / 2.0, cropWidth, cropHeight);

        int width = Math.Min(1920, cropWidth);
        var target = new PixelSize(width, (int)Math.Round(width * 9.0 / 16));
        using var fitted = new RenderTargetBitmap(target);
        using (var context = fitted.CreateDrawingContext())
            context.DrawImage(source, crop, new Rect(0, 0, target.Width, target.Height));

        string folder = Path.Combine(Path.GetTempPath(), "osu-ai-mapper", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        string output = Path.Combine(folder, $"{Path.GetFileNameWithoutExtension(imagePath)} 16x9.jpg");
        using (var file = File.Create(output))
            fitted.Save(file, new JpegBitmapEncoderOptions { Quality = 92 });
        return output;
    }
}
