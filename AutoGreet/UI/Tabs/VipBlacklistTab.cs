using AutoGreet.Models;
using AutoGreet.Services;
using AutoGreet.UI.Components;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.SubKinds;

namespace AutoGreet.UI.Tabs;

public sealed class VipBlacklistTab
{
    private readonly VenueService venues;
    private readonly VisitorService visitors;
    private readonly PersistenceService persistence;
    private Guid editingVenueId = Guid.Empty;
    private Guid assignmentTierId = Guid.Empty;
    private string newTierName = "New VIP tier";
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
        ImGui.TextWrapped("VIP tiers choose which active VIP macro a visitor receives. Blacklisted visitors are excluded from tracking, queueing, and greetings.");
        ImGui.Spacing();
        DrawVenueSelector(venue);
        ImGui.Separator();

        DrawTierManagement(venue);
        ImGui.Separator();
        DrawAssignmentTierSelector(venue);
        ImGui.Spacing();
        DrawTargetControls(venue);
        ImGui.Spacing();
        DrawManualControls(venue);
        ImGui.Spacing();
        UiHelpers.TextDisabledWrapped(status);
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
                assignmentTierId = venue.GetDefaultVipTier().Id;
                status = $"Editing VIP and blacklist lists for {venue.Name}.";
            }

            if (selected)
                ImGui.SetItemDefaultFocus();
        }

        ImGui.EndCombo();
    }

    private void DrawTierManagement(VenueProfile venue)
    {
        if (!ImGui.CollapsingHeader("VIP tiers##vip-tier-management", ImGuiTreeNodeFlags.DefaultOpen))
            return;

        foreach (var tier in venue.VipTiers.ToArray())
        {
            ImGui.PushID($"vip-tier-{venue.Id}-{tier.Id}");
            var name = tier.Name;
            ImGui.SetNextItemWidth(240);
            if (ImGui.InputText("##tier-name", ref name, 64))
                tier.Name = name;
            if (ImGui.IsItemDeactivatedAfterEdit())
            {
                tier.Name = MakeUniqueTierName(venue, tier.Name, tier.Id);
                venues.RepairVenueData(venue);
                persistence.SaveNow();
            }

            ImGui.SameLine();
            if (tier.Id == venue.DefaultVipTierId)
            {
                ImGui.TextDisabled("Default tier");
            }
            else
            {
                if (ImGui.SmallButton("Make default"))
                {
                    venue.DefaultVipTierId = tier.Id;
                    venues.RepairVenueData(venue);
                    persistence.SaveNow();
                }
                ImGui.SameLine();
                if (ImGui.SmallButton("Delete"))
                    DeleteTier(venue, tier.Id);
            }

            ImGui.PopID();
        }

        ImGui.SetNextItemWidth(240);
        ImGui.InputText("New tier name##new-vip-tier-name", ref newTierName, 64);
        ImGui.SameLine();
        if (ImGui.Button("Add tier##add-vip-tier"))
        {
            var tier = new VipTierDefinition
            {
                Name = MakeUniqueTierName(venue, newTierName),
            };
            venue.VipTiers.Add(tier);
            assignmentTierId = tier.Id;
            newTierName = "New VIP tier";
            venues.RepairVenueData(venue);
            persistence.SaveNow();
            status = $"Added VIP tier {tier.Name} to {venue.Name}.";
        }
    }

    private void DrawAssignmentTierSelector(VenueProfile venue)
    {
        var tierId = GetAssignmentTierId(venue);
        var selectedTier = venue.GetVipTier(tierId) ?? venue.GetDefaultVipTier();
        ImGui.SetNextItemWidth(260);
        if (!ImGui.BeginCombo("VIP tier to assign", selectedTier.Name))
            return;

        foreach (var tier in venue.VipTiers)
        {
            var selected = tier.Id == selectedTier.Id;
            if (ImGui.Selectable($"{tier.Name}##assign-vip-tier-{tier.Id}", selected))
                assignmentTierId = tier.Id;
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
            ImGui.Button("Set Target VIP"); ImGui.SameLine();
            ImGui.Button("Unset Target VIP"); ImGui.SameLine();
            ImGui.Button("Blacklist Target"); ImGui.SameLine();
            ImGui.Button("Unblacklist Target");
            ImGui.EndDisabled();
            return;
        }

        ImGui.TextColored(new System.Numerics.Vector4(0.6f, 1f, 0.6f, 1f), key.Value.Display);
        if (ImGui.Button("Set Target VIP")) SetVipTier(venue, key.Value, GetAssignmentTierId(venue));
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

        if (ImGui.Button("Set Manual VIP")) TryApplyManual(venue, k => SetVipTier(venue, k, GetAssignmentTierId(venue)));
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
            .Where(v => v.Vip || v.VipTierId != Guid.Empty)
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
                DrawVisitorTierCombo(venue, visitor);
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
                if (ImGui.SmallButton("Set VIP")) SetVipTier(venue, key, GetAssignmentTierId(venue));
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

    private Guid GetAssignmentTierId(VenueProfile venue)
    {
        if (venue.GetVipTier(assignmentTierId) is null)
            assignmentTierId = venue.GetDefaultVipTier().Id;
        return assignmentTierId;
    }

    private void DrawVisitorTierCombo(VenueProfile venue, Visitor visitor)
    {
        var current = venue.GetVipTier(visitor.VipTierId) ?? venue.GetDefaultVipTier();
        ImGui.SetNextItemWidth(150);
        if (!ImGui.BeginCombo("##visitor-vip-tier", current.Name))
            return;

        foreach (var tier in venue.VipTiers)
        {
            var selected = tier.Id == current.Id;
            if (ImGui.Selectable($"{tier.Name}##visitor-tier-{tier.Id}", selected))
                SetVipTier(venue, visitor.Key, tier.Id);
            if (selected)
                ImGui.SetItemDefaultFocus();
        }

        ImGui.EndCombo();
    }

    private void DeleteTier(VenueProfile venue, Guid tierId)
    {
        if (tierId == Guid.Empty || tierId == venue.DefaultVipTierId)
            return;

        var tier = venue.GetVipTier(tierId);
        if (tier is null)
            return;

        var defaultTier = venue.GetDefaultVipTier();
        venue.VipTiers.RemoveAll(t => t.Id == tierId);
        venue.ActiveVipMacroIdsByTier.Remove(tierId);
        foreach (var visitor in venue.LifetimeVisitors.Values.Where(v => v.VipTierId == tierId))
        {
            visitor.Vip = true;
            visitor.VipTierId = defaultTier.Id;
        }

        if (assignmentTierId == tierId)
            assignmentTierId = defaultTier.Id;

        venues.RepairVenueData(venue);
        persistence.SaveNow();
        status = $"Deleted VIP tier {tier.Name}. Its visitors now use {defaultTier.Name}.";
    }

    private static string MakeUniqueTierName(VenueProfile venue, string? requestedName, Guid? currentTierId = null)
    {
        var baseName = string.IsNullOrWhiteSpace(requestedName) ? "VIP" : requestedName.Trim();
        var name = baseName;
        var suffix = 2;
        while (venue.VipTiers.Any(t => t.Id != currentTierId && string.Equals(t.Name, name, StringComparison.OrdinalIgnoreCase)))
            name = $"{baseName} ({suffix++})";
        return name;
    }

    private void SetVip(VenueProfile venue, VisitorKey key, bool isVip)
        => SetVipTier(venue, key, isVip ? GetAssignmentTierId(venue) : Guid.Empty);

    private void SetVipTier(VenueProfile venue, VisitorKey key, Guid tierId)
    {
        if (!venue.LifetimeVisitors.TryGetValue(key.ToString(), out var visitor))
            visitor = Visitor.FromKey(key);

        var tier = tierId == Guid.Empty ? null : venue.GetVipTier(tierId);
        visitor.Vip = tier is not null;
        visitor.VipTierId = tier?.Id ?? Guid.Empty;
        visitor.LastSeenUtc = DateTimeOffset.UtcNow;
        venue.LifetimeVisitors[key.ToString()] = visitor;
        venues.RepairVenueData(venue);
        persistence.SaveNow();
        status = tier is not null
            ? $"Set {key.Display} to VIP tier {tier.Name} for {venue.Name}."
            : $"Removed VIP status from {key.Display} for {venue.Name}.";
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
