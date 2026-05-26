namespace Argus.AgentService.Agents;

using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Extensions.AI;

/// <summary>
/// IChatClient implementation backed by a CLI subprocess (claude, opencode, codex, gemini, openai).
/// </summary>
public sealed class CliChatClient(string tool, string model) : IChatClient
{
    public Task<ChatResponse> GetResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        CancellationToken cancellationToken = default)
    {
        return GetResponseImplAsync(chatMessages, options, cancellationToken);
    }

    private async Task<ChatResponse> GetResponseImplAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options,
        CancellationToken cancellationToken)
    {
        var prompt = BuildPrompt(chatMessages.ToList());
        var output = await InvokeAsync(prompt, cancellationToken);
        return new ChatResponse(new ChatMessage(ChatRole.Assistant, output));
    }

    public async IAsyncEnumerable<ChatResponseUpdate> GetStreamingResponseAsync(
        IEnumerable<ChatMessage> chatMessages,
        ChatOptions? options = null,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var response = await GetResponseImplAsync(chatMessages, options, cancellationToken);
        yield return new ChatResponseUpdate(ChatRole.Assistant, response.Text);
    }

    public object? GetService(Type serviceType, object? key = null) => null;

    public void Dispose() { }

    private async Task<string> InvokeAsync(string prompt, CancellationToken ct)
    {
        var psi = new ProcessStartInfo
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

        AddToolArguments(psi, prompt);

        using var process = new Process { StartInfo = psi };
        process.Start();
        var output = await process.StandardOutput.ReadToEndAsync(ct);

        if (!await Task.Run(() => process.WaitForExit(120_000), ct))
        {
            process.Kill();
            throw new TimeoutException($"CLI tool '{tool}' timed out after 120s");
        }

        if (process.ExitCode != 0)
        {
            var error = await process.StandardError.ReadToEndAsync(ct);
            throw new InvalidOperationException($"CLI tool '{tool}' exited {process.ExitCode}: {error}");
        }

        return output;
    }

    private void AddToolArguments(ProcessStartInfo psi, string prompt)
    {
        switch (tool.ToLowerInvariant())
        {
            case "claude":
                psi.FileName = "claude";
                psi.ArgumentList.Add("--settings");
                psi.ArgumentList.Add(ClaudeStatusLineSettings());
                psi.ArgumentList.Add("--model");
                psi.ArgumentList.Add(model);
                psi.ArgumentList.Add("-p");
                psi.ArgumentList.Add(prompt);
                psi.Environment["ARGUS_CLAUDE_STATUS_PATH"] = "/tmp/argus-claude-status.json";
                break;
            case "opencode":
                psi.FileName = "opencode";
                psi.ArgumentList.Add("run");
                psi.ArgumentList.Add("-m");
                psi.ArgumentList.Add(model);
                psi.ArgumentList.Add(prompt);
                break;
            case "codex":
                psi.FileName = "codex";
                psi.ArgumentList.Add("--model");
                psi.ArgumentList.Add(model);
                psi.ArgumentList.Add(prompt);
                break;
            case "openai":
                psi.FileName = "openai";
                psi.ArgumentList.Add("api");
                psi.ArgumentList.Add("chat.completions.create");
                psi.ArgumentList.Add("-m");
                psi.ArgumentList.Add(model);
                psi.ArgumentList.Add("-g");
                psi.ArgumentList.Add("user");
                psi.ArgumentList.Add(prompt);
                break;
            case "gemini":
                psi.FileName = "gemini";
                psi.ArgumentList.Add("--model");
                psi.ArgumentList.Add(model);
                psi.ArgumentList.Add("--prompt");
                psi.ArgumentList.Add(prompt);
                break;
            default:
                throw new ArgumentException($"Unknown CLI tool: {tool}");
        }
    }

    private static string BuildPrompt(IList<ChatMessage> messages)
    {
        if (messages.Count == 1)
            return messages[0].Text ?? string.Empty;

        return string.Join("\n\n", messages.Select(m => $"[{m.Role}]: {m.Text}"));
    }

    private static string ClaudeStatusLineSettings()
    {
        var script = Path.Combine(AppContext.BaseDirectory, "scripts", "argus-claude-statusline.py");
        return JsonSerializer.Serialize(new
        {
            statusLine = new
            {
                type = "command",
                command = $"python3 {script}",
                refreshInterval = 15,
                padding = 0
            }
        });
    }
}
