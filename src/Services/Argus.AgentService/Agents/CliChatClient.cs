namespace Argus.AgentService.Agents;

using System.Diagnostics;
using System.Runtime.CompilerServices;
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
        var escapedPrompt = prompt.Replace("\"", "\\\"");
        var (filename, args) = tool.ToLowerInvariant() switch
        {
            "claude" => ("claude", $"--model {model} -p \"{escapedPrompt}\""),
            "opencode" => ("opencode", $"run --model {model} --prompt-text \"{escapedPrompt}\""),
            "codex" => ("codex", $"--model {model} \"{escapedPrompt}\""),
            "openai" => ("openai", $"api chat.completions.create -m {model} -g user \"{escapedPrompt}\""),
            "gemini" => ("gemini", $"--model {model} --prompt \"{escapedPrompt}\""),
            _ => throw new ArgumentException($"Unknown CLI tool: {tool}")
        };

        var psi = new ProcessStartInfo
        {
            FileName = filename,
            Arguments = args,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true
        };

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

    private static string BuildPrompt(IList<ChatMessage> messages)
    {
        if (messages.Count == 1)
            return messages[0].Text ?? string.Empty;

        return string.Join("\n\n", messages.Select(m => $"[{m.Role}]: {m.Text}"));
    }
}
