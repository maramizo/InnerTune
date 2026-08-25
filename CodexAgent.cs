using System.Diagnostics;
using System.IO;
using System.Text;
using System.Text.Json;

namespace InnerTune;

public sealed record AgentActivity(string Role, string Text, bool IsFinal = false);

public sealed class CodexAgent
{
    private Process? _process;
    private string? _threadId;
    public bool IsRunning => _process is { HasExited: false };
    public event EventHandler<AgentActivity>? Activity;

    public async Task<string> RunAsync(string request, string dataDirectory, CancellationToken token = default)
    {
        if (IsRunning) throw new InvalidOperationException("The music assistant is already working.");
        var server = Path.Combine(AppContext.BaseDirectory, "provider", "mcp-server.mjs");
        if (!File.Exists(server)) throw new InvalidOperationException("The AI action bridge is missing. Run setup.ps1 once.");
        var codex = CodexLocator.Resolve();

        const string prompt = """
            You are the music assistant inside InnerTune, a local Windows music player.
            Use the innertune MCP tools for every search, queue/library change, and playback action.
            Inspect state and search iteratively. Adding, replacing, loading, or choosing Play next NEVER interrupts the current song.
            Only call play_song or control_playback when explicitly asked to start or control playback. Match requested counts exactly and avoid duplicates unless asked.
            Keep the user informed with concise commentary before and between tool calls so the live activity transcript explains your approach.
            Finish with a short friendly summary of the useful result or actions taken.
            Do not narrate expected non-events such as playback staying unchanged, unless the user explicitly asks for that confirmation.
            """;
        var start = new ProcessStartInfo
        {
            FileName = codex.FileName,
            UseShellExecute = false,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            StandardOutputEncoding = Encoding.UTF8,
            StandardErrorEncoding = Encoding.UTF8,
            CreateNoWindow = true,
            WorkingDirectory = dataDirectory
        };
        foreach (var prefix in codex.PrefixArguments) start.ArgumentList.Add(prefix);
        var resumeThread = _threadId;
        start.ArgumentList.Add("exec");
        if (!string.IsNullOrWhiteSpace(resumeThread)) start.ArgumentList.Add("resume");
        foreach (var arg in new[] { "--model", "gpt-5.6-luna", "--ignore-user-config", "--skip-git-repo-check", "--json" }) start.ArgumentList.Add(arg);
        start.ArgumentList.Add("-c"); start.ArgumentList.Add("service_tier=\"fast\"");
        if (string.IsNullOrWhiteSpace(resumeThread))
        {
            start.ArgumentList.Add("--sandbox"); start.ArgumentList.Add("read-only");
            start.ArgumentList.Add("--color"); start.ArgumentList.Add("never");
        }
        start.ArgumentList.Add("-c"); start.ArgumentList.Add($"mcp_servers.innertune.command={Quote(RuntimeTools.Node)}");
        start.ArgumentList.Add("-c"); start.ArgumentList.Add($"mcp_servers.innertune.args=[{Quote(server)}]");
        start.ArgumentList.Add("-c"); start.ArgumentList.Add($"mcp_servers.innertune.env={{ITMUSIC_DATA_DIR={Quote(dataDirectory)}}}");
        start.ArgumentList.Add("-c"); start.ArgumentList.Add("mcp_servers.innertune.required=true");
        start.ArgumentList.Add("-c"); start.ArgumentList.Add("mcp_servers.innertune.default_tools_approval_mode=\"approve\"");
        start.ArgumentList.Add("-c"); start.ArgumentList.Add("mcp_servers.innertune.tool_timeout_sec=120");
        if (string.IsNullOrWhiteSpace(resumeThread))
            start.ArgumentList.Add($"{prompt}\n\nUser request: {request}");
        else
        {
            start.ArgumentList.Add(resumeThread);
            start.ArgumentList.Add(request);
        }

        var errors = new List<string>();
        var finalResponse = "";
        try
        {
            _process = Process.Start(start) ?? throw new InvalidOperationException("Could not start Codex CLI.");
            using var registration = token.Register(Cancel);
            var errorTask = ReadErrorsAsync(_process, errors, token);

            while (await _process.StandardOutput.ReadLineAsync(token) is { } line)
            {
                if (string.IsNullOrWhiteSpace(line)) continue;
                foreach (var activity in ParseEvent(line))
                {
                    if (activity.IsFinal) finalResponse = activity.Text;
                    Activity?.Invoke(this, activity);
                }
            }

            await errorTask;
            await _process.WaitForExitAsync(token);
            if (_process.ExitCode != 0)
                throw new InvalidOperationException(errors.LastOrDefault() ?? $"Codex exited with status {_process.ExitCode}.");

            if (string.IsNullOrWhiteSpace(finalResponse))
            {
                finalResponse = "Done.";
                Activity?.Invoke(this, new("Luna", finalResponse, true));
            }
            return finalResponse;
        }
        finally
        {
            _process?.Dispose();
            _process = null;
        }
    }

    private async Task ReadErrorsAsync(Process process, List<string> errors, CancellationToken token)
    {
        while (await process.StandardError.ReadLineAsync(token) is { } line)
        {
            if (string.IsNullOrWhiteSpace(line)) continue;
            errors.Add(line);
            Activity?.Invoke(this, new("Diagnostic", line));
        }
    }

    private IEnumerable<AgentActivity> ParseEvent(string line)
    {
        JsonDocument? document = null;
        try { document = JsonDocument.Parse(line); }
        catch { }
        if (document is null)
        {
            yield return new("Event", line);
            yield break;
        }

        using (document)
        {
            var root = document.RootElement;
            var type = String(root, "type");
            switch (type)
            {
                case "thread.started":
                    var thread = String(root, "thread_id");
                    var firstThread = string.IsNullOrWhiteSpace(_threadId);
                    var changedThread = !firstThread && !string.IsNullOrWhiteSpace(thread) && !string.Equals(_threadId, thread, StringComparison.OrdinalIgnoreCase);
                    if (!string.IsNullOrWhiteSpace(thread)) _threadId = thread;
                    if (firstThread) yield return new("Session", "Conversation started");
                    else if (changedThread) yield return new("Session", "A new conversation was started because the previous one was unavailable.");
                    break;
                case "turn.started":
                    yield return new("Thinking", "Working through your request…");
                    break;
                case "item.started":
                case "item.completed":
                    if (root.TryGetProperty("item", out var item))
                        foreach (var activity in ParseItem(item, type == "item.completed")) yield return activity;
                    else
                        yield return new("Event", Pretty(root));
                    break;
                case "turn.completed":
                    yield return new("Usage", FormatUsage(root));
                    break;
                case "turn.failed":
                case "error":
                    yield return new("Error", FirstText(root, "message", "error", "detail") ?? Pretty(root));
                    break;
                default:
                    yield return new("Event", Pretty(root));
                    break;
            }
        }
    }

    private static IEnumerable<AgentActivity> ParseItem(JsonElement item, bool completed)
    {
        var type = String(item, "type");
        switch (type)
        {
            case "agent_message":
                if (completed)
                {
                    var message = FirstText(item, "text", "message", "content");
                    if (!string.IsNullOrWhiteSpace(message)) yield return new("Luna", message, true);
                }
                else yield return new("Thinking", "Writing a response…");
                break;

            case "reasoning":
                var reasoning = FirstText(item, "text", "summary", "content");
                if (!string.IsNullOrWhiteSpace(reasoning)) yield return new("Thinking", reasoning);
                else if (!completed) yield return new("Thinking", "Reasoning…");
                break;

            case "command_execution":
                var command = FirstText(item, "command") ?? "Command";
                if (!completed)
                {
                    yield return new("Action", $"Run command\n{command}");
                    break;
                }
                var output = FirstText(item, "aggregated_output", "output") ?? "";
                var exitCode = item.TryGetProperty("exit_code", out var exit) && exit.ValueKind != JsonValueKind.Null ? exit.ToString() : "unknown";
                yield return new("Result", string.IsNullOrWhiteSpace(output)
                    ? $"{command}\nFinished with exit code {exitCode}"
                    : $"{command}\n\n{output.TrimEnd()}\n\nExit code: {exitCode}");
                break;

            case "mcp_tool_call":
                var server = FirstText(item, "server", "server_name");
                var tool = FirstText(item, "tool", "name", "tool_name") ?? "tool";
                var displayName = string.IsNullOrWhiteSpace(server) ? tool : $"{server}.{tool}";
                if (!completed)
                {
                    var arguments = Property(item, "arguments", "input", "params");
                    yield return new("Action", arguments is null ? $"Use {displayName}" : $"Use {displayName}\n{Pretty(arguments.Value)}");
                    break;
                }
                var error = Property(item, "error");
                var result = Property(item, "result", "output", "content");
                if (error is not null && error.Value.ValueKind != JsonValueKind.Null)
                    yield return new("Error", $"{displayName}\n{Pretty(error.Value)}");
                else
                    yield return new("Result", result is null ? $"{displayName} completed" : $"{displayName}\n{Pretty(result.Value)}");
                break;

            case "web_search":
                var query = FirstText(item, "query", "text") ?? "Web search";
                yield return new(completed ? "Result" : "Action", completed ? $"Search completed\n{query}" : $"Search the web\n{query}");
                break;

            case "file_change":
                yield return new(completed ? "Result" : "Action", $"{(completed ? "File changes applied" : "Apply file changes")}\n{Pretty(item)}");
                break;

            case "todo_list":
                yield return new("Plan", Pretty(Property(item, "items") ?? item));
                break;

            default:
                yield return new(completed ? "Result" : "Action", Pretty(item));
                break;
        }
    }

    private static string FormatUsage(JsonElement root)
    {
        if (!root.TryGetProperty("usage", out var usage)) return "Turn complete";
        var input = Number(usage, "input_tokens");
        var cached = Number(usage, "cached_input_tokens");
        var output = Number(usage, "output_tokens");
        var parts = new List<string> { "Turn complete" };
        if (input is not null) parts.Add($"{input:N0} input tokens");
        if (cached is > 0) parts.Add($"{cached:N0} cached");
        if (output is not null) parts.Add($"{output:N0} output tokens");
        return string.Join(" · ", parts);
    }

    private static long? Number(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.TryGetInt64(out var number) ? number : null;

    private static string String(JsonElement element, string name) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";

    private static string? FirstText(JsonElement element, params string[] names)
    {
        foreach (var name in names)
        {
            if (!element.TryGetProperty(name, out var value)) continue;
            if (value.ValueKind == JsonValueKind.String) return value.GetString();
            if (value.ValueKind is JsonValueKind.Object or JsonValueKind.Array) return Pretty(value);
        }
        return null;
    }

    private static JsonElement? Property(JsonElement element, params string[] names)
    {
        foreach (var name in names)
            if (element.TryGetProperty(name, out var value)) return value;
        return null;
    }

    private static string Pretty(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.String) return element.GetString() ?? "";
        return JsonSerializer.Serialize(element, new JsonSerializerOptions { WriteIndented = true });
    }

    public void Cancel() { try { _process?.Kill(true); } catch { } }
    private static string Quote(string value) => JsonSerializer.Serialize(value);
}
