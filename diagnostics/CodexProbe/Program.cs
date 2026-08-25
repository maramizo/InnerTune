using System.Text.Json;
using InnerTune;

try
{
    var command = CodexLocator.Resolve();
    Console.WriteLine(JsonSerializer.Serialize(new
    {
        found = true,
        command.FileName,
        command.PrefixArguments,
        command.DiscoveredFrom
    }));
}
catch (Exception error)
{
    Console.Error.WriteLine(error.Message);
    Environment.ExitCode = 1;
}
