using AutoGreet.Models;
using Dalamud.Game.Text;
using Dalamud.Game.Chat;

namespace AutoGreet.Services;

public sealed class GreetingService : IDisposable
{
    private readonly VenueService venues;
    private readonly Dictionary<string, VisitorKey> pendingTellLines = new(StringComparer.OrdinalIgnoreCase);
    private VisitorService? visitorService;

    public string LastCommandText { get; set; } = "None";
    public string LastOutgoingTellObserved { get; private set; } = "None";
    public string LastGreetingConfirmation { get; private set; } = "None";

    public GreetingService(VenueService venues)
    {
        this.venues = venues;
        DalamudServices.ChatGui.ChatMessage += OnChatMessage;
    }

    public void AttachVisitorService(VisitorService service) => visitorService = service;

    public GreetingMacro? PickMacro(VisitorKey key)
    {
        var venue = venues.ActiveVenueOrNull;
        if (venue is null) return null;
        if (venue.Blacklist.Contains(key.ToString())) return null;
        var profile = venues.GetGreetingProfileForVenue(venue);
        var category = GetCategory(key);
        var selectedId = venue.GetActiveMacroId(category);
        var selected = selectedId == Guid.Empty
            ? null
            : profile.Macros.FirstOrDefault(m => m.Enabled && m.Category == category && m.Id == selectedId);

        return selected
            ?? profile.Macros.FirstOrDefault(m => m.Enabled && m.Category == category)
            ?? profile.Macros.FirstOrDefault(m => m.Enabled && m.Category == GreetingCategory.FirstTime);
    }

    public GreetingCategory GetCategory(VisitorKey key)
    {
        var venue = venues.ActiveVenueOrNull;
        if (venue is null) return GreetingCategory.FirstTime;
        if (venue.Blacklist.Contains(key.ToString())) return GreetingCategory.Blacklisted;
        if (venue.LifetimeVisitors.TryGetValue(key.ToString(), out var visitor) && visitor.Vip) return GreetingCategory.Vip;
        if (venue.LifetimeVisitors.TryGetValue(key.ToString(), out visitor) && visitor.HasBeenGreeted) return GreetingCategory.Returning;
        return GreetingCategory.FirstTime;
    }

    public bool IsConfiguredGreetingTell(string message)
    {
        var normalized = Normalize(message);
        return venues.AllGreetingProfiles
            .SelectMany(x => x.Profile.Macros)
            .SelectMany(GetTellLines)
            .Any(line => Normalize(line).Equals(normalized, StringComparison.OrdinalIgnoreCase));
    }

    public void RegisterExpectedTell(VisitorKey key, string message)
    {
        var normalized = Normalize(message);
        pendingTellLines[normalized] = key;
        LastGreetingConfirmation = $"Waiting for outgoing tell to {key.Display}: {message}";
    }

    public bool IsGreetingConfirmed(VisitorKey key) => venues.ActiveVenueOrNull is { } venue && VenueService.ContainsKey(venue.Session.Greeted, key);

    public void ConfirmTellCommandSent(VisitorKey key, string message)
    {
        // Some Dalamud/API combinations do not echo local /tell commands through ChatGui.ChatMessage.
        // CommandManager.ProcessCommand returning without throwing is the best local confirmation available.
        // We still keep the ChatMessage observer for diagnostics when the outgoing tell event is available.
        var normalized = Normalize(message);
        pendingTellLines.Remove(normalized);
        LastOutgoingTellObserved = message;
        LastGreetingConfirmation = $"Confirmed greeting command for {key.Display}.";
        visitorService?.MarkGreeted(key);
    }

    public bool MacroHasTell(GreetingMacro macro) => GetTellLines(macro).Any();

    private void OnChatMessage(IHandleableChatMessage chatMessage)
    {
        if (chatMessage.IsHandled || chatMessage.LogKind != XivChatType.TellOutgoing) return;

        var messageText = chatMessage.Message.TextValue;
        LastOutgoingTellObserved = messageText;
        var text = Normalize(messageText);
        if (!pendingTellLines.TryGetValue(text, out var key))
        {
            LastGreetingConfirmation = "Outgoing tell observed, but it did not match a pending AutoGreet tell.";
            return;
        }
        if (!IsConfiguredGreetingTell(messageText))
        {
            LastGreetingConfirmation = "Outgoing tell observed, but it did not match the configured greeting macros.";
            return;
        }

        pendingTellLines.Remove(text);
        LastGreetingConfirmation = $"Confirmed greeting for {key.Display}.";
        visitorService?.MarkGreeted(key);
    }

    private static IEnumerable<string> GetTellLines(GreetingMacro macro)
    {
        foreach (var raw in macro.Script.Split('\n'))
        {
            var line = raw.Trim();
            if (!line.StartsWith("/tell <t>", StringComparison.OrdinalIgnoreCase)) continue;
            yield return line[9..].Trim();
        }
    }

    public static string Normalize(string text) => string.Join(' ', text.Replace("<t>", string.Empty, StringComparison.OrdinalIgnoreCase).Split(' ', StringSplitOptions.RemoveEmptyEntries)).Trim();

    public void Dispose() => DalamudServices.ChatGui.ChatMessage -= OnChatMessage;
}
