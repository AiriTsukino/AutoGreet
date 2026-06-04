using System.Collections;
using System.Globalization;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Runtime.CompilerServices;

namespace AutoGreet.Services;

/// <summary>
/// Best-effort helper for resuming the local player's persistent emote after AutoGreet sends greeting emotes.
/// Dalamud does not expose a simple high-level "current emote command" API, so this service uses conservative
/// reflection against client-struct data and the Lumina Emote sheet. If the current emote cannot be identified,
/// it safely does nothing and records a diagnostic status.
/// </summary>
public sealed class EmoteResumeService
{
    private readonly Configuration config;
    private readonly ChatCommandService chatCommands;

    public string LastStatus { get; private set; } = "Resume Previous Emote has not run yet.";
    public string LastCapturedCommand { get; private set; } = string.Empty;
    public string LastResumedCommand { get; private set; } = string.Empty;

    public EmoteResumeService(Configuration config, ChatCommandService chatCommands)
    {
        this.config = config;
        this.chatCommands = chatCommands;
    }

    public async Task<string?> CaptureAsync(CancellationToken token)
    {
        if (!config.ResumePreviousEmoteEnabled)
        {
            LastStatus = "Resume Previous Emote is disabled.";
            LastCapturedCommand = string.Empty;
            return null;
        }

        token.ThrowIfCancellationRequested();

        try
        {
            var command = await DalamudServices.Framework.RunOnFrameworkThread(CaptureOnFrameworkThread).ConfigureAwait(false);
            LastCapturedCommand = command ?? string.Empty;
            LastStatus = command is null
                ? "No resumable persistent emote was detected before greeting."
                : $"Captured previous emote command: {command}";
            return command;
        }
        catch (Exception ex)
        {
            LastCapturedCommand = string.Empty;
            LastStatus = $"Could not capture previous emote: {ex.Message}";
            DalamudServices.Log.Warning(ex, "AutoGreet could not capture the previous emote.");
            return null;
        }
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
                LastStatus = $"Resumed previous emote: {command}";
            }
            else
            {
                LastStatus = $"Could not resume previous emote: {chatCommands.LastError}";
            }
        }
        catch (OperationCanceledException)
        {
            LastStatus = "Resume previous emote was cancelled.";
        }
        catch (Exception ex)
        {
            LastStatus = $"Could not resume previous emote: {ex.Message}";
            DalamudServices.Log.Warning(ex, "AutoGreet could not resume the previous emote.");
        }
    }

    private string? CaptureOnFrameworkThread()
    {
        var local = DalamudServices.ObjectTable.LocalPlayer;
        if (local is null)
            return null;

        var address = local.Address;
        if (address == nint.Zero)
            return null;

        var characterType = FindLoadedType("FFXIVClientStructs.FFXIV.Client.Game.Character.Character");
        if (characterType is null)
        {
            LastStatus = "Client character type was unavailable.";
            return null;
        }

        object? character;
        try
        {
            character = Marshal.PtrToStructure(address, characterType);
        }
        catch
        {
            return null;
        }

        if (character is null) return null;

        var candidates = FindPossibleEmoteIds(character)
            .Where(id => id != 0)
            .Distinct()
            .Select(id => new EmoteResumeCandidate(id, TryGetEmoteCommand(id)))
            .Where(c => IsResumeCommand(c.Command))
            .GroupBy(c => NormalizeCommand(c.Command!))
            .Select(g => g.OrderByDescending(c => ScoreResumeCandidate(c)).First())
            .OrderByDescending(ScoreResumeCandidate)
            .ToArray();

        if (candidates.Length == 0)
            return null;

        var chosen = candidates[0];
        if (candidates.Length > 1)
        {
            var summary = string.Join(", ", candidates.Take(6).Select(c => $"{NormalizeCommand(c.Command!)}#{c.EmoteId}"));
            DalamudServices.Log.Debug("AutoGreet emote resume candidates: {Candidates}. Chose {Chosen}.", summary, NormalizeCommand(chosen.Command!));
        }

        return NormalizeCommand(chosen.Command!);
    }

    private readonly record struct EmoteResumeCandidate(uint EmoteId, string? Command);

    private static int ScoreResumeCandidate(EmoteResumeCandidate candidate)
    {
        var command = NormalizeCommand(candidate.Command ?? string.Empty).ToLowerInvariant();
        if (string.IsNullOrWhiteSpace(command)) return int.MinValue;

        var score = 0;

        // More specific emotes usually have longer slash commands and higher row IDs than the generic
        // base emotes that share similar animation state. This helps avoid /golddance being reduced to
        // /dance when both show up in reflected state candidates.
        score += Math.Min(command.Length, 40) * 10;
        score += (int)Math.Min(candidate.EmoteId, 5000);

        if (GenericAmbiguousResumeCommands.Contains(command))
            score -= 2500;

        if (DanceResumeCommands.Contains(command))
            score += 600;

        return score;
    }

    private static IEnumerable<uint> FindPossibleEmoteIds(object root)
    {
        var seen = new HashSet<object>(ReferenceEqualityComparer.Instance);
        foreach (var id in FindPossibleEmoteIdsRecursive(root, 0, seen))
            yield return id;
    }

    private static IEnumerable<uint> FindPossibleEmoteIdsRecursive(object? value, int depth, HashSet<object> seen)
    {
        if (value is null || depth > 4) yield break;

        var type = value.GetType();
        if (!type.IsValueType && !seen.Add(value)) yield break;

        // A common client-struct pattern is Character.Mode + Character.ModeParam. Only trust ModeParam
        // when the mode name looks emote-related.
        var mode = GetMemberValue(value, "Mode");
        var modeText = mode?.ToString() ?? string.Empty;
        if (modeText.Contains("Emote", StringComparison.OrdinalIgnoreCase) || modeText.Contains("Pose", StringComparison.OrdinalIgnoreCase))
        {
            var modeParam = GetMemberValue(value, "ModeParam");
            if (TryGetUInt(modeParam, out var modeParamId))
                yield return modeParamId;
        }

        foreach (var member in GetReadableMembers(type))
        {
            var name = member.Name;
            object? memberValue;
            try { memberValue = GetValue(member, value); }
            catch { continue; }

            if (memberValue is null) continue;

            var lower = name.ToLowerInvariant();
            var nameLooksLikeEmoteId = lower.Contains("emote") && (lower.Contains("id") || lower.EndsWith("emote", StringComparison.Ordinal));
            if (nameLooksLikeEmoteId && TryGetUInt(memberValue, out var id))
            {
                yield return id;
                continue;
            }

            // Recurse into likely controller/state structs only. This avoids accidentally treating unrelated
            // numeric character fields as emote row IDs.
            if (lower.Contains("emote") || lower.Contains("pose") || lower.Contains("mode") || lower.Contains("timeline"))
            {
                foreach (var nestedId in FindPossibleEmoteIdsRecursive(memberValue, depth + 1, seen))
                    yield return nestedId;
            }
        }
    }

    private string? TryGetEmoteCommand(uint emoteId)
    {
        try
        {
            var emoteType = FindLoadedType("Lumina.Excel.Sheets.Emote");
            if (emoteType is null) return null;

            var sheet = GetExcelSheet(emoteType);
            if (sheet is null) return null;

            var row = GetExcelRow(sheet, emoteId);
            if (row is null) return null;

            var textCommandRef = GetMemberValue(row, "TextCommand");
            var textCommand = ResolveRowRef(textCommandRef);
            if (textCommand is null) return null;

            var commandValue = GetMemberValue(textCommand, "Command");
            var command = commandValue?.ToString();
            return string.IsNullOrWhiteSpace(command) ? null : command;
        }
        catch (Exception ex)
        {
            DalamudServices.Log.Debug(ex, "AutoGreet could not resolve emote command for row {EmoteId}.", emoteId);
            return null;
        }
    }

    private static object? GetExcelSheet(Type rowType)
    {
        var methods = DalamudServices.DataManager.GetType()
            .GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .Where(m => m.Name == "GetExcelSheet" && m.IsGenericMethodDefinition)
            .OrderBy(m => m.GetParameters().Length);

        foreach (var method in methods)
        {
            try
            {
                var generic = method.MakeGenericMethod(rowType);
                var parameters = method.GetParameters();
                object?[] args = parameters.Length == 0 ? Array.Empty<object?>() : parameters.Select(_ => (object?)null).ToArray();
                return generic.Invoke(DalamudServices.DataManager, args);
            }
            catch
            {
                // Try the next overload.
            }
        }

        return null;
    }

    private static object? GetExcelRow(object sheet, uint rowId)
    {
        var sheetType = sheet.GetType();
        var getRow = sheetType.GetMethods(BindingFlags.Instance | BindingFlags.Public)
            .FirstOrDefault(m => m.Name == "GetRow" && m.GetParameters().Length >= 1);
        if (getRow is null) return null;

        var first = getRow.GetParameters()[0];
        object idArg = first.ParameterType == typeof(uint) ? rowId : Convert.ChangeType(rowId, first.ParameterType, CultureInfo.InvariantCulture);
        object?[] args = getRow.GetParameters().Length == 1
            ? new object?[] { idArg }
            : getRow.GetParameters().Select((_, i) => i == 0 ? idArg : null).ToArray();

        return getRow.Invoke(sheet, args);
    }

    private static object? ResolveRowRef(object? rowRef)
    {
        if (rowRef is null) return null;
        foreach (var name in new[] { "Value", "ValueNullable" })
        {
            var value = GetMemberValue(rowRef, name);
            if (value is not null) return value;
        }
        return rowRef;
    }

    private static readonly HashSet<string> GenericAmbiguousResumeCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "/dance",
        "/sit",
        "/groundsit",
        "/doze",
        "/changepose",
        "/cpose",
    };

    private static readonly HashSet<string> DanceResumeCommands = new(StringComparer.OrdinalIgnoreCase)
    {
        "/beesknees",
        "/golddance",
        "/mandervilledance",
        "/moonlift",
        "/sundrop",
        "/thavnairiandance",
        "/yol dance",
        "/edance",
        "/stepdance",
        "/harvestdance",
        "/balldance",
        "/sidedance",
        "/boxstep",
        "/getfantasy",
        "/hum",
        "/popotostep",
        "/laliho",
    };

    private static bool IsResumeCommand(string? command)
    {
        if (string.IsNullOrWhiteSpace(command)) return false;
        command = NormalizeCommand(command);
        if (!command.StartsWith('/')) return false;

        // Avoid commands that are not persistent emotes or that AutoGreet itself may run while greeting.
        // /visor is especially easy to mis-detect from character state, so keep it out of resume logic.
        var blockedCommands = new[]
        {
            "/dote",
            "/tell",
            "/target",
            "/visor",
            "/battlemode",
            "/bm",
            "/draw",
            "/sheathe",
            "/automove",
        };

        return !blockedCommands.Any(blocked => command.Equals(blocked, StringComparison.OrdinalIgnoreCase));
    }

    private static string NormalizeCommand(string command)
    {
        command = command.Trim();
        var space = command.IndexOf(' ');
        return space > 0 ? command[..space] : command;
    }

    private static Type? FindLoadedType(string fullName)
    {
        foreach (var assembly in AppDomain.CurrentDomain.GetAssemblies())
        {
            try
            {
                var type = assembly.GetType(fullName, false, false);
                if (type is not null) return type;
            }
            catch
            {
                // Ignore dynamic/reflection-only assembly issues.
            }
        }

        return Type.GetType(fullName, false, false);
    }

    private static IEnumerable<MemberInfo> GetReadableMembers(Type type)
    {
        foreach (var field in type.GetFields(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic))
            yield return field;
        foreach (var prop in type.GetProperties(BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic).Where(p => p.GetIndexParameters().Length == 0))
            yield return prop;
    }

    private static object? GetMemberValue(object value, string memberName)
    {
        var type = value.GetType();
        var field = type.GetField(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        if (field is not null) return field.GetValue(value);
        var prop = type.GetProperty(memberName, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);
        return prop?.GetValue(value);
    }

    private static object? GetValue(MemberInfo member, object instance) => member switch
    {
        FieldInfo field => field.GetValue(instance),
        PropertyInfo prop => prop.GetValue(instance),
        _ => null,
    };

    private static bool TryGetUInt(object? value, out uint result)
    {
        result = 0;
        if (value is null) return false;

        try
        {
            switch (value)
            {
                case byte b: result = b; return true;
                case sbyte sb when sb >= 0: result = (uint)sb; return true;
                case short s when s >= 0: result = (uint)s; return true;
                case ushort us: result = us; return true;
                case int i when i >= 0: result = (uint)i; return true;
                case uint ui: result = ui; return true;
                case long l when l >= 0 && l <= uint.MaxValue: result = (uint)l; return true;
                case ulong ul when ul <= uint.MaxValue: result = (uint)ul; return true;
                default:
                    if (value.GetType().IsEnum)
                    {
                        result = Convert.ToUInt32(value, CultureInfo.InvariantCulture);
                        return true;
                    }
                    return false;
            }
        }
        catch
        {
            return false;
        }
    }

    private sealed class ReferenceEqualityComparer : IEqualityComparer<object>
    {
        public static readonly ReferenceEqualityComparer Instance = new();
        public new bool Equals(object? x, object? y) => ReferenceEquals(x, y);
        public int GetHashCode(object obj) => RuntimeHelpers.GetHashCode(obj);
    }
}
