using System.Diagnostics;
using System.Numerics;
using AutoGreet.Services;
using AutoGreet.UI.Components;
using AutoGreet.UI.Tabs;
using Dalamud.Interface.Windowing;
using Dalamud.Bindings.ImGui;

namespace AutoGreet.UI;

public sealed class MainWindow : Window
{
    private readonly Configuration config;
    private readonly PersistenceService persistence;
    private readonly VenueService venueService;
    private readonly MainTab main;
    private readonly VisitorsTab visitorsTab;
    private readonly ActiveVisitorsTab activeVisitorsTab;
    private readonly QueueTab queue;
    private readonly DetectionService detectionService;
    private readonly Action openSettings;

    public MainWindow(Configuration config, VenueService venueService, VisitorService visitorService, QueueService queueService, DetectionService detectionService, PersistenceService persistence, Action openSettings)
        : base("AutoGreet###AutoGreetMainWindow", ImGuiWindowFlags.NoScrollbar | ImGuiWindowFlags.NoFocusOnAppearing)
    {
        this.config = config;
        this.persistence = persistence;
        this.venueService = venueService;
        this.detectionService = detectionService;
        this.openSettings = openSettings;
        SizeConstraints = new WindowSizeConstraints
        {
            MinimumSize = new System.Numerics.Vector2(780, 540),
            MaximumSize = new System.Numerics.Vector2(float.MaxValue, float.MaxValue)
        };
        main = new MainTab(config, venueService, visitorService, queueService, detectionService, persistence, openSettings);
        visitorsTab = new VisitorsTab(venueService, visitorService, queueService);
        activeVisitorsTab = new ActiveVisitorsTab(config, venueService, visitorService, queueService, detectionService);
        queue = new QueueTab(venueService);
    }

    public override void PreDraw() => AutoGreetTheme.Push();

    public override void PostDraw() => AutoGreetTheme.Pop();

    public override void Draw()
    {
        var venue = venueService.ActiveVenueOrNull;
        var session = venue?.Session;
        if (ImGui.BeginTabBar("AutoGreetTabs"))
        {
            if (ImGui.BeginTabItem("Main##main")) { main.Draw(); ImGui.EndTabItem(); }
            var ungreetedCount = session?.Ungreeted.Count ?? 0;
            var greetedCount = session?.Greeted.Count ?? 0;
            var activeVisitorCount = session?.NightlyVisitors.Count(v => v.Present) ?? (config.MonitorWhenNoVenueSelected ? detectionService.PresentKeys.Count : 0);
            var queueCount = venue?.Queue.Count(q => q.Status == Models.QueueEntryStatus.Waiting) ?? 0;
            if (ImGui.BeginTabItem($"Greets ({ungreetedCount}/{greetedCount})##greets")) { visitorsTab.Draw(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem($"Visitors ({activeVisitorCount})##active-visitors")) { activeVisitorsTab.Draw(); ImGui.EndTabItem(); }
            if (ImGui.BeginTabItem($"Queue ({queueCount})##queue")) { queue.Draw(); ImGui.EndTabItem(); }
            DrawSupportButtonOnTabRow();
            ImGui.EndTabBar();
        }
    }


    private static void DrawSupportButtonOnTabRow()
    {
        const string label = "      Support##autogreet-kofi-support";
        var buttonWidth = MathF.Max(116f, ImGui.CalcTextSize("Support").X + 52f);

        ImGui.SameLine();
        var available = ImGui.GetContentRegionAvail().X;
        if (available > buttonWidth + 8f)
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + available - buttonWidth);

        AutoGreetTheme.PushKofiButton();
        var clicked = ImGui.Button(label, new Vector2(buttonWidth, 0));
        AutoGreetTheme.PopKofiButton();

        DrawKofiCupIcon(ImGui.GetItemRectMin(), ImGui.GetItemRectMax());

        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Support me on Ko-Fi");

        if (clicked)
        {
            try
            {
                Process.Start(new ProcessStartInfo
                {
                    FileName = "https://ko-fi.com/airitsukino",
                    UseShellExecute = true,
                });
            }
            catch (Exception ex)
            {
                DalamudServices.Log.Warning(ex, "AutoGreet failed to open Ko-Fi link.");
            }
        }
    }

    private static void DrawKofiCupIcon(Vector2 min, Vector2 max)
    {
        var draw = ImGui.GetWindowDrawList();
        var centerY = (min.Y + max.Y) * 0.5f;
        var cupMin = new Vector2(min.X + 10f, centerY - 6f);
        var cupMax = new Vector2(min.X + 25f, centerY + 6f);
        var color = ImGui.GetColorU32(new Vector4(0.96f, 0.91f, 1.00f, 1f));
        var shadow = ImGui.GetColorU32(new Vector4(0.20f, 0.07f, 0.36f, 0.9f));
        var heart = ImGui.GetColorU32(new Vector4(0.78f, 0.28f, 1.00f, 1f));

        draw.AddRectFilled(cupMin + new Vector2(1f, 1f), cupMax + new Vector2(1f, 1f), shadow, 3f);
        draw.AddRectFilled(cupMin, cupMax, color, 3f);
        draw.AddRect(new Vector2(cupMax.X - 1f, centerY - 4f), new Vector2(cupMax.X + 6f, centerY + 4f), color, 4f, 0, 2f);
        draw.AddCircleFilled(new Vector2(cupMin.X + 5f, centerY - 1f), 2f, heart);
        draw.AddCircleFilled(new Vector2(cupMin.X + 8f, centerY - 1f), 2f, heart);
        draw.AddTriangleFilled(new Vector2(cupMin.X + 3f, centerY), new Vector2(cupMin.X + 10f, centerY), new Vector2(cupMin.X + 6.5f, centerY + 4f), heart);
    }

}
