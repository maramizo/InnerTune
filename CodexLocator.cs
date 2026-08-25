using Microsoft.Win32;
using System.IO;

namespace InnerTune;

public sealed record CodexCommand(string FileName, IReadOnlyList<string> PrefixArguments, string DiscoveredFrom)
{
    public static CodexCommand Direct(string path, string source) => new(path, [], source);
}

public static class CodexLocator
{
    public const string OverrideVariable = "INNERTUNE_CODEX_PATH";

    public static CodexCommand Resolve()
    {
        var checkedLocations = new List<string>();
        var overridePath = Environment.GetEnvironmentVariable(OverrideVariable);
        if (!string.IsNullOrWhiteSpace(overridePath))
        {
            var overridden = ResolveCandidate(Environment.ExpandEnvironmentVariables(overridePath), "environment override", checkedLocations);
            if (overridden is not null) return overridden;
            throw new InvalidOperationException($"{OverrideVariable} points to a Codex installation that cannot be used: {overridePath}");
        }

        foreach (var appPath in ReadAppPaths())
        {
            var resolved = ResolveCandidate(appPath, "Windows App Paths", checkedLocations);
            if (resolved is not null) return resolved;
        }

        foreach (var candidate in StandardCandidates())
        {
            var resolved = ResolveCandidate(candidate.Path, candidate.Source, checkedLocations);
            if (resolved is not null) return resolved;
        }

        foreach (var directory in PathDirectories())
        {
            foreach (var name in new[] { "codex.exe", "codex.cmd", "codex.bat", "codex.ps1" })
            {
                var resolved = ResolveCandidate(Path.Combine(directory, name), "PATH", checkedLocations);
                if (resolved is not null) return resolved;
            }
        }

        throw new InvalidOperationException(
            "Codex CLI was not found. Install it from https://developers.openai.com/codex/cli, restart InnerTune, and try again. " +
            $"For a portable/custom installation, set {OverrideVariable} to codex.exe. Checked {checkedLocations.Count} locations.");
    }

    private static CodexCommand? ResolveCandidate(string candidate, string source, List<string> checkedLocations)
    {
        if (string.IsNullOrWhiteSpace(candidate)) return null;
        string path;
        try { path = Path.GetFullPath(candidate.Trim().Trim('"')); }
        catch { return null; }
        if (!checkedLocations.Contains(path, StringComparer.OrdinalIgnoreCase)) checkedLocations.Add(path);
        if (!File.Exists(path)) return null;

        var extension = Path.GetExtension(path);
        if (extension.Equals(".exe", StringComparison.OrdinalIgnoreCase) || string.IsNullOrEmpty(extension))
            return CodexCommand.Direct(path, source);

        if (extension.Equals(".cmd", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".bat", StringComparison.OrdinalIgnoreCase) ||
            extension.Equals(".ps1", StringComparison.OrdinalIgnoreCase))
            return ResolvePackageShim(path, source, checkedLocations);

        return null;
    }

    private static CodexCommand? ResolvePackageShim(string shim, string source, List<string> checkedLocations)
    {
        var shimDirectory = Path.GetDirectoryName(shim);
        if (string.IsNullOrWhiteSpace(shimDirectory)) return null;
        var package = Path.Combine(shimDirectory, "node_modules", "@openai", "codex");
        if (!Directory.Exists(package)) return null;

        try
        {
            var native = Directory.EnumerateFiles(package, "codex.exe", SearchOption.AllDirectories).FirstOrDefault();
            if (!string.IsNullOrWhiteSpace(native)) return CodexCommand.Direct(native, $"{source} package");
        }
        catch { }

        var script = Path.Combine(package, "bin", "codex.js");
        if (!File.Exists(script)) return null;
        var node = ResolveNode(shimDirectory, checkedLocations);
        return node is null ? null : new(node, [script], $"{source} npm shim");
    }

    private static string? ResolveNode(string shimDirectory, List<string> checkedLocations)
    {
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var candidates = new[]
        {
            Path.Combine(shimDirectory, "node.exe"),
            Path.Combine(programFiles, "nodejs", "node.exe")
        }.Concat(PathDirectories().Select(directory => Path.Combine(directory, "node.exe")));
        foreach (var candidate in candidates)
        {
            if (!checkedLocations.Contains(candidate, StringComparer.OrdinalIgnoreCase)) checkedLocations.Add(candidate);
            if (File.Exists(candidate)) return candidate;
        }
        return null;
    }

    private static IEnumerable<(string Path, string Source)> StandardCandidates()
    {
        var local = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
        var roaming = Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData);
        var profile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
        var programFiles = Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles);
        var pnpm = Environment.GetEnvironmentVariable("PNPM_HOME");
        yield return (Path.Combine(local, "Programs", "OpenAI", "Codex", "bin", "codex.exe"), "OpenAI Windows installer");
        yield return (Path.Combine(local, "OpenAI", "Codex", "bin", "codex.exe"), "OpenAI Windows installer");
        yield return (Path.Combine(profile, ".local", "bin", "codex.exe"), "standalone installer");
        yield return (Path.Combine(local, "Microsoft", "WinGet", "Links", "codex.exe"), "WinGet");
        yield return (Path.Combine(local, "Microsoft", "WindowsApps", "codex.exe"), "WindowsApps");
        yield return (Path.Combine(programFiles, "OpenAI", "Codex", "bin", "codex.exe"), "system installation");
        foreach (var name in new[] { "codex.exe", "codex.cmd", "codex.ps1" })
            yield return (Path.Combine(roaming, "npm", name), "npm global installation");
        if (!string.IsNullOrWhiteSpace(pnpm))
            foreach (var name in new[] { "codex.exe", "codex.cmd", "codex.ps1" })
                yield return (Path.Combine(pnpm, name), "pnpm installation");
    }

    private static IEnumerable<string> PathDirectories()
    {
        var values = new[]
        {
            Environment.GetEnvironmentVariable("PATH"),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.User),
            Environment.GetEnvironmentVariable("PATH", EnvironmentVariableTarget.Machine)
        };
        return values.Where(value => !string.IsNullOrWhiteSpace(value))
            .SelectMany(value => value!.Split(Path.PathSeparator, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Select(value => Environment.ExpandEnvironmentVariables(value.Trim('"')))
            .Where(Directory.Exists)
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }

    private static IEnumerable<string> ReadAppPaths()
    {
        const string key = @"Software\Microsoft\Windows\CurrentVersion\App Paths\codex.exe";
        foreach (var hive in new[] { RegistryHive.CurrentUser, RegistryHive.LocalMachine })
        foreach (var view in new[] { RegistryView.Registry64, RegistryView.Registry32 })
        {
            RegistryKey? baseKey = null;
            RegistryKey? appKey = null;
            try
            {
                baseKey = RegistryKey.OpenBaseKey(hive, view);
                appKey = baseKey.OpenSubKey(key);
                if (appKey?.GetValue(null) is string value && !string.IsNullOrWhiteSpace(value)) yield return value;
            }
            finally
            {
                appKey?.Dispose();
                baseKey?.Dispose();
            }
        }
    }
}
