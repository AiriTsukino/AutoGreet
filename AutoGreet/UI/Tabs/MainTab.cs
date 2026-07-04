using AutoGreet.Models;
using AutoGreet.Services;
using AutoGreet.UI.Components;
using Dalamud.Bindings.ImGui;

namespace AutoGreet.UI.Tabs;

internal sealed class MainTab
{
    private readonly Configuration config;
    private readonly VenueService venues;
    private readonly VisitorService visitors;
    private readonly QueueService queue;
    private readonly DetectionService detection;
    private readonly PersistenceService persistence;
    private readonly Action openSettings;
    private System.Numerics.Vector2 resetSessionPopupAnchor = System.Numerics.Vector2.Zero;
    private System.Numerics.Vector2 manualScanPopupAnchor = System.Numerics.Vector2.Zero;

    public MainTab(Configuration config, VenueService venues, VisitorService visitors, QueueService queue, DetectionService detection, PersistenceService persistence, Action openSettings)
    {
        this.config = config;
        this.venues = venues;
        this.visitors = visitors;
        this.queue = queue;
        this.detection = detection;
        this.persistence = persistence;
        this.openSettings = openSettings;
    }

    public void Draw()
    {
        DrawVenueSelector();

        if (!venues.IsVenueActive)
        {
            if (config.AutoGreetEnabled)
            {
                config.AutoGreetEnabled = false;
                persistence.SaveNow();
            }

            UiHelpers.TextDisabledWrapped("No active venue is selected. Greeting lists, queueing, and auto-greetings are paused until you select a venue again.");
            var monitor = config.MonitorWhenNoVenueSelected;
            if (ImGui.Checkbox("Keep entry alerts and active Visitors tab enabled while paused", ref monitor))
            {
                config.MonitorWhenNoVenueSelected = monitor;
                detection.ClearPresenceCache();
                persistence.SaveNow();
            }
            UiHelpers.TooltipOnHover("When enabled, None acts like a monitor-only mode: AutoGreet will still scan the current house or custom region for entry alerts and the Visitors tab, but it will not populate Greets, queue anyone, or run greetings.");
            if (!config.MonitorWhenNoVenueSelected)
            {
                ImGui.Spacing();
                ImGui.TextDisabled($"Detection status: {detection.LastStatus}");
            }

            return;
        }

        var venue = venues.ActiveVenue;
        var session = venue.Session;
        var auto = config.AutoGreetEnabled;
        if (ImGui.Checkbox("Auto-greet enabled", ref auto)) { config.AutoGreetEnabled = auto; persistence.SaveNow(); if (auto) queue.EnqueueEligibleUngreeted(true); }
        UiHelpers.TooltipOnHover("Turning this on will automatically greet guests on the ungreeted list with the configured venue and macros.");
        ImGui.SameLine();
        if (ImGui.Button("Reset Session##main-reset-session"))
        {
            resetSessionPopupAnchor = UiHelpers.GetPopupPositionNearMouse(new System.Numerics.Vector2(460f, 190f));
            ImGui.OpenPopup("Reset session?##main-reset-session-popup");
        }
        ImGui.SameLine();
        if (ImGui.Button("Manual Scan##main-manual-scan"))
        {
            manualScanPopupAnchor = UiHelpers.GetPopupPositionNearMouse(new System.Numerics.Vector2(500f, 190f));
            ImGui.OpenPopup("Manual scan?##main-manual-scan-popup");
        }
        UiHelpers.TooltipOnHover("Scans everyone currently visible in the active housing interior or custom region and adds eligible visitors to Ungreeted. By default, people already present when you arrive are marked as greeted with [Here When Arrived] instead of being queued.");

        UiHelpers.SetNextPopupPositionNearMouse(manualScanPopupAnchor, new System.Numerics.Vector2(500f, 190f));
        if (ImGui.BeginPopupModal("Manual scan?##main-manual-scan-popup", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextWrapped("This will add everyone currently in the house or custom region to the ungreeted list. Use this when you intentionally want to greet people who were already present when you arrived.");
            ImGui.Separator();
            if (ImGui.Button("Manual Scan##main-confirm-manual-scan", new System.Numerics.Vector2(140, 0)))
            {
                var count = visitors.ImportCurrentVisitorsForGreeting(detection.GetCurrentVisibleVisitors());
                if (count > 0 && config.AutoGreetEnabled)
                    queue.EnqueueEligibleUngreeted(true);
                manualScanPopupAnchor = System.Numerics.Vector2.Zero;
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel##main-cancel-manual-scan", new System.Numerics.Vector2(100, 0)))
            {
                manualScanPopupAnchor = System.Numerics.Vector2.Zero;
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }

        UiHelpers.SetNextPopupPositionNearMouse(resetSessionPopupAnchor, new System.Numerics.Vector2(460f, 190f));
        if (ImGui.BeginPopupModal("Reset session?##main-reset-session-popup", ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextWrapped("Reset the current venue session? This clears the ungreeted, greeted, skipped, nightly visitor, and queue lists. Lifetime visitor history, VIPs, blacklist, venues, and macros are kept.");
            ImGui.Separator();
            if (ImGui.Button("Reset Session##main-confirm-reset", new System.Numerics.Vector2(140, 0)))
            {
                visitors.ResetSession();
                resetSessionPopupAnchor = System.Numerics.Vector2.Zero;
                ImGui.CloseCurrentPopup();
            }
            ImGui.SameLine();
            if (ImGui.Button("Cancel##main-cancel-reset", new System.Numerics.Vector2(100, 0)))
            {
                resetSessionPopupAnchor = System.Numerics.Vector2.Zero;
                ImGui.CloseCurrentPopup();
            }
            ImGui.EndPopup();
        }

        UiHelpers.Section("Greeting macro profile and active macros");
        DrawGreetingProfileSelector(venue);
        DrawMacroSelector(venue, GreetingCategory.FirstTime, "Active first-time macro");
        DrawMacroSelector(venue, GreetingCategory.Returning, "Active returning macro");
        DrawMacroSelector(venue, GreetingCategory.Vip, "Active VIP macro");
        UiHelpers.TextDisabledWrapped("Blacklisted visitors are excluded from auto-greeting.");

        ImGui.Text($"Lifetime unique: {venue.LifetimeVisitors.Count}   Session visitors: {session.NightlyVisitors.Count}   Greeted: {session.Greeted.Count}   Ungreeted: {session.Ungreeted.Count}   Queue: {venue.Queue.Count(q => q.Status == QueueEntryStatus.Waiting)}");
        ImGui.Text($"Queue status: {(queue.IsRunning ? "processing" : "idle")}");
        ImGui.SameLine();
        ImGui.TextDisabled($"Active venue: {venue.Name}");

        if (ImGui.BeginTable("main-lists-table", 2, ImGuiTableFlags.BordersInnerV | ImGuiTableFlags.Resizable | ImGuiTableFlags.SizingStretchSame))
        {
            ImGui.TableSetupColumn("Ungreeted");
            ImGui.TableSetupColumn("Greeted");
            ImGui.TableNextRow();

            ImGui.TableSetColumnIndex(0);
            DrawUngreeted(session);

            ImGui.TableSetColumnIndex(1);
            DrawGreeted(session);

            ImGui.EndTable();
        }
    }

    private void DrawVenueSelector()
    {
        UiHelpers.Section("Active venue");
        var activeVenue = venues.ActiveVenueOrNull;
        var preview = activeVenue?.Name ?? "None - AutoGreet paused";
        ImGui.SetNextItemWidth(320);
        if (!ImGui.BeginCombo("Venue##main-active-venue", preview)) return;

        if (ImGui.Selectable("None - pause AutoGreet##main-venue-none", activeVenue is null))
        {
            venues.SwitchVenue(Guid.Empty);
            config.AutoGreetEnabled = false;
            detection.ClearPresenceCache();
            persistence.SaveNow();
        }
        if (activeVenue is null)
            ImGui.SetItemDefaultFocus();

        ImGui.Separator();

        foreach (var venue in venues.Venues.ToArray())
        {
            var selected = activeVenue is not null && venue.Id == activeVenue.Id;
            if (ImGui.Selectable($"{venue.Name}##main-venue-{venue.Id}", selected))
            {
                venues.SwitchVenue(venue.Id);
                EnsureActiveMacroDefaults(venues.ActiveVenue);
                persistence.SaveNow();
                if (config.AutoGreetEnabled)
                    queue.EnqueueEligibleUngreeted(true);
            }

            if (selected)
                ImGui.SetItemDefaultFocus();
        }

        ImGui.EndCombo();
    }

    private void DrawGreetingProfileSelector(VenueProfile venue)
    {
        var activeProfile = venues.GetGreetingProfileForVenue(venue);
        ImGui.SetNextItemWidth(320);
        if (!ImGui.BeginCombo("Greeting profile##main-active-greeting-profile", activeProfile.Name)) return;

        foreach (var item in venues.AllGreetingProfiles.ToArray())
        {
            var selected = item.Profile.Id == venue.ActiveGreetingProfileId;
            var label = item.Venue.Id == venue.Id
                ? item.Profile.Name
                : $"{item.Profile.Name}  ({item.Venue.Name})";

            if (ImGui.Selectable($"{label}##main-greeting-profile-{item.Profile.Id}", selected))
            {
                venue.ActiveGreetingProfileId = item.Profile.Id;
                EnsureActiveMacroDefaults(venue);
                persistence.SaveNow();
                if (config.AutoGreetEnabled)
                    queue.EnqueueEligibleUngreeted(true);
            }

            if (selected)
                ImGui.SetItemDefaultFocus();
        }

        ImGui.EndCombo();
    }

    private void DrawMacroSelector(VenueProfile venue, GreetingCategory category, string label)
    {
        var profile = venues.GetGreetingProfileForVenue(venue);
        var macros = profile.Macros
            .Where(m => m.Enabled && m.Category == category)
            .ToList();

        var activeId = venue.GetActiveMacroId(category);
        if (activeId != Guid.Empty && macros.All(m => m.Id != activeId))
        {
            activeId = macros.FirstOrDefault()?.Id ?? Guid.Empty;
            venue.SetActiveMacroId(category, activeId);
            persistence.SaveNow();
        }

        var preview = activeId == Guid.Empty
            ? "None configured"
            : macros.FirstOrDefault(m => m.Id == activeId)?.Name ?? "None configured";

        ImGui.SetNextItemWidth(280);
        if (ImGui.BeginCombo(label, preview))
        {
            if (ImGui.Selectable("None", activeId == Guid.Empty))
            {
                venue.SetActiveMacroId(category, Guid.Empty);
                persistence.SaveNow();
            }

            foreach (var macro in macros)
            {
                var selected = macro.Id == activeId;
                if (ImGui.Selectable($"{macro.Name}##{category}-{macro.Id}", selected))
                {
                    venue.SetActiveMacroId(category, macro.Id);
                    persistence.SaveNow();
                }
                if (selected) ImGui.SetItemDefaultFocus();
            }

            ImGui.EndCombo();
        }
    }

    private void EnsureActiveMacroDefaults(VenueProfile venue)
    {
        foreach (var category in new[] { GreetingCategory.FirstTime, GreetingCategory.Returning, GreetingCategory.Vip })
        {
            var activeId = venue.GetActiveMacroId(category);
            var macros = venues.GetGreetingProfileForVenue(venue).Macros.Where(m => m.Enabled && m.Category == category).ToList();
            if (activeId == Guid.Empty || macros.All(m => m.Id != activeId))
                venue.SetActiveMacroId(category, macros.FirstOrDefault()?.Id ?? Guid.Empty);
        }
    }

    private void DrawUngreeted(SessionData session)
    {
        UiHelpers.Section($"Ungreeted ({session.Ungreeted.Count})");
        if (ImGui.BeginChild("ungreeted", new System.Numerics.Vector2(0, ImGui.GetContentRegionAvail().Y - 6), true))
        {
            var i = 0;
            foreach (var key in session.Ungreeted.ToArray())
            {
                DrawVisitorActions(key, greetedList: false, i++);
                ImGui.Separator();
            }
        }
        ImGui.EndChild();
    }

    private void DrawGreeted(SessionData session)
    {
        UiHelpers.Section($"Greeted ({session.Greeted.Count})");
        if (ImGui.BeginChild("greeted", new System.Numerics.Vector2(0, ImGui.GetContentRegionAvail().Y - 6), true))
        {
            var i = 0;
            foreach (var key in session.Greeted.ToArray())
            {
                DrawVisitorActions(key, greetedList: true, i++);
                ImGui.Separator();
            }
        }
        ImGui.EndChild();
    }

    private void DrawVisitorActions(VisitorKey key, bool greetedList, int index)
    {
        ImGui.PushID($"main-{(greetedList ? "g" : "u")}-{index}-{key}");
        var state = venues.ActiveVenue.Session.NightlyVisitors.FirstOrDefault(v => string.Equals(v.Key.ToString(), key.ToString(), StringComparison.OrdinalIgnoreCase));
        UiHelpers.VisitorRow(key, state?.Present == true, state?.ReturningThisSession == true, state?.HereWhenArrived == true);
        ImGui.TextDisabled(state is null ? "No session timestamp" : $"Last seen: {state.LastSeenUtc.LocalDateTime:g}");
        if (!greetedList)
        {
            if (ImGui.SmallButton("Greet Now")) queue.Enqueue(key, forceStart: true);
            ImGui.SameLine();
            if (ImGui.SmallButton("Skip")) visitors.Skip(key);
            ImGui.SameLine();
            if (ImGui.SmallButton("Mark Greeted")) visitors.MarkGreeted(key);
        }
        else
        {
            if (ImGui.SmallButton("Move to Ungreeted")) visitors.MoveToUngreeted(key);
        }
        ImGui.SameLine();
        if (ImGui.SmallButton("Blacklist")) visitors.ToggleBlacklist(key);
        ImGui.PopID();
    }
}
