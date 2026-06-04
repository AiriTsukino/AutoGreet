using AutoGreet.Models;
using AutoGreet.Services;
using AutoGreet.UI.Components;
using Dalamud.Bindings.ImGui;

namespace AutoGreet.UI.Tabs;

internal sealed class VenuesTab
{
    private readonly VenueService venues;
    private readonly PersistenceService persistence;
    private string newVenueName = "New Venue";

    public VenuesTab(VenueService venues, PersistenceService persistence)
    {
        this.venues = venues;
        this.persistence = persistence;
    }

    public void Draw()
    {
        UiHelpers.Section("Venue profiles");
        ImGui.InputText("New venue name", ref newVenueName, 80);
        ImGui.SameLine();
        if (ImGui.Button("Create venue")) venues.CreateVenue(newVenueName);

        foreach (var venue in venues.Venues.ToArray())
        {
            ImGui.PushID(venue.Id.ToString());
            var name = venue.Name;
            if (ImGui.InputText("Name", ref name, 80)) venues.RenameVenue(venue.Id, name);
            ImGui.SameLine();
            if (ImGui.RadioButton("Active", venues.ActiveVenue.Id == venue.Id)) venues.SwitchVenue(venue.Id);
            ImGui.SameLine();
            if (venues.Venues.Count > 1 && ImGui.Button("Delete")) venues.DeleteVenue(venue.Id);
            ImGui.TextDisabled($"Lifetime visitors: {venue.LifetimeVisitors.Count} | Session visitors: {venue.Session.NightlyVisitors.Count} | Blacklisted: {venue.Blacklist.Count}");
            DrawRegionRouting(venue);
            ImGui.Separator();
            ImGui.PopID();
        }
    }
    private void DrawRegionRouting(VenueProfile venue)
    {
        if (ImGui.TreeNodeEx($"Detection regions##venue-regions-{venue.Id}", ImGuiTreeNodeFlags.DefaultOpen))
        {
            UiHelpers.TextDisabledWrapped("Optional: choose where this venue should ring the doorbell and where each visitor type becomes eligible for greeting. Leave these set to Default to keep classic behavior: the whole house indoors, or any enabled custom region outdoors.");

            DrawRegionCombo("Doorbell region", venue.DoorbellRegionId, id => venue.DoorbellRegionId = id,
                "Controls chat/sound entry notifications. Default is the whole house in housing interiors, or any enabled custom region outdoors.");
            DrawRegionCombo("First-time greeting region", venue.FirstTimeGreetingRegionId, id => venue.FirstTimeGreetingRegionId = id,
                "First-time visitors are only added to Ungreeted when they enter this region. Default is the classic whole-house behavior.");
            DrawRegionCombo("Returning greeting region", venue.ReturningGreetingRegionId, id => venue.ReturningGreetingRegionId = id,
                "Returning visitors are only added to Ungreeted when they enter this region. Default is the classic whole-house behavior.");
            DrawRegionCombo("VIP greeting region", venue.VipGreetingRegionId, id => venue.VipGreetingRegionId = id,
                "VIP visitors are only added to Ungreeted when they enter this region. Default is the classic whole-house behavior.");

            ImGui.TreePop();
        }
    }

    private void DrawRegionCombo(string label, Guid selectedId, Action<Guid> setSelected, string tooltip)
    {
        var regions = persistence.CustomRegions
            .OrderBy(r => r.TerritoryType)
            .ThenBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToArray();

        var selectedName = selectedId == Guid.Empty
            ? "Default (whole house / outdoor regions)"
            : regions.FirstOrDefault(r => r.Id == selectedId) is { } selected
                ? $"{selected.Name}  (Territory {selected.TerritoryType})"
                : "Missing region - using default";

        ImGui.SetNextItemWidth(360);
        if (ImGui.BeginCombo($"{label}##{label}", selectedName))
        {
            if (ImGui.Selectable("Default (whole house / outdoor regions)##default", selectedId == Guid.Empty))
            {
                setSelected(Guid.Empty);
                persistence.SaveNow();
            }

            if (regions.Length > 0)
                ImGui.Separator();

            foreach (var region in regions)
            {
                var regionSelected = selectedId == region.Id;
                var regionLabel = $"{region.Name}  (Territory {region.TerritoryType})##region-{region.Id}";
                if (ImGui.Selectable(regionLabel, regionSelected))
                {
                    setSelected(region.Id);
                    persistence.SaveNow();
                }

                if (regionSelected)
                    ImGui.SetItemDefaultFocus();
            }

            ImGui.EndCombo();
        }
        UiHelpers.TooltipOnHover(tooltip);
    }

}
