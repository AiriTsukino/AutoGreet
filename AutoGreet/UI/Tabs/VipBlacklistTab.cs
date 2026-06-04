using AutoGreet.Models;
using AutoGreet.Services;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.SubKinds;

namespace AutoGreet.UI.Tabs;

public sealed class VipBlacklistTab
{
    private readonly VenueService venues;
    private readonly VisitorService visitors;
    private readonly PersistenceService persistence;
    private Guid editingVenueId = Guid.Empty;
    private string manualName = string.Empty;
    private string manualWorld = string.Empty;
    private string status = "Select a player target or type a character name and world.";

    public VipBlacklistTab(VenueService venues, VisitorService visitors, PersistenceService persistence)
    {
        this.venues = venues;
        this.visitors = visitors;
        this.persistence = persistence;
    }

    public void Draw()
    {
        var venue = GetEditingVenue();
        ImGui.TextColored(new System.Numerics.Vector4(0.65f, 0.85f, 1f, 1f), "VIP and Blacklist Management");
        ImGui.TextWrapped("VIP visitors use the VIP greeting category. Blacklisted visitors are excluded from tracking, queueing, and greetings.");
        ImGui.Spacing();
        DrawVenueSelector(venue);
        ImGui.Separator();

        DrawTargetControls(venue);
        ImGui.Spacing();
        DrawManualControls(venue);
        ImGui.Spacing();
        ImGui.TextDisabled(status);
        ImGui.Separator();

        if (ImGui.BeginTable("VipBlacklistTables", 2, ImGuiTableFlags.Resizable | ImGuiTableFlags.BordersInnerV))
        {
            ImGui.TableSetupColumn("VIPs");
            ImGui.TableSetupColumn("Blacklist");
            ImGui.TableHeadersRow();

            ImGui.TableNextRow();
            ImGui.TableSetColumnIndex(0);
            DrawVipList(venue);
            ImGui.TableSetColumnIndex(1);
            DrawBlacklist(venue);
            ImGui.EndTable();
        }
    }

    private VenueProfile GetEditingVenue()
    {
        if (editingVenueId == Guid.Empty || venues.Venues.All(v => v.Id != editingVenueId))
            editingVenueId = venues.ActiveVenue.Id;

        return venues.Venues.FirstOrDefault(v => v.Id == editingVenueId) ?? venues.ActiveVenue;
    }

    private void DrawVenueSelector(VenueProfile current)
    {
        ImGui.SetNextItemWidth(320);
        if (!ImGui.BeginCombo("Editing venue##vip-blacklist-editing-venue", current.Name))
            return;

        foreach (var venue in venues.Venues)
        {
            var selected = venue.Id == current.Id;
            if (ImGui.Selectable($"{venue.Name}##vip-blacklist-venue-{venue.Id}", selected))
            {
                editingVenueId = venue.Id;
                status = $"Editing VIP and blacklist lists for {venue.Name}.";
            }

            if (selected)
                ImGui.SetItemDefaultFocus();
        }

        ImGui.EndCombo();
    }

    private void DrawTargetControls(VenueProfile venue)
    {
        ImGui.Text("Current target");
        var key = GetTargetKey();
        if (key is null)
        {
            ImGui.TextDisabled("No player target selected.");
            ImGui.BeginDisabled();
            ImGui.Button("Set Target as VIP"); ImGui.SameLine();
            ImGui.Button("Unset Target VIP"); ImGui.SameLine();
            ImGui.Button("Blacklist Target"); ImGui.SameLine();
            ImGui.Button("Unblacklist Target");
            ImGui.EndDisabled();
            return;
        }

        ImGui.TextColored(new System.Numerics.Vector4(0.6f, 1f, 0.6f, 1f), key.Value.Display);
        if (ImGui.Button("Set Target as VIP")) SetVip(venue, key.Value, true);
        ImGui.SameLine();
        if (ImGui.Button("Unset Target VIP")) SetVip(venue, key.Value, false);
        ImGui.SameLine();
        if (ImGui.Button("Blacklist Target")) SetBlacklist(venue, key.Value, true);
        ImGui.SameLine();
        if (ImGui.Button("Unblacklist Target")) SetBlacklist(venue, key.Value, false);
    }

    private void DrawManualControls(VenueProfile venue)
    {
        ImGui.Text("Manual entry");
        ImGui.SetNextItemWidth(240);
        ImGui.InputTextWithHint("##ManualCharacterName", "Character Name, e.g. Jane Doe", ref manualName, 64);
        ImGui.SameLine();
        ImGui.SetNextItemWidth(160);
        ImGui.InputTextWithHint("##ManualWorld", "World, e.g. Kraken", ref manualWorld, 32);

        if (ImGui.Button("Set Manual as VIP")) TryApplyManual(venue, k => SetVip(venue, k, true));
        ImGui.SameLine();
        if (ImGui.Button("Unset Manual VIP")) TryApplyManual(venue, k => SetVip(venue, k, false));
        ImGui.SameLine();
        if (ImGui.Button("Blacklist Manual")) TryApplyManual(venue, k => SetBlacklist(venue, k, true));
        ImGui.SameLine();
        if (ImGui.Button("Unblacklist Manual")) TryApplyManual(venue, k => SetBlacklist(venue, k, false));
    }

    private void DrawVipList(VenueProfile venue)
    {
        var vips = venue.LifetimeVisitors.Values
            .Where(v => v.Vip)
            .OrderBy(v => v.World, StringComparer.OrdinalIgnoreCase)
            .ThenBy(v => v.CharacterName, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (vips.Count == 0)
        {
            ImGui.TextDisabled($"No VIPs set for {venue.Name}.");
            return;
        }

        if (ImGui.BeginChild($"VipListChild-{venue.Id}", new System.Numerics.Vector2(0, 300), true))
        {
            foreach (var visitor in vips)
            {
                var key = visitor.Key;
                ImGui.PushID($"vip-{venue.Id}-{key}");
                ImGui.TextColored(new System.Numerics.Vector4(0.85f, 1f, 0.55f, 1f), key.Display);
                ImGui.SameLine();
                if (ImGui.SmallButton("Unset VIP")) SetVip(venue, key, false);
                ImGui.SameLine();
                if (ImGui.SmallButton("Blacklist")) SetBlacklist(venue, key, true);
                ImGui.PopID();
            }
            ImGui.EndChild();
        }
    }

    private void DrawBlacklist(VenueProfile venue)
    {
        var blacklisted = venue.Blacklist
            .Select(x => VisitorKey.TryParse(x, out var key) ? key : (VisitorKey?)null)
            .Where(x => x is not null)
            .Select(x => x!.Value)
            .OrderBy(x => x.World, StringComparer.OrdinalIgnoreCase)
            .ThenBy(x => x.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

        if (blacklisted.Count == 0)
        {
            ImGui.TextDisabled($"No blacklisted visitors set for {venue.Name}.");
            return;
        }

        if (ImGui.BeginChild($"BlacklistChild-{venue.Id}", new System.Numerics.Vector2(0, 300), true))
        {
            foreach (var key in blacklisted)
            {
                ImGui.PushID($"blacklist-{venue.Id}-{key}");
                ImGui.TextColored(new System.Numerics.Vector4(1f, 0.55f, 0.55f, 1f), key.Display);
                ImGui.SameLine();
                if (ImGui.SmallButton("Unblacklist")) SetBlacklist(venue, key, false);
                ImGui.SameLine();
                if (ImGui.SmallButton("Set VIP")) SetVip(venue, key, true);
                ImGui.PopID();
            }
            ImGui.EndChild();
        }
    }

    private VisitorKey? GetTargetKey()
    {
        if (DalamudServices.TargetManager.Target is not IPlayerCharacter pc) return null;
        var name = pc.Name.ToString();
        string world;
        try { world = pc.HomeWorld.Value.Name.ToString(); }
        catch { world = string.Empty; }

        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(world)) return null;
        return new VisitorKey(name, world);
    }

    private void TryApplyManual(VenueProfile venue, Action<VisitorKey> action)
    {
        if (!TryGetManualKey(out var key))
        {
            status = "Enter both Character Name and World, or use Name@World in the name box.";
            return;
        }

        action(key);
    }

    private bool TryGetManualKey(out VisitorKey key)
    {
        key = default;
        var name = manualName.Trim();
        var world = manualWorld.Trim();

        if (VisitorKey.TryParse(name, out key)) return true;
        if (string.IsNullOrWhiteSpace(name) || string.IsNullOrWhiteSpace(world)) return false;

        key = new VisitorKey(name, world);
        return true;
    }

    private void SetVip(VenueProfile venue, VisitorKey key, bool isVip)
    {
        if (!venue.LifetimeVisitors.TryGetValue(key.ToString(), out var visitor))
            visitor = Visitor.FromKey(key);

        visitor.Vip = isVip;
        visitor.LastSeenUtc = DateTimeOffset.UtcNow;
        venue.LifetimeVisitors[key.ToString()] = visitor;
        venues.RepairVenueData(venue);
        persistence.SaveNow();
        status = isVip ? $"Set {key.Display} as VIP for {venue.Name}." : $"Removed VIP status from {key.Display} for {venue.Name}.";
    }

    private void SetBlacklist(VenueProfile venue, VisitorKey key, bool blacklisted)
    {
        if (blacklisted)
        {
            venue.Blacklist.Add(key.ToString());
            VenueService.RemoveKey(venue.Session.Ungreeted, key);
            VenueService.RemoveKey(venue.Session.Greeted, key);
            VenueService.RemoveKey(venue.Session.Skipped, key);
            venue.Queue.RemoveAll(q => string.Equals(q.Visitor.ToString(), key.ToString(), StringComparison.OrdinalIgnoreCase));
        }
        else
        {
            venue.Blacklist.RemoveWhere(x => string.Equals(x, key.ToString(), StringComparison.OrdinalIgnoreCase));
        }

        if (!venue.LifetimeVisitors.ContainsKey(key.ToString()))
            venue.LifetimeVisitors[key.ToString()] = Visitor.FromKey(key);

        venues.RepairVenueData(venue);
        persistence.SaveNow();
        status = blacklisted ? $"Blacklisted {key.Display} for {venue.Name}." : $"Removed {key.Display} from blacklist for {venue.Name}.";
    }
}
