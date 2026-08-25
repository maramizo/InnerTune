using System.IO;
using System.Security.Cryptography;
using System.Text;

namespace InnerTune;

public static class AppRuntime
{
    private static readonly object LogGate = new();
    public const string TestModeVariable = "INNERTUNE_TEST_MODE";
    public const string TestInstanceVariable = "INNERTUNE_TEST_INSTANCE";
    public const string DataDirectoryVariable = "ITMUSIC_DATA_DIR";
    public const string TestShareActivation = "test:share-current";
    public const string TestCaptureDirectoryVariable = "INNERTUNE_TEST_CAPTURE_DIR";
    public const string TestCaptureViewVariable = "INNERTUNE_TEST_CAPTURE_VIEW";
    public const string TestIconFrameVariable = "INNERTUNE_TEST_ICON_FRAME";
    public const string TestEqualizerLevelVariable = "INNERTUNE_TEST_EQUALIZER_LEVEL";
    public const string TestExpandActiveQueueVariable = "INNERTUNE_TEST_EXPAND_ACTIVE_QUEUE";

    public static bool IsTestMode { get; } =
        string.Equals(Environment.GetEnvironmentVariable(TestModeVariable), "1", StringComparison.OrdinalIgnoreCase);

    public static string DefaultDataDirectory { get; } =
        Path.GetFullPath(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "InnerTune"));

    public static string DataDirectory { get; } = ResolveDataDirectory();

    public static string InstanceKey { get; } = ResolveInstanceKey();

    public static bool HasSafeTestConfiguration => !IsTestMode ||
        (!string.IsNullOrWhiteSpace(Environment.GetEnvironmentVariable(TestInstanceVariable)) &&
         !string.Equals(DataDirectory.TrimEnd(Path.DirectorySeparatorChar),
             DefaultDataDirectory.TrimEnd(Path.DirectorySeparatorChar), StringComparison.OrdinalIgnoreCase));

    public static double AudibleVolume(double requested) => IsTestMode ? 0 : Math.Clamp(requested, 0, 100);

    public static void TestLog(string message)
    {
        if (!IsTestMode || !HasSafeTestConfiguration) return;
        try
        {
            lock (LogGate)
            {
                Directory.CreateDirectory(DataDirectory);
                File.AppendAllText(Path.Combine(DataDirectory, "test.log"), $"{DateTimeOffset.Now:O} {message}{Environment.NewLine}");
            }
        }
        catch { }
    }

    private static string ResolveDataDirectory()
    {
        var configured = Environment.GetEnvironmentVariable(DataDirectoryVariable);
        return Path.GetFullPath(string.IsNullOrWhiteSpace(configured) ? DefaultDataDirectory : configured);
    }

    private static string ResolveInstanceKey()
    {
        if (!IsTestMode) return "v1";
        var requested = Environment.GetEnvironmentVariable(TestInstanceVariable) ?? "missing";
        var material = $"{requested}|{DataDirectory}";
        return "test-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)))[..16].ToLowerInvariant();
    }
}
