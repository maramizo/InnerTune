using System.Diagnostics;
using System.IO;
using System.Text.RegularExpressions;

namespace InnerTune;

public static partial class WindowsPathEnvironment
{
    public static void RefreshCurrentProcess()
    {
        var path = BuildEffectivePath(
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User),
            Environment.GetEnvironmentVariable("PATH"));
        if (!string.IsNullOrWhiteSpace(path)) Environment.SetEnvironmentVariable("PATH", path);
    }

    public static void ApplyFreshPath(ProcessStartInfo start)
    {
        var path = BuildEffectivePath(
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User),
            Environment.GetEnvironmentVariable("PATH"));
        if (!string.IsNullOrWhiteSpace(path)) start.Environment["PATH"] = path;
    }

    public static string BuildEffectivePath(string? machinePath, string? userPath, string? processPath)
    {
        var machine = Expand(machinePath, "");
        var user = Expand(userPath, machine);
        var process = Expand(processPath, machine);
        return string.Join(Path.PathSeparator, Split(machine)
            .Concat(Split(user))
            .Concat(Split(process))
            .Distinct(StringComparer.OrdinalIgnoreCase));
    }

    private static string Expand(string? value, string pathReplacement)
    {
        if (string.IsNullOrWhiteSpace(value)) return "";
        var withoutPathReference = PathReference().Replace(value, pathReplacement);
        return Environment.ExpandEnvironmentVariables(withoutPathReference);
    }

    private static IEnumerable<string> Split(string value) => value
        .Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
        .Select(entry => entry.Trim().Trim('"'))
        .Where(entry => entry.Length > 0);

    [GeneratedRegex("%PATH%", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)]
    private static partial Regex PathReference();
}
