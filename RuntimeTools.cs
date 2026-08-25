using System.IO;

namespace InnerTune;

internal static class RuntimeTools
{
    public static string Node => BundledOrPath("node.exe");
    public static string Ffmpeg => BundledOrPath("ffmpeg.exe");

    private static string BundledOrPath(string fileName)
    {
        var bundled = Path.Combine(AppContext.BaseDirectory, "runtime", fileName);
        return File.Exists(bundled) ? bundled : fileName;
    }
}
