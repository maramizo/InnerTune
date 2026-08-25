using System.Text.Json;
using System.Diagnostics;
using InnerTune;

try
{
    var command = CodexLocator.Resolve();
    var start = new ProcessStartInfo
    {
        FileName = command.FileName,
        UseShellExecute = false,
        RedirectStandardOutput = true,
        RedirectStandardError = true,
        CreateNoWindow = true
    };
    foreach (var prefix in command.PrefixArguments) start.ArgumentList.Add(prefix);
    start.ArgumentList.Add("--version");
    using var process = Process.Start(start) ?? throw new InvalidOperationException("The discovered Codex command could not start.");
    var output = await process.StandardOutput.ReadToEndAsync();
    var error = await process.StandardError.ReadToEndAsync();
    await process.WaitForExitAsync();
    if (process.ExitCode != 0) throw new InvalidOperationException(string.IsNullOrWhiteSpace(error) ? $"Codex exited with {process.ExitCode}." : error.Trim());
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        found = true,
        command.FileName,
        command.PrefixArguments,
        command.DiscoveredFrom,
        version = output.Trim()
    }));
}
catch (Exception error)
{
    Console.Error.WriteLine(error.Message);
    Environment.ExitCode = 1;
}
