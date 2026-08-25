using System.Diagnostics;
using System.IO;
using System.Net.Http;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace InnerTune;

public sealed record AppUpdate(
    Version Version,
    string Tag,
    string InstallerUrl,
    string ChecksumUrl,
    string ReleaseUrl,
    string Notes,
    string? InstallerPath = null);

public sealed class UpdateService : IDisposable
{
    public const string Repository = "maramizo/InnerTune";
    private readonly HttpClient _client = new() { Timeout = Timeout.InfiniteTimeSpan };
    public Version CurrentVersion { get; }

    public UpdateService(Version? currentVersion = null)
    {
        CurrentVersion = currentVersion ?? Assembly.GetExecutingAssembly().GetName().Version ?? new Version(0, 0);
        _client.DefaultRequestHeaders.UserAgent.ParseAdd($"InnerTune/{CurrentVersion}");
        _client.DefaultRequestHeaders.Accept.ParseAdd("application/vnd.github+json");
    }

    public async Task<AppUpdate?> CheckAsync(CancellationToken token = default)
    {
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        timeout.CancelAfter(TimeSpan.FromSeconds(30));
        using var response = await _client.GetAsync($"https://api.github.com/repos/{Repository}/releases/latest", timeout.Token);
        if (response.StatusCode == System.Net.HttpStatusCode.NotFound) return null;
        response.EnsureSuccessStatusCode();
        await using var stream = await response.Content.ReadAsStreamAsync(timeout.Token);
        var release = await JsonSerializer.DeserializeAsync<GitHubRelease>(stream, cancellationToken: timeout.Token)
            ?? throw new InvalidOperationException("GitHub returned an invalid release response.");
        if (!TryVersion(release.TagName, out var version) || version <= CurrentVersion) return null;

        var installerName = $"InnerTune-Setup-{version.Major}.{version.Minor}.{version.Build}.exe";
        var checksumName = installerName + ".sha256";
        var installer = release.Assets.FirstOrDefault(asset => asset.Name.Equals(installerName, StringComparison.OrdinalIgnoreCase));
        var checksum = release.Assets.FirstOrDefault(asset => asset.Name.Equals(checksumName, StringComparison.OrdinalIgnoreCase));
        if (installer is null || checksum is null)
            throw new InvalidOperationException($"Release {release.TagName} is missing its installer or SHA-256 checksum.");
        return new(version, release.TagName, installer.DownloadUrl, checksum.DownloadUrl, release.HtmlUrl, release.Body ?? "");
    }

    public async Task<AppUpdate> DownloadAsync(AppUpdate update, CancellationToken token = default)
    {
        var directory = Path.Combine(AppRuntime.DataDirectory, "updates", update.Tag);
        Directory.CreateDirectory(directory);
        var name = Path.GetFileName(new Uri(update.InstallerUrl).AbsolutePath);
        var destination = Path.Combine(directory, name);
        var temporary = destination + ".download";
        using var checksumTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
        checksumTimeout.CancelAfter(TimeSpan.FromSeconds(30));
        var expected = (await _client.GetStringAsync(update.ChecksumUrl, checksumTimeout.Token))
            .Split((char[]?)null, StringSplitOptions.RemoveEmptyEntries)
            .FirstOrDefault();
        if (expected is null || expected.Length != 64 || !expected.All(Uri.IsHexDigit))
            throw new InvalidOperationException("The release checksum is invalid.");

        if (!File.Exists(destination) || !HashMatches(destination, expected))
        {
            using var downloadTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
            downloadTimeout.CancelAfter(TimeSpan.FromMinutes(15));
            using var response = await _client.GetAsync(update.InstallerUrl, HttpCompletionOption.ResponseHeadersRead, downloadTimeout.Token);
            response.EnsureSuccessStatusCode();
            await using (var input = await response.Content.ReadAsStreamAsync(downloadTimeout.Token))
            await using (var output = File.Create(temporary))
                await input.CopyToAsync(output, downloadTimeout.Token);
            if (!HashMatches(temporary, expected))
            {
                File.Delete(temporary);
                throw new InvalidOperationException("The downloaded update did not match its SHA-256 checksum.");
            }
            File.Move(temporary, destination, true);
        }
        return update with { InstallerPath = destination };
    }

    public static void LaunchInstaller(AppUpdate update)
    {
        if (string.IsNullOrWhiteSpace(update.InstallerPath) || !File.Exists(update.InstallerPath))
            throw new InvalidOperationException("The update installer is not ready.");
        _ = Process.Start(new ProcessStartInfo
        {
            FileName = update.InstallerPath,
            Arguments = "/VERYSILENT /SUPPRESSMSGBOXES /NORESTART /SP- /CLOSEAPPLICATIONS /RESTARTAPPLICATIONS",
            UseShellExecute = true
        }) ?? throw new InvalidOperationException("Windows could not start the update installer.");
    }

    private static bool HashMatches(string path, string expected)
    {
        using var stream = File.OpenRead(path);
        return Convert.ToHexString(SHA256.HashData(stream)).Equals(expected, StringComparison.OrdinalIgnoreCase);
    }

    private static bool TryVersion(string tag, out Version version)
    {
        var value = tag.Trim().TrimStart('v', 'V');
        if (Version.TryParse(value, out var parsed)) { version = parsed; return true; }
        version = new Version(0, 0);
        return false;
    }

    public void Dispose() => _client.Dispose();

    private sealed class GitHubRelease
    {
        [JsonPropertyName("tag_name")] public string TagName { get; set; } = "";
        [JsonPropertyName("html_url")] public string HtmlUrl { get; set; } = "";
        [JsonPropertyName("body")] public string? Body { get; set; }
        [JsonPropertyName("assets")] public List<GitHubAsset> Assets { get; set; } = [];
    }

    private sealed class GitHubAsset
    {
        [JsonPropertyName("name")] public string Name { get; set; } = "";
        [JsonPropertyName("browser_download_url")] public string DownloadUrl { get; set; } = "";
    }
}
