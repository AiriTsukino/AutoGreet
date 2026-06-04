using System.Numerics;
using AutoGreet.Models;
using Dalamud.Bindings.ImGui;
using Dalamud.Game.ClientState.Objects.SubKinds;
using Dalamud.Plugin.Services;
using FFXIVClientStructs.FFXIV.Client.Game;

namespace AutoGreet.Services;

public sealed class DetectionService : IDisposable
{
    private readonly Configuration config;
    private readonly PersistenceService persistence;
    private readonly HashSet<string> present = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> greetingPresent = new(StringComparer.OrdinalIgnoreCase);
    private readonly TimeSpan scanInterval = TimeSpan.FromMilliseconds(500);
    private DateTimeOffset lastScan = DateTimeOffset.MinValue;
    private bool disposed;
    private bool baselineCaptured;
    private bool wasInDetectionArea;

    public event Action<VisitorKey>? PlayerEntered;
    public event Action<VisitorKey>? PlayerDoorbellEntered;
    public event Action<VisitorKey>? PlayerPresentOnArrival;
    public event Action<VisitorKey>? PlayerLeft;

    public bool IsInHousingInterior { get; private set; }
    public bool IsInCustomRegionTerritory { get; private set; }
    public bool IsScanningActive => (!config.ActiveVenueDisabled || config.MonitorWhenNoVenueSelected) && (IsInHousingInterior || IsInCustomRegionTerritory);
    public uint CurrentTerritoryType { get; private set; }
    public int CurrentPlayerObjectCount { get; private set; }
    public DateTimeOffset LastScanUtc { get; private set; } = DateTimeOffset.MinValue;
    public string LastStatus { get; private set; } = "Waiting for first scan.";
    public IReadOnlySet<string> PresentKeys => present;

    public DetectionService(Configuration config, PersistenceService persistence)
    {
        this.config = config;
        this.persistence = persistence;
        DalamudServices.Framework.Update += OnFrameworkUpdate;
        DalamudServices.PluginInterface.UiBuilder.Draw += DrawRegionOverlays;
    }

    private void OnFrameworkUpdate(IFramework framework)
    {
        if (disposed || DateTimeOffset.UtcNow - lastScan < scanInterval) return;
        lastScan = DateTimeOffset.UtcNow;
        Scan();
    }

    public void Scan(bool importCurrentPlayers = false)
    {
        CurrentTerritoryType = DalamudServices.ClientState.TerritoryType;
        LastScanUtc = DateTimeOffset.UtcNow;

        if (config.ActiveVenueDisabled && !config.MonitorWhenNoVenueSelected)
        {
            IsInHousingInterior = false;
            IsInCustomRegionTerritory = false;
            CurrentPlayerObjectCount = 0;
            LastStatus = "Not scanning: no active venue selected.";
            ClearPresenceCache();
            return;
        }

        var territoryMatched = HousingDetector.IsHousingInterior(CurrentTerritoryType, config.CustomHousingTerritories);
        var housingManagerInside = HousingDetector.IsHousingManagerInside();
        var inHousing = territoryMatched || housingManagerInside;
        var activeRegions = GetActiveRegionsForCurrentTerritory().ToList();
        var useCustomRegions = activeRegions.Count > 0;
        var venue = GetActiveVenue();

        IsInHousingInterior = inHousing;
        IsInCustomRegionTerritory = useCustomRegions;

        if (!inHousing && !useCustomRegions)
        {
            CurrentPlayerObjectCount = 0;
            LastStatus = $"Not scanning: territory {CurrentTerritoryType} is not housing and has no enabled AutoGreet custom region.";
            ClearPresenceCache();
            return;
        }

        var doorbellCurrent = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var greetingCurrent = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var seenPlayers = 0;
        foreach (var obj in DalamudServices.ObjectTable.PlayerObjects)
        {
            if (obj is not IPlayerCharacter pc) continue;
            seenPlayers++;
            if (IsLocalPlayer(pc)) continue;

            var name = pc.Name.ToString();
            if (string.IsNullOrWhiteSpace(name)) continue;
            var world = GetWorldName(pc);
            if (string.IsNullOrWhiteSpace(world) || world == "Unknown") continue;
            var key = new VisitorKey(name, world);

            if (IsInDoorbellArea(venue, pc.Position, inHousing, activeRegions))
                doorbellCurrent.Add(key.ToString());

            if (!config.ActiveVenueDisabled && IsInGreetingArea(venue, key, pc.Position, inHousing, activeRegions))
                greetingCurrent.Add(key.ToString());
        }

        CurrentPlayerObjectCount = seenPlayers;

        var justEnteredDetectionArea = !wasInDetectionArea;
        wasInDetectionArea = true;

        if (!baselineCaptured)
        {
            present.Clear();
            greetingPresent.Clear();
            foreach (var key in doorbellCurrent) present.Add(key);
            foreach (var key in greetingCurrent) greetingPresent.Add(key);
            baselineCaptured = true;

            foreach (var existing in doorbellCurrent)
                if (VisitorKey.TryParse(existing, out var key)) PlayerPresentOnArrival?.Invoke(key);

            var baselineMode = config.ActiveVenueDisabled ? "paused monitor" : "active venue";
            LastStatus = $"Baseline captured for territory {CurrentTerritoryType}. Source: {GetSourceText(inHousing, housingManagerInside, useCustomRegions, venue)}. Mode: {baselineMode}. Player actors: {seenPlayers}, doorbell present: {present.Count}, greeting-area present: {greetingPresent.Count}.";
            return;
        }

        if (importCurrentPlayers || justEnteredDetectionArea)
        {
            foreach (var existing in doorbellCurrent)
                if (VisitorKey.TryParse(existing, out var key)) PlayerPresentOnArrival?.Invoke(key);
        }

        var greetingEntered = greetingCurrent.Except(greetingPresent, StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var doorbellEntered = doorbellCurrent.Except(present, StringComparer.OrdinalIgnoreCase).ToHashSet(StringComparer.OrdinalIgnoreCase);

        foreach (var entered in doorbellEntered)
        {
            if (greetingEntered.Contains(entered)) continue;
            if (VisitorKey.TryParse(entered, out var key)) PlayerDoorbellEntered?.Invoke(key);
        }

        foreach (var entered in greetingEntered)
            if (VisitorKey.TryParse(entered, out var key)) PlayerEntered?.Invoke(key);

        foreach (var left in present.Except(doorbellCurrent, StringComparer.OrdinalIgnoreCase))
            if (VisitorKey.TryParse(left, out var key)) PlayerLeft?.Invoke(key);

        present.Clear();
        greetingPresent.Clear();
        foreach (var key in doorbellCurrent) present.Add(key);
        foreach (var key in greetingCurrent) greetingPresent.Add(key);
        var scanMode = config.ActiveVenueDisabled ? "paused monitor" : "active venue";
        LastStatus = $"Scanning territory {CurrentTerritoryType}. Source: {GetSourceText(inHousing, housingManagerInside, useCustomRegions, venue)}. Mode: {scanMode}. Player actors: {seenPlayers}, doorbell tracked: {present.Count}, greeting-area tracked: {greetingPresent.Count}.";
    }

    public void ClearPresenceCache()
    {
        present.Clear();
        greetingPresent.Clear();
        baselineCaptured = false;
        wasInDetectionArea = false;
    }

    public IReadOnlyList<CustomDetectionRegion> GetRegionsForCurrentTerritory() =>
        persistence.CustomRegions
            .Where(r => r.TerritoryType == CurrentTerritoryType)
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public IReadOnlyList<CustomDetectionRegion> GetActiveRegionsForCurrentTerritory() =>
        persistence.CustomRegions
            .Where(r => r.TerritoryType == CurrentTerritoryType && r.Enabled)
            .OrderBy(r => r.Name, StringComparer.OrdinalIgnoreCase)
            .ToList();

    public CustomDetectionRegion? CreateRegionAtLocalPlayer(string? name = null)
    {
        var local = DalamudServices.ObjectTable.LocalPlayer;
        if (local is null || CurrentTerritoryType == 0)
        {
            LastStatus = "Could not create custom region: local player or territory was unavailable.";
            return null;
        }

        var region = new CustomDetectionRegion
        {
            Name = string.IsNullOrWhiteSpace(name) ? $"Region {persistence.CustomRegions.Count(r => r.TerritoryType == CurrentTerritoryType) + 1}" : name.Trim(),
            TerritoryType = CurrentTerritoryType,
            Center = local.Position,
            Radius = 5f,
            HalfExtents = new Vector3(2.5f, 2.5f, 2.5f),
            YawDegrees = 0f,
            Shape = CustomDetectionRegionShape.Sphere,
            Enabled = true,
            ShowOverlay = true,
        };

        persistence.CustomRegions.Add(region);
        persistence.SaveNow();
        ClearPresenceCache();
        Scan(importCurrentPlayers: true);
        return region;
    }

    public void DeleteRegion(Guid id)
    {
        persistence.CustomRegions.RemoveAll(r => r.Id == id);
        persistence.SaveNow();
        ClearPresenceCache();
        Scan(importCurrentPlayers: true);
    }

    public IReadOnlyList<VisitorKey> GetCurrentVisibleVisitors()
    {
        try
        {
            return DalamudServices.Framework
                .RunOnFrameworkThread(CollectCurrentVisibleVisitorsOnFrameworkThread)
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception ex)
        {
            DalamudServices.Log.Warning(ex, "AutoGreet could not enumerate currently visible visitors.");
            return present
                .Select(x => VisitorKey.TryParse(x, out var key) ? key : default)
                .Where(k => !string.IsNullOrWhiteSpace(k.Name) && !string.IsNullOrWhiteSpace(k.World))
                .ToList();
        }
    }

    private IReadOnlyList<VisitorKey> CollectCurrentVisibleVisitorsOnFrameworkThread()
    {
        if (config.ActiveVenueDisabled && !config.MonitorWhenNoVenueSelected)
            return [];

        var territory = DalamudServices.ClientState.TerritoryType;
        var territoryMatched = HousingDetector.IsHousingInterior(territory, config.CustomHousingTerritories);
        var housingManagerInside = HousingDetector.IsHousingManagerInside();
        var inHousing = territoryMatched || housingManagerInside;
        var activeRegions = persistence.CustomRegions
            .Where(r => r.TerritoryType == territory && r.Enabled)
            .ToList();
        var useCustomRegions = activeRegions.Count > 0;
        var venue = GetActiveVenue();

        if (!inHousing && !useCustomRegions)
            return [];

        var keys = new List<VisitorKey>();
        foreach (var obj in DalamudServices.ObjectTable.PlayerObjects)
        {
            if (obj is not IPlayerCharacter pc) continue;
            if (IsLocalPlayer(pc)) continue;
            var name = pc.Name.ToString();
            if (string.IsNullOrWhiteSpace(name)) continue;
            var world = GetWorldName(pc);
            if (string.IsNullOrWhiteSpace(world) || world == "Unknown") continue;
            var key = new VisitorKey(name, world);

            if (!IsInDoorbellArea(venue, pc.Position, inHousing, activeRegions))
                continue;

            keys.Add(key);
        }

        return keys
            .GroupBy(k => k.ToString(), StringComparer.OrdinalIgnoreCase)
            .Select(g => g.First())
            .ToList();
    }

    public bool IsPlayerVisible(VisitorKey key)
    {
        try
        {
            return DalamudServices.Framework
                .RunOnFrameworkThread(() => IsPlayerVisibleOnFrameworkThread(key))
                .GetAwaiter()
                .GetResult();
        }
        catch (Exception ex)
        {
            DalamudServices.Log.Warning(ex, "AutoGreet could not check whether {Visitor} was visible.", key.Display);
            return present.Contains(key.ToString());
        }
    }

    private bool IsPlayerVisibleOnFrameworkThread(VisitorKey key)
    {
        if (config.ActiveVenueDisabled && !config.MonitorWhenNoVenueSelected)
            return false;

        var territory = DalamudServices.ClientState.TerritoryType;
        var territoryMatched = HousingDetector.IsHousingInterior(territory, config.CustomHousingTerritories);
        var housingManagerInside = HousingDetector.IsHousingManagerInside();
        var inHousing = territoryMatched || housingManagerInside;
        var activeRegions = persistence.CustomRegions
            .Where(r => r.TerritoryType == territory && r.Enabled)
            .ToList();
        var venue = GetActiveVenue();

        foreach (var obj in DalamudServices.ObjectTable.PlayerObjects)
        {
            if (obj is not IPlayerCharacter pc) continue;
            if (IsLocalPlayer(pc)) continue;

            var name = pc.Name.ToString();
            if (!name.Equals(key.Name, StringComparison.OrdinalIgnoreCase)) continue;

            var world = GetWorldName(pc);
            if (!world.Equals(key.World, StringComparison.OrdinalIgnoreCase)) continue;

            return IsInGreetingArea(venue, key, pc.Position, inHousing, activeRegions);
        }

        return false;
    }

    private VenueProfile? GetActiveVenue()
    {
        if (config.ActiveVenueDisabled)
            return null;

        return persistence.Venues.FirstOrDefault(v => v.Id == config.ActiveVenueId);
    }

    private bool IsInDoorbellArea(VenueProfile? venue, Vector3 position, bool inHousing, IReadOnlyList<CustomDetectionRegion> activeRegions)
    {
        if (venue is not null && venue.DoorbellRegionId != Guid.Empty)
        {
            var selected = persistence.CustomRegions.FirstOrDefault(r => r.Id == venue.DoorbellRegionId && r.TerritoryType == CurrentTerritoryType);
            return selected is null
                ? IsInDefaultDetectionArea(position, inHousing, activeRegions)
                : selected.Enabled && selected.Contains(position);
        }

        return IsInDefaultDetectionArea(position, inHousing, activeRegions);
    }

    private bool IsInGreetingArea(VenueProfile? venue, VisitorKey key, Vector3 position, bool inHousing, IReadOnlyList<CustomDetectionRegion> activeRegions)
    {
        if (venue is null)
            return inHousing || activeRegions.Any(r => r.Contains(position));

        if (venue.Blacklist.Contains(key.ToString()))
            return false;

        var regionId = GetGreetingRegionId(venue, key);
        if (regionId != Guid.Empty)
        {
            var selected = persistence.CustomRegions.FirstOrDefault(r => r.Id == regionId && r.TerritoryType == CurrentTerritoryType);
            return selected is null
                ? IsInDefaultDetectionArea(position, inHousing, activeRegions)
                : selected.Enabled && selected.Contains(position);
        }

        // Default behavior: no configured greeting region means the classic whole-house
        // behavior inside housing, or any enabled custom region in non-housing territories.
        return IsInDefaultDetectionArea(position, inHousing, activeRegions);
    }

    private static bool IsInDefaultDetectionArea(Vector3 position, bool inHousing, IReadOnlyList<CustomDetectionRegion> activeRegions)
    {
        if (inHousing)
            return true;

        return activeRegions.Any(r => r.Contains(position));
    }

    private static Guid GetGreetingRegionId(VenueProfile venue, VisitorKey key)
    {
        if (venue.LifetimeVisitors.TryGetValue(key.ToString(), out var visitor) && visitor.Vip)
            return venue.VipGreetingRegionId;

        return venue.LifetimeVisitors.TryGetValue(key.ToString(), out visitor) && visitor.HasBeenGreeted
            ? venue.ReturningGreetingRegionId
            : venue.FirstTimeGreetingRegionId;
    }

    private static string GetSourceText(bool inHousing, bool housingManagerInside, bool useCustomRegions, VenueProfile? venue)
    {
        var doorbell = venue is not null && venue.DoorbellRegionId != Guid.Empty ? ", doorbell region override" : string.Empty;
        var greeting = venue is not null && (venue.FirstTimeGreetingRegionId != Guid.Empty || venue.ReturningGreetingRegionId != Guid.Empty || venue.VipGreetingRegionId != Guid.Empty)
            ? ", greeting region routing"
            : string.Empty;

        if (useCustomRegions)
            return inHousing
                ? (housingManagerInside ? $"housing + custom regions (HousingManager.IsInside{doorbell}{greeting})" : $"housing + custom regions (territory allow-list{doorbell}{greeting})")
                : $"custom region{doorbell}{greeting}";

        if (inHousing) return housingManagerInside ? $"HousingManager.IsInside{doorbell}{greeting}" : $"territory allow-list{doorbell}{greeting}";
        return "none";
    }

    private void DrawRegionOverlays()
    {
        if (disposed || CurrentTerritoryType == 0) return;

        foreach (var region in persistence.CustomRegions.Where(r => r.TerritoryType == CurrentTerritoryType && r.ShowOverlay))
            DrawRegionOverlay(region);
    }

    private static void DrawRegionOverlay(CustomDetectionRegion region)
    {
        var drawList = ImGui.GetForegroundDrawList();
        var color = RegionColorToU32(region.DisplayColorHex);
        const float thickness = 2.0f;

        if (region.Shape == CustomDetectionRegionShape.Sphere)
        {
            var axisX = RotateAroundY(Vector3.UnitX, region.YawDegrees);
            var axisZ = RotateAroundY(Vector3.UnitZ, region.YawDegrees);
            DrawWorldCircle(drawList, region.Center, axisX, axisZ, region.Radius, color, thickness);
            DrawWorldCircle(drawList, region.Center, axisX, Vector3.UnitY, region.Radius, color, thickness);
            DrawWorldCircle(drawList, region.Center, axisZ, Vector3.UnitY, region.Radius, color, thickness);
            return;
        }

        DrawWireCube(drawList, region.Center, region.HalfExtents, region.YawDegrees, color, thickness);
    }

    private static uint RegionColorToU32(string? hex)
    {
        var normalized = NormalizeHexColor(hex);
        var r = Convert.ToByte(normalized.Substring(1, 2), 16);
        var g = Convert.ToByte(normalized.Substring(3, 2), 16);
        var b = Convert.ToByte(normalized.Substring(5, 2), 16);
        return 0xFF000000u | ((uint)b << 16) | ((uint)g << 8) | r;
    }

    private static string NormalizeHexColor(string? hex)
    {
        var value = string.IsNullOrWhiteSpace(hex) ? "#FF0000" : hex.Trim();
        if (!value.StartsWith("#", StringComparison.Ordinal)) value = "#" + value;
        if (value.Length == 9) value = value[..7];
        if (value.Length != 7) return "#FF0000";
        for (var i = 1; i < value.Length; i++)
        {
            var c = value[i];
            if (!Uri.IsHexDigit(c)) return "#FF0000";
        }

        return value.ToUpperInvariant();
    }

    private static Vector3 RotateAroundY(Vector3 value, float degrees)
    {
        var radians = degrees * MathF.PI / 180f;
        var cos = MathF.Cos(radians);
        var sin = MathF.Sin(radians);
        return new Vector3(
            value.X * cos - value.Z * sin,
            value.Y,
            value.X * sin + value.Z * cos);
    }

    private static void DrawWorldCircle(ImDrawListPtr drawList, Vector3 center, Vector3 axisA, Vector3 axisB, float radius, uint color, float thickness)
    {
        const int segments = 64;
        Vector2? previous = null;
        Vector2? first = null;

        for (var i = 0; i <= segments; i++)
        {
            var angle = MathF.Tau * (i % segments) / segments;
            var world = center + axisA * (MathF.Cos(angle) * radius) + axisB * (MathF.Sin(angle) * radius);
            if (!DalamudServices.GameGui.WorldToScreen(world, out var screen))
            {
                previous = null;
                continue;
            }

            if (first is null) first = screen;
            if (previous is not null) drawList.AddLine(previous.Value, screen, color, thickness);
            previous = screen;
        }

        if (previous is not null && first is not null)
            drawList.AddLine(previous.Value, first.Value, color, thickness);
    }

    private static void DrawWireCube(ImDrawListPtr drawList, Vector3 center, Vector3 halfExtents, float yawDegrees, uint color, float thickness)
    {
        var e = new Vector3(
            MathF.Max(0.5f, halfExtents.X),
            MathF.Max(0.5f, halfExtents.Y),
            MathF.Max(0.5f, halfExtents.Z));

        var localCorners = new[]
        {
            new Vector3(-e.X, -e.Y, -e.Z),
            new Vector3( e.X, -e.Y, -e.Z),
            new Vector3( e.X, -e.Y,  e.Z),
            new Vector3(-e.X, -e.Y,  e.Z),
            new Vector3(-e.X,  e.Y, -e.Z),
            new Vector3( e.X,  e.Y, -e.Z),
            new Vector3( e.X,  e.Y,  e.Z),
            new Vector3(-e.X,  e.Y,  e.Z),
        };

        var corners = localCorners
            .Select(corner => center + RotateAroundY(corner, yawDegrees))
            .ToArray();

        DrawCubeEdge(drawList, corners[0], corners[1], color, thickness);
        DrawCubeEdge(drawList, corners[1], corners[2], color, thickness);
        DrawCubeEdge(drawList, corners[2], corners[3], color, thickness);
        DrawCubeEdge(drawList, corners[3], corners[0], color, thickness);
        DrawCubeEdge(drawList, corners[4], corners[5], color, thickness);
        DrawCubeEdge(drawList, corners[5], corners[6], color, thickness);
        DrawCubeEdge(drawList, corners[6], corners[7], color, thickness);
        DrawCubeEdge(drawList, corners[7], corners[4], color, thickness);
        DrawCubeEdge(drawList, corners[0], corners[4], color, thickness);
        DrawCubeEdge(drawList, corners[1], corners[5], color, thickness);
        DrawCubeEdge(drawList, corners[2], corners[6], color, thickness);
        DrawCubeEdge(drawList, corners[3], corners[7], color, thickness);
    }

    private static void DrawCubeEdge(ImDrawListPtr drawList, Vector3 startWorld, Vector3 endWorld, uint color, float thickness)
    {
        // Do not draw cube edges as one corner-to-corner line. Dalamud's WorldToScreen can
        // fail for individual cube corners depending on camera angle and cube size, which made
        // whole cube edges disappear. Splitting the edge into small world-space segments lets
        // visible portions keep rendering even when a corner itself is off-screen or clipped.
        const int segments = 24;
        Vector2? previousScreen = null;
        var previousVisible = false;

        for (var i = 0; i <= segments; i++)
        {
            var t = i / (float)segments;
            var world = Vector3.Lerp(startWorld, endWorld, t);

            if (!DalamudServices.GameGui.WorldToScreen(world, out var screen))
            {
                previousScreen = null;
                previousVisible = false;
                continue;
            }

            if (previousVisible && previousScreen is { } previous)
                drawList.AddLine(previous, screen, color, thickness);

            previousScreen = screen;
            previousVisible = true;
        }
    }

    private static bool IsLocalPlayer(IPlayerCharacter pc)
    {
        var local = DalamudServices.ObjectTable.LocalPlayer;
        if (local is null) return false;

        var pcName = pc.Name.ToString();
        var localName = local.Name.ToString();
        if (!pcName.Equals(localName, StringComparison.OrdinalIgnoreCase)) return false;

        var pcWorld = GetWorldName(pc);
        var localWorld = GetWorldName(local);
        return pcWorld.Equals(localWorld, StringComparison.OrdinalIgnoreCase);
    }

    private static string GetWorldName(IPlayerCharacter pc)
    {
        try { return pc.HomeWorld.Value.Name.ToString(); }
        catch { return "Unknown"; }
    }

    public void Dispose()
    {
        disposed = true;
        DalamudServices.Framework.Update -= OnFrameworkUpdate;
        DalamudServices.PluginInterface.UiBuilder.Draw -= DrawRegionOverlays;
    }
}

internal static class HousingDetector
{
    private static readonly HashSet<uint> KnownHousingInteriorTerritories =
    [
        282, 283, 284, 342, 343, 344, 384, 608, 609, 610, 649, 650, 651, 652, 653, 654,
        655, 656, 657, 980, 981, 982, 983, 984, 985, 1044, 1045, 1046
    ];

    public static bool IsHousingInterior(uint territoryType, IEnumerable<uint>? customTerritories = null)
    {
        if (KnownHousingInteriorTerritories.Contains(territoryType)) return true;
        return customTerritories?.Contains(territoryType) == true;
    }

    public static unsafe bool IsHousingManagerInside()
    {
        try
        {
            var manager = HousingManager.Instance();
            return manager != null && manager->IsInside();
        }
        catch
        {
            return false;
        }
    }
}
