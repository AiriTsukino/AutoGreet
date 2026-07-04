using System.Globalization;
using System.Text.RegularExpressions;
using AutoGreet.Models;

namespace AutoGreet.Services;

public sealed class MacroSyntaxException : Exception
{
    public MacroSyntaxException(string message) : base(message)
    {
    }
}

public sealed class MacroEngine
{
    private static readonly Regex InlineWaitRegex = new(@"<wait\.(?<seconds>\d+(?:\.\d+)?)>", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    private readonly Configuration config;
    private readonly GreetingService greetings;
    private readonly ChatCommandService chatCommands;
    private readonly TargetingService targeting;
    private readonly DiagnosticLogService logs;

    public MacroEngine(Configuration config, GreetingService greetings, ChatCommandService chatCommands, TargetingService targeting, DiagnosticLogService logs)
    {
        this.config = config;
        this.greetings = greetings;
        this.chatCommands = chatCommands;
        this.targeting = targeting;
        this.logs = logs;
    }

    public async Task ExecuteAsync(VisitorKey target, GreetingMacro macro, Func<bool> stillPresent, CancellationToken token, bool markVisitorGreeted = true)
    {
        var parsedLines = ValidateAndParse(macro);

        foreach (var parsed in parsedLines)
        {
            token.ThrowIfCancellationRequested();

            if (!stillPresent())
                throw new OperationCanceledException("Target left before greeting completed.", token);

            if (parsed.WaitOnlySeconds is not null)
            {
                await DelaySecondsAsync(parsed.WaitOnlySeconds.Value, token).ConfigureAwait(false);
                continue;
            }

            var command = BuildCommand(parsed.Line, target, out var tellText);
            if (tellText is not null)
                greetings.RegisterExpectedTell(target, tellText, markVisitorGreeted);

            if (parsed.Kind == MacroLineKind.Dote)
            {
                var targetedForEmote = config.WaitForVisibleTargetBeforeEmote
                    ? await targeting.WaitForTargetAsync(target, stillPresent, config.EmoteTargetWaitSeconds, token).ConfigureAwait(false)
                    : await targeting.TargetAsync(target, token).ConfigureAwait(false);

                if (!targetedForEmote)
                    throw new InvalidOperationException($"Could not target visible player {target.Display} for /dote.");

                await Task.Delay(200, token).ConfigureAwait(false);
            }

            await SendCommandAsync(command, token).ConfigureAwait(false);
            if (tellText is not null)
                greetings.ConfirmTellCommandSent(target, tellText);

            if (parsed.InlineWaitSeconds is not null)
                await DelaySecondsAsync(parsed.InlineWaitSeconds.Value, token).ConfigureAwait(false);
            else
                await Task.Delay(250, token).ConfigureAwait(false);
        }
    }

    public IReadOnlyList<MacroSyntaxIssue> Validate(GreetingMacro macro)
    {
        var issues = new List<MacroSyntaxIssue>();
        var lineNumber = 0;

        foreach (var raw in macro.Script.Replace("\r", string.Empty).Split('\n'))
        {
            lineNumber++;
            var original = raw.Trim();
            if (string.IsNullOrWhiteSpace(original))
                continue;

            var line = original;
            var inlineWaitSeconds = ExtractInlineWaitSeconds(ref line);
            if (inlineWaitSeconds is null && original.Contains("<wait.", StringComparison.OrdinalIgnoreCase))
            {
                issues.Add(new MacroSyntaxIssue(lineNumber, original, "Inline wait was not recognized. Use <wait.1>, <wait.2>, or <wait.02>."));
                continue;
            }

            if (string.IsNullOrWhiteSpace(line))
                continue;

            if (TryParseWaitCommand(line, out _))
                continue;

            if (IsTellToTarget(line) || IsDote(line))
                continue;

            issues.Add(new MacroSyntaxIssue(lineNumber, original, "This line does not match AutoGreet's supported macro syntax."));
        }

        return issues;
    }

    private List<ParsedMacroLine> ValidateAndParse(GreetingMacro macro)
    {
        var parsed = new List<ParsedMacroLine>();
        var lineNumber = 0;

        foreach (var raw in macro.Script.Replace("\r", string.Empty).Split('\n'))
        {
            lineNumber++;
            var original = raw.Trim();
            if (string.IsNullOrWhiteSpace(original))
                continue;

            var line = original;
            var inlineWaitSeconds = ExtractInlineWaitSeconds(ref line);
            if (inlineWaitSeconds is null && original.Contains("<wait.", StringComparison.OrdinalIgnoreCase))
                ThrowSyntax(macro, lineNumber, original, "Inline wait was not recognized. Use <wait.1>, <wait.2>, or <wait.02>.");

            if (string.IsNullOrWhiteSpace(line))
            {
                if (inlineWaitSeconds is not null)
                    parsed.Add(new ParsedMacroLine(MacroLineKind.Wait, string.Empty, inlineWaitSeconds.Value, null));
                continue;
            }

            if (TryParseWaitCommand(line, out var waitSeconds))
            {
                parsed.Add(new ParsedMacroLine(MacroLineKind.Wait, string.Empty, waitSeconds, null));
                if (inlineWaitSeconds is not null)
                    parsed.Add(new ParsedMacroLine(MacroLineKind.Wait, string.Empty, inlineWaitSeconds.Value, null));
                continue;
            }

            if (IsTellToTarget(line))
            {
                parsed.Add(new ParsedMacroLine(MacroLineKind.Tell, line, null, inlineWaitSeconds));
                continue;
            }

            if (IsDote(line))
            {
                parsed.Add(new ParsedMacroLine(MacroLineKind.Dote, line, null, inlineWaitSeconds));
                continue;
            }

            ThrowSyntax(macro, lineNumber, original, "This line does not match AutoGreet's supported macro syntax.");
        }

        return parsed;
    }

    private void ThrowSyntax(GreetingMacro macro, int lineNumber, string line, string message)
    {
        logs.MacroSyntaxError(macro.Name, lineNumber, line, message + "\n\n" + DiagnosticLogService.SupportedSyntaxText);
        throw new MacroSyntaxException($"Macro syntax error in '{macro.Name}', line {lineNumber}.");
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

        if (TryGetTellText(line, out tellText))
            return $"/tell {tellTarget} {tellText}";

        if (IsDote(line))
            return "/dote <t>";

        return line;
    }

    private static bool IsTellToTarget(string line) => TryGetTellText(line, out _);

    private static bool TryGetTellText(string line, out string? tellText)
    {
        tellText = null;

        if (line.StartsWith("/tell <t>", StringComparison.OrdinalIgnoreCase))
        {
            tellText = line[9..].Trim();
            return !string.IsNullOrWhiteSpace(tellText);
        }

        if (line.StartsWith("/t <t>", StringComparison.OrdinalIgnoreCase))
        {
            tellText = line[6..].Trim();
            return !string.IsNullOrWhiteSpace(tellText);
        }

        return false;
    }

    private static bool IsDote(string line)
        => line.Equals("/dote", StringComparison.OrdinalIgnoreCase)
           || line.Equals("/dote <t>", StringComparison.OrdinalIgnoreCase);

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

        var rest = line[5..];
        if (rest.Length > 0 && !char.IsWhiteSpace(rest[0]) && rest[0] != '.' && !char.IsDigit(rest[0]))
            return false;

        rest = rest.Trim();
        if (rest.StartsWith(".", StringComparison.Ordinal))
            rest = rest[1..].Trim();

        // Support all of these forms:
        // /wait
        // /wait 1
        // /wait1
        // /wait.1
        // /wait.02
        if (string.IsNullOrWhiteSpace(rest))
            return true;

        var firstToken = rest.Split(new[] { " " }, StringSplitOptions.RemoveEmptyEntries).FirstOrDefault() ?? string.Empty;
        if (!double.TryParse(firstToken, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed))
            return false;

        seconds = Math.Clamp(parsed, 0, 10);
        return true;
    }

    private static Task DelaySecondsAsync(double seconds, CancellationToken token)
        => Task.Delay(TimeSpan.FromSeconds(Math.Clamp(seconds, 0, 10)), token);

    private enum MacroLineKind
    {
        Tell,
        Dote,
        Wait,
    }

    private sealed record ParsedMacroLine(MacroLineKind Kind, string Line, double? WaitOnlySeconds, double? InlineWaitSeconds);
}

public sealed record MacroSyntaxIssue(int LineNumber, string LineText, string Message);
