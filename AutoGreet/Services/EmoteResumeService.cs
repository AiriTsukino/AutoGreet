namespace AutoGreet.Services;

/// <summary>
/// Runs a user-configured slash command after AutoGreet finishes a greeting queue/manual greet.
/// </summary>
public sealed class EmoteResumeService
{
    private readonly Configuration config;
    private readonly ChatCommandService chatCommands;

    public string LastStatus { get; private set; } = "Resume emote has not run yet.";
    public string LastCapturedCommand { get; private set; } = string.Empty;
    public string LastResumedCommand { get; private set; } = string.Empty;

    public EmoteResumeService(Configuration config, ChatCommandService chatCommands)
    {
        this.config = config;
        this.chatCommands = chatCommands;
    }

    public Task<string?> CaptureAsync(CancellationToken token)
    {
        token.ThrowIfCancellationRequested();

        if (!config.ResumePreviousEmoteEnabled)
        {
            LastStatus = "Resume emote is disabled.";
            LastCapturedCommand = string.Empty;
            return Task.FromResult<string?>(null);
        }

        var command = NormalizeConfiguredCommand(config.ResumeEmoteCommand);
        LastCapturedCommand = command ?? string.Empty;
        LastStatus = command is null
            ? "Resume emote is enabled, but no slash command is configured."
            : $"Configured resume emote command: {command}";
        return Task.FromResult(command);
    }

    public async Task ResumeAsync(string? command, CancellationToken token)
    {
        if (!config.ResumePreviousEmoteEnabled || string.IsNullOrWhiteSpace(command)) return;
        if (token.IsCancellationRequested) return;

        try
        {
            await Task.Delay(350, token).ConfigureAwait(false);
            var sent = await chatCommands.SendAsync(command, token).ConfigureAwait(false);
            if (sent)
            {
                LastResumedCommand = command;
                LastStatus = $"Resumed configured emote: {command}";
            }
            else
            {
                LastStatus = $"Could not resume configured emote: {chatCommands.LastError}";
            }
        }
        catch (OperationCanceledException)
        {
            LastStatus = "Resume emote was cancelled.";
        }
        catch (Exception ex)
        {
            LastStatus = $"Could not resume configured emote: {ex.Message}";
            DalamudServices.Log.Warning(ex, "AutoGreet could not resume the configured emote.");
        }
    }

    private static string? NormalizeConfiguredCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return null;

        command = command.Trim();
        if (!command.StartsWith('/'))
            command = "/" + command;

        return command.Length <= 1 ? null : command;
    }
}
