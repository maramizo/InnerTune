using System.Text.Json;
using InnerTune;

using var updates = new UpdateService(new Version(1, 0, 9));
var available = await updates.CheckAsync() ?? throw new InvalidOperationException("No newer release was found.");
var downloaded = await updates.DownloadAsync(available);
var installer = new FileInfo(downloaded.InstallerPath!);
Console.WriteLine(JsonSerializer.Serialize(new
{
    currentVersion = updates.CurrentVersion.ToString(),
    availableVersion = available.Version.ToString(),
    available.Tag,
    installer.Name,
    installer.Length,
    checksumVerified = installer.Exists && installer.Length > 0
}));
