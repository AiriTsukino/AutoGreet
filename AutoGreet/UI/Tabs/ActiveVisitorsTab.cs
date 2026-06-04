using AutoGreet.Models;
using AutoGreet.Services;
using AutoGreet.UI.Components;
using Dalamud.Bindings.ImGui;

namespace AutoGreet.UI.Tabs;

internal sealed class ActiveVisitorsTab
{
    private readonly Configuration config;
    private readonly VenueService venues;
    private readonly VisitorService visitors;
    private readonly QueueService queue;
    private readonly DetectionService detection;

    public ActiveVisitorsTab(Configuration config, VenueService venues, VisitorService visitors, QueueService queue, DetectionService detection)
    {
        this.config = config;
        this.venues = venues;
        this.visitors = visitors;
        this.queue = queue;
        this.detection = detection;
    }

    public void Draw()
    {
        if (!venues.IsVenueActive)
        {
            DrawPausedMonitorVisitors();
            return;
        }

        var venue = venues.ActiveVenue;
        var session = venue.Session;
        var activeVisitors = session.NightlyVisitors
            .Where(v => v.Present)
            .OrderBy(v => v.Key.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(v => v.Key.World, StringComparer.OrdinalIgnoreCase)
            .ToList();

        UiHelpers.Section($"Active visitors ({activeVisitors.Count})");
        UiHelpers.TextDisabledWrapped("Shows people currently detected in the active venue area. This follows the venue's doorbell detection area, so a whole-house doorbell region acts as a whole-house active player count.");

        if (activeVisitors.Count == 0)
        {
            ImGui.Spacing();
            UiHelpers.TextDisabledWrapped("No visitors are currently detected in the active venue area.");
            return;
        }

        if (ImGui.BeginTable("active-visitors-table", 4, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Visitor", ImGuiTableColumnFlags.WidthStretch, 2.2f);
            ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthStretch, 1.5f);
            ImGui.TableSetupColumn("Last seen", ImGuiTableColumnFlags.WidthFixed, 132f);
            ImGui.TableSetupColumn("Actions", ImGuiTableColumnFlags.WidthFixed, 112f);
            ImGui.TableHeadersRow();

            for (var i = 0; i < activeVisitors.Count; i++)
            {
                var state = activeVisitors[i];
                var key = state.Key;
                var lifetimeVisitor = venue.LifetimeVisitors.TryGetValue(key.ToString(), out var visitor) ? visitor : null;
                var isVip = lifetimeVisitor?.Vip == true;
                var isBlacklisted = venue.Blacklist.Contains(key.ToString());
                var isUngreeted = VenueService.ContainsKey(session.Ungreeted, key);
                var isGreeted = VenueService.ContainsKey(session.Greeted, key);
                var isSkipped = VenueService.ContainsKey(session.Skipped, key);

                ImGui.PushID($"active-visitor-{i}-{key}");
                ImGui.TableNextRow();

                ImGui.TableSetColumnIndex(0);
                UiHelpers.VisitorRow(key, present: true, state.ReturningThisSession, state.HereWhenArrived);

                ImGui.TableSetColumnIndex(1);
                DrawStatus(isVip, isBlacklisted, isUngreeted, isGreeted, isSkipped, state.HereWhenArrived);

                ImGui.TableSetColumnIndex(2);
                ImGui.TextDisabled(state.LastSeenUtc.LocalDateTime.ToString("g"));

                ImGui.TableSetColumnIndex(3);
                DrawActionButtons(key, isVip, isBlacklisted);

                ImGui.PopID();
            }

            ImGui.EndTable();
        }
    }

    private void DrawPausedMonitorVisitors()
    {
        if (!config.MonitorWhenNoVenueSelected)
        {
            UiHelpers.TextDisabledWrapped("No active venue is selected. Visitor tracking is paused. Enable monitor-only mode on the Main tab to keep the doorbell and active Visitors tab running while no venue is selected.");
            return;
        }

        var activeVisitors = detection.PresentKeys
            .Select(x => VisitorKey.TryParse(x, out var key) ? key : default)
            .Where(k => !string.IsNullOrWhiteSpace(k.Name) && !string.IsNullOrWhiteSpace(k.World))
            .OrderBy(k => k.Name, StringComparer.OrdinalIgnoreCase)
            .ThenBy(k => k.World, StringComparer.OrdinalIgnoreCase)
            .ToList();

        UiHelpers.Section($"Active visitors ({activeVisitors.Count})");
        UiHelpers.TextDisabledWrapped("Monitor-only mode is enabled. This tab shows people currently detected in the house or enabled custom region, and the doorbell can still fire. Greets, queueing, and auto-greetings remain paused because no venue is selected.");

        if (activeVisitors.Count == 0)
        {
            ImGui.Spacing();
            UiHelpers.TextDisabledWrapped("No visitors are currently detected in the monitored area.");
            return;
        }

        if (ImGui.BeginTable("paused-active-visitors-table", 2, ImGuiTableFlags.Borders | ImGuiTableFlags.RowBg | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchProp))
        {
            ImGui.TableSetupColumn("Visitor", ImGuiTableColumnFlags.WidthStretch, 2.2f);
            ImGui.TableSetupColumn("Status", ImGuiTableColumnFlags.WidthStretch, 1.0f);
            ImGui.TableHeadersRow();

            for (var i = 0; i < activeVisitors.Count; i++)
            {
                var key = activeVisitors[i];
                ImGui.PushID($"paused-active-visitor-{i}-{key}");
                ImGui.TableNextRow();

                ImGui.TableSetColumnIndex(0);
                UiHelpers.VisitorRow(key, present: true, returning: false, hereWhenArrived: false);

                ImGui.TableSetColumnIndex(1);
                ImGui.TextUnformatted("Present");

                ImGui.PopID();
            }

            ImGui.EndTable();
        }
    }

    private void DrawActionButtons(VisitorKey key, bool isVip, bool isBlacklisted)
    {
        var width = ImGui.GetContentRegionAvail().X;

        if (!isBlacklisted)
            if (ImGui.Button("Greet##active-greet", new System.Numerics.Vector2(width, 0))) queue.Enqueue(key, forceStart: true);

        if (ImGui.Button(isVip ? "Un-VIP##active-vip" : "VIP##active-vip", new System.Numerics.Vector2(width, 0))) visitors.SetVip(key, !isVip);
        if (ImGui.Button("Blacklist##active-blacklist", new System.Numerics.Vector2(width, 0))) visitors.ToggleBlacklist(key);
    }

    private static void DrawStatus(bool isVip, bool isBlacklisted, bool isUngreeted, bool isGreeted, bool isSkipped, bool hereWhenArrived)
    {
        var parts = new List<string>();
        if (isBlacklisted) parts.Add("Blacklisted");
        if (isVip) parts.Add("VIP");
        if (isUngreeted) parts.Add("Ungreeted");
        else if (isSkipped) parts.Add("Skipped");
        else if (isGreeted) parts.Add(hereWhenArrived ? "Here on arrival" : "Greeted");
        else parts.Add("Present");

        ImGui.TextUnformatted(string.Join(" • ", parts));
    }
}
