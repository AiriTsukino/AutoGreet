using System.Globalization;
using System.Text.RegularExpressions;
using AutoGreet.Models;

namespace AutoGreet.Services;

public sealed class MacroEngine
{
    private static readonly Regex InlineWaitRegex = new(@"<wait\.(?<seconds>\d+(?:\.\d+)?)>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly GreetingService greetings;
    private readonly ChatCommandService chatCommands;
    private readonly TargetingService targeting;
    public MacroEngine(GreetingService greetings, ChatCommandService chatCommands, TargetingService targeting)
    {
        this.greetings = greetings;
        this.chatCommands = chatCommands;
        this.targeting = targeting;
    }

    public async Task ExecuteAsync(VisitorKey target, GreetingMacro macro, Func<bool> stillPresent, CancellationToken token)
    {
            foreach (var raw in macro.Script.Replace("\r", string.Empty).Split('\n'))
            {
                token.ThrowIfCancellationRequested();
                if (!stillPresent()) throw new OperationCanceledException("Target left before greeting completed.", token);

                var line = raw.Trim();
                if (string.IsNullOrWhiteSpace(line)) continue;

                var inlineWaitSeconds = ExtractInlineWaitSeconds(ref line);
                if (string.IsNullOrWhiteSpace(line))
                {
                    if (inlineWaitSeconds is not null)
                        await DelaySecondsAsync(inlineWaitSeconds.Value, token).ConfigureAwait(false);
                    continue;
                }

                if (TryParseWaitCommand(line, out var waitSeconds))
                {
                    await DelaySecondsAsync(waitSeconds, token).ConfigureAwait(false);
                    if (inlineWaitSeconds is not null)
                        await DelaySecondsAsync(inlineWaitSeconds.Value, token).ConfigureAwait(false);
                    continue;
                }

                var command = BuildCommand(line, target, out var tellText);
                if (tellText is not null) greetings.RegisterExpectedTell(target, tellText);

                if (line.StartsWith("/dote", StringComparison.OrdinalIgnoreCase))
                {
                    var targetedForEmote = await targeting.TargetAsync(target, token).ConfigureAwait(false);
                    if (!targetedForEmote)
                        throw new InvalidOperationException($"Could not target visible player {target.Display} for /dote.");
                    await Task.Delay(200, token).ConfigureAwait(false);
                }

                await SendCommandAsync(command, token).ConfigureAwait(false);
                if (tellText is not null) greetings.ConfirmTellCommandSent(target, tellText);

                if (inlineWaitSeconds is not null)
                    await DelaySecondsAsync(inlineWaitSeconds.Value, token).ConfigureAwait(false);
                else
                    await Task.Delay(250, token).ConfigureAwait(false);
            }
    }

    private async Task SendCommandAsync(string command, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        greetings.LastCommandText = command;
        var sent = await chatCommands.SendAsync(command, token).ConfigureAwait(false);
        if (!sent)
            throw new InvalidOperationException($"Chat command was not sent: {chatCommands.LastError}");
    }

    private static string BuildCommand(string line, VisitorKey target, out string? tellText)
    {
        tellText = null;
        var tellTarget = $"{target.Name}@{target.World}";

        if (line.StartsWith("/tell <t>", StringComparison.OrdinalIgnoreCase))
        {
            tellText = line[9..].Trim();
            return $"/tell {tellTarget} {tellText}";
        }

        if (line.StartsWith("/dote", StringComparison.OrdinalIgnoreCase))
            return "/dote <t>";

        return line;
    }

    private static double? ExtractInlineWaitSeconds(ref string line)
    {
        var matches = InlineWaitRegex.Matches(line);
        if (matches.Count == 0) return null;

        double? seconds = null;
        foreach (Match match in matches)
        {
            if (double.TryParse(match.Groups["seconds"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
                seconds = Math.Clamp(parsed, 0, 10);
        }

        line = InlineWaitRegex.Replace(line, string.Empty).Trim();
        return seconds;
    }

    private static bool TryParseWaitCommand(string line, out double seconds)
    {
        seconds = 1;

        if (!line.StartsWith("/wait", StringComparison.OrdinalIgnoreCase))
            return false;

        var rest = line[5..].Trim();
        if (rest.StartsWith(".", StringComparison.Ordinal)) rest = rest[1..].Trim();

        // Support all of these forms:
        // /wait
        // /wait 1
        // /wait1
        // /wait.1
        if (string.IsNullOrWhiteSpace(rest))
            return true;

        var firstToken = rest.Split(new[] { " " }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        if (double.TryParse(firstToken, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            seconds = Math.Clamp(parsed, 0, 10);

        return true;
    }

    private static Task DelaySecondsAsync(double seconds, CancellationToken token)
        => Task.Delay(TimeSpan.FromSeconds(Math.Clamp(seconds, 0, 10)), token);
}
