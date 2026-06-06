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
        var tabBarScreenPos = ImGui.GetCursorScreenPos();

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
            ImGui.EndTabBar();
        }

        DrawTopRightButtonsOnTabRow(tabBarScreenPos);
    }


    private void DrawTopRightButtonsOnTabRow(Vector2 tabBarScreenPos)
    {
        const string supportLabel = "##autogreet-kofi-support";
        const string supportText = "Support";
        const string settingsLabel = "Settings##autogreet-main-top-settings";
        const float rightMargin = 12f;
        const float buttonGap = 8f;
        const float topInset = 1f;
        const float buttonHeight = 20f;

        var supportWidth = MathF.Max(116f, ImGui.CalcTextSize(supportText).X + 52f);
        var settingsWidth = MathF.Max(88f, ImGui.CalcTextSize("Settings").X + 28f);
        var contentMax = ImGui.GetWindowContentRegionMax();
        var windowPos = ImGui.GetWindowPos();
        var supportPos = new Vector2(windowPos.X + contentMax.X - supportWidth - rightMargin, tabBarScreenPos.Y + topInset);
        var settingsPos = new Vector2(supportPos.X - settingsWidth - buttonGap, supportPos.Y);

        var savedCursor = ImGui.GetCursorScreenPos();

        ImGui.SetCursorScreenPos(settingsPos);
        if (ImGui.Button(settingsLabel, new Vector2(settingsWidth, buttonHeight)))
            openSettings();
        if (ImGui.IsItemHovered())
            ImGui.SetTooltip("Open AutoGreet settings");

        ImGui.SetCursorScreenPos(supportPos);
        AutoGreetTheme.PushKofiButton();
        var clicked = ImGui.Button(supportLabel, new Vector2(supportWidth, buttonHeight));
        AutoGreetTheme.PopKofiButton();

        var min = ImGui.GetItemRectMin();
        var max = ImGui.GetItemRectMax();
        DrawKofiCupIcon(min, max);
        DrawSupportButtonText(min, max, supportText);

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

        ImGui.SetCursorScreenPos(savedCursor);
    }

    private static void DrawSupportButtonText(Vector2 min, Vector2 max, string text)
    {
        var draw = ImGui.GetWindowDrawList();
        var textSize = ImGui.CalcTextSize(text);
        var textX = min.X + 40f;
        var textY = min.Y + ((max.Y - min.Y - textSize.Y) * 0.5f);
        var color = ImGui.GetColorU32(new Vector4(0.98f, 0.95f, 1.00f, 1f));
        draw.AddText(new Vector2(textX, textY), color, text);
    }

    private static void DrawKofiCupIcon(Vector2 min, Vector2 max)
    {
        var draw = ImGui.GetWindowDrawList();
        var centerY = (min.Y + max.Y) * 0.5f;
        var cupMin = new Vector2(min.X + 11f, centerY - 5f);
        var cupMax = new Vector2(min.X + 25f, centerY + 5f);
        var color = ImGui.GetColorU32(new Vector4(0.96f, 0.91f, 1.00f, 1f));
        var shadow = ImGui.GetColorU32(new Vector4(0.20f, 0.07f, 0.36f, 0.9f));
        var heart = ImGui.GetColorU32(new Vector4(0.78f, 0.28f, 1.00f, 1f));

        draw.AddRectFilled(cupMin + new Vector2(1f, 1f), cupMax + new Vector2(1f, 1f), shadow, 3f);
        draw.AddRectFilled(cupMin, cupMax, color, 3f);
        draw.AddRect(new Vector2(cupMax.X - 1f, centerY - 3.5f), new Vector2(cupMax.X + 5.5f, centerY + 3.5f), color, 4f, 0, 2f);
        draw.AddCircleFilled(new Vector2(cupMin.X + 4.7f, centerY - 0.8f), 1.8f, heart);
        draw.AddCircleFilled(new Vector2(cupMin.X + 7.9f, centerY - 0.8f), 1.8f, heart);
        draw.AddTriangleFilled(new Vector2(cupMin.X + 3f, centerY), new Vector2(cupMin.X + 9.6f, centerY), new Vector2(cupMin.X + 6.3f, centerY + 3.6f), heart);
    }

}
