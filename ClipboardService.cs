using System.IO;

namespace InnerTune;

public static class ClipboardService
{
    private static string TestClipboardPath => Path.Combine(AppRuntime.DataDirectory, "test-clipboard.txt");

    public static void SetText(string value)
    {
        if (AppRuntime.IsTestMode)
        {
            Directory.CreateDirectory(AppRuntime.DataDirectory);
            File.WriteAllText(TestClipboardPath, value);
            return;
        }
        System.Windows.Clipboard.SetText(value);
    }

    public static string? GetText()
    {
        if (AppRuntime.IsTestMode)
            return File.Exists(TestClipboardPath) ? File.ReadAllText(TestClipboardPath) : null;
        try { return System.Windows.Clipboard.ContainsText() ? System.Windows.Clipboard.GetText() : null; }
        catch { return null; }
    }
}
