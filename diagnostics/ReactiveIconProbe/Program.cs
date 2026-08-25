using System.Security.Cryptography;
using System.Text.Json;
using System.IO;
using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using InnerTune;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        var outputDirectory = args.FirstOrDefault() ?? Path.Combine(Path.GetTempPath(), "InnerTuneReactiveIconProbe");
        Directory.CreateDirectory(outputDirectory);
        var frames = ReactiveDjIconRenderer.CreateFrames();
        var hashes = frames.Select(Hash).ToArray();
        var selected = new[]
        {
            0,
            AudioIconAnimator.Encode(1, 0),
            AudioIconAnimator.Encode(2, 1),
            AudioIconAnimator.Encode(3, 0),
            AudioIconAnimator.Encode(3, 2),
            AudioIconAnimator.Encode(4, 4),
            AudioIconAnimator.Encode(5, 0),
            AudioIconAnimator.Encode(5, 2),
            AudioIconAnimator.Encode(5, 6)
        };
        foreach (var index in selected) Save(frames[index], Path.Combine(outputDirectory, $"frame-{index:D2}.png"));
        var animationDirectory = Path.Combine(outputDirectory, "peak-animation");
        Directory.CreateDirectory(animationDirectory);
        for (var phase = 0; phase < AudioIconAnimator.PhaseCount; phase++)
        {
            var frame = AudioIconAnimator.Encode(AudioIconAnimator.LevelCount - 1, phase);
            Save(frames[frame], Path.Combine(animationDirectory, $"frame-{phase:D2}.png"));
        }

        const int cell = 148;
        var visual = new DrawingVisual();
        using (var drawing = visual.RenderOpen())
        {
            drawing.DrawRectangle(new SolidColorBrush(Color.FromRgb(8, 8, 12)), null, new Rect(0, 0, cell * 3, cell * 3));
            for (var index = 0; index < selected.Length; index++)
            {
                var left = index % 3 * cell;
                var top = index / 3 * cell;
                drawing.DrawRoundedRectangle(new SolidColorBrush(Color.FromRgb(24, 23, 31)), null, new Rect(left + 5, top + 5, 138, 138), 18, 18);
                drawing.DrawImage(frames[selected[index]], new Rect(left + 10, top + 10, 128, 128));
            }
        }
        var contact = new RenderTargetBitmap(cell * 3, cell * 3, 96, 96, PixelFormats.Pbgra32);
        contact.Render(visual);
        contact.Freeze();
        var contactPath = Path.Combine(outputDirectory, "contact-sheet.png");
        Save(contact, contactPath);
        var idlePath = Path.Combine(outputDirectory, "idle.png");
        var iconPath = Path.Combine(outputDirectory, "app.ico");
        Save(frames[0], idlePath);
        SaveIcon(frames[0], iconPath);

        var passed = frames.Length == AudioIconAnimator.FrameCount && hashes.Distinct().Count() == frames.Length;
        Console.WriteLine(JsonSerializer.Serialize(new
        {
            passed,
            frameCount = frames.Length,
            uniqueFrames = hashes.Distinct().Count(),
            selected,
            contactSheet = contactPath,
            idle = idlePath,
            icon = iconPath
        }));
        return passed ? 0 : 1;
    }

    private static string Hash(BitmapSource bitmap)
    {
        var pixels = new byte[bitmap.PixelHeight * bitmap.BackBufferStride()];
        bitmap.CopyPixels(pixels, bitmap.BackBufferStride(), 0);
        return Convert.ToHexString(SHA256.HashData(pixels));
    }

    private static int BackBufferStride(this BitmapSource bitmap) => (bitmap.PixelWidth * bitmap.Format.BitsPerPixel + 7) / 8;

    private static void Save(BitmapSource bitmap, string path)
    {
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = File.Create(path);
        encoder.Save(output);
    }

    private static void SaveIcon(BitmapSource source, string path)
    {
        int[] sizes = [16, 20, 24, 32, 40, 48, 64, 128, 256];
        var images = sizes.Select(size => EncodePng(source, size)).ToArray();
        using var output = new BinaryWriter(File.Create(path));
        output.Write((ushort)0);
        output.Write((ushort)1);
        output.Write((ushort)sizes.Length);
        var offset = 6 + sizes.Length * 16;
        for (var index = 0; index < sizes.Length; index++)
        {
            output.Write((byte)(sizes[index] >= 256 ? 0 : sizes[index]));
            output.Write((byte)(sizes[index] >= 256 ? 0 : sizes[index]));
            output.Write((byte)0);
            output.Write((byte)0);
            output.Write((ushort)1);
            output.Write((ushort)32);
            output.Write(images[index].Length);
            output.Write(offset);
            offset += images[index].Length;
        }
        foreach (var image in images) output.Write(image);
    }

    private static byte[] EncodePng(BitmapSource source, int size)
    {
        var visual = new DrawingVisual();
        RenderOptions.SetBitmapScalingMode(visual, BitmapScalingMode.HighQuality);
        using (var drawing = visual.RenderOpen()) drawing.DrawImage(source, new Rect(0, 0, size, size));
        var bitmap = new RenderTargetBitmap(size, size, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        bitmap.Freeze();
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        using var output = new MemoryStream();
        encoder.Save(output);
        return output.ToArray();
    }
}
