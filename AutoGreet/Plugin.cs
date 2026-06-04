using AutoGreet.Services;
using AutoGreet.UI;
using Dalamud.Game.Command;
using Dalamud.Interface.Windowing;
using Dalamud.Plugin;

namespace AutoGreet;

public sealed class Plugin : IDalamudPlugin
{
    private const string CommandName = "/autogreet";
    private const string SettingsCommandName = "/autogreetsettings";
    private readonly WindowSystem windowSystem = new("AutoGreet");
    private readonly Configuration config;
    private readonly PersistenceService persistence;
    private readonly VenueService venues;
    private readonly DetectionService detection;
    private readonly GreetingService greetings;
    private readonly ChatCommandService chatCommands;
    private readonly SoundService sound;
    private readonly TargetingService targeting;
    private readonly EmoteResumeService emoteResume;
    private readonly MacroEngine macroEngine;
    private readonly QueueService queue;
    private readonly VisitorService visitors;
    private readonly MainWindow mainWindow;
    private readonly SettingsWindow settingsWindow;

    public Plugin(IDalamudPluginInterface pluginInterface)
    {
        DalamudServices.Initialize(pluginInterface);
        config = DalamudServices.PluginInterface.GetPluginConfig() as Configuration ?? new Configuration();
        MigrateBaseConfigDefaults(config);
        persistence = new PersistenceService(config);
        venues = new VenueService(config, persistence);
        detection = new DetectionService(config, persistence);
        greetings = new GreetingService(venues);
        chatCommands = new ChatCommandService();
        sound = new SoundService(config);
        targeting = new TargetingService();
        emoteResume = new EmoteResumeService(config, chatCommands);
        macroEngine = new MacroEngine(greetings, chatCommands, targeting);
        queue = new QueueService(config, venues, persistence, greetings, macroEngine, detection, emoteResume);
        visitors = new VisitorService(venues, persistence, queue, config, sound);
        greetings.AttachVisitorService(visitors);

        detection.PlayerEntered += visitors.OnPlayerEntered;
        detection.PlayerDoorbellEntered += visitors.OnPlayerDoorbellEntered;
        detection.PlayerPresentOnArrival += visitors.OnPlayerPresentOnArrival;
        detection.PlayerLeft += visitors.OnPlayerLeft;

        mainWindow = new MainWindow(config, venues, visitors, queue, detection, persistence, OpenSettingsWindow) { IsOpen = config.WindowVisible };
        settingsWindow = new SettingsWindow(config, venues, visitors, persistence, detection, greetings, sound, emoteResume) { IsOpen = config.SettingsWindowVisible };
        windowSystem.AddWindow(mainWindow);
        windowSystem.AddWindow(settingsWindow);

        DalamudServices.CommandManager.AddHandler(CommandName, new CommandInfo(OnCommand)
        {
            HelpMessage = "Toggle AutoGreet window."
        });
        DalamudServices.CommandManager.AddHandler(SettingsCommandName, new CommandInfo(OnSettingsCommand)
        {
            HelpMessage = "Toggle AutoGreet settings window."
        });
        DalamudServices.PluginInterface.UiBuilder.Draw += DrawUi;
        DalamudServices.PluginInterface.UiBuilder.OpenMainUi += ToggleMainUi;
        DalamudServices.PluginInterface.UiBuilder.OpenConfigUi += ToggleConfigUi;
        persistence.SaveNow();
    }


    private static void MigrateBaseConfigDefaults(Configuration config)
    {
        if (config.Version < 3)
        {
            if (Math.Abs(config.GreetingStartDelaySeconds - 1.0f) < 0.001f)
                config.GreetingStartDelaySeconds = 3.0f;
            if (Math.Abs(config.QueueDelaySeconds - 1.0f) < 0.001f)
                config.QueueDelaySeconds = 3.0f;
            config.Version = 3;
        }
    }

    private void OnCommand(string command, string arguments)
    {
        config.WindowVisible = !config.WindowVisible;
        mainWindow.IsOpen = config.WindowVisible;
        persistence.SaveNow();
    }

    private void OnSettingsCommand(string command, string arguments)
    {
        ToggleConfigUi();
    }

    private void OpenSettingsWindow()
    {
        config.SettingsWindowVisible = true;
        settingsWindow.IsOpen = true;
        persistence.SaveNow();
    }

    private void ToggleMainUi()
    {
        config.WindowVisible = !config.WindowVisible;
        mainWindow.IsOpen = config.WindowVisible;
        persistence.SaveNow();
    }

    private void ToggleConfigUi()
    {
        config.SettingsWindowVisible = !config.SettingsWindowVisible;
        settingsWindow.IsOpen = config.SettingsWindowVisible;
        persistence.SaveNow();
    }

    private void DrawUi()
    {
        // Do not force IsOpen from config every frame. Reassigning window state during
        // every draw can keep AutoGreet at the top of the ImGui z-order in some Dalamud
        // plugin draw-order situations. Commands/buttons update IsOpen directly; here we
        // only draw and then sync config from the actual close/open state.
        windowSystem.Draw();

        if (config.WindowVisible != mainWindow.IsOpen || config.SettingsWindowVisible != settingsWindow.IsOpen)
        {
            config.WindowVisible = mainWindow.IsOpen;
            config.SettingsWindowVisible = settingsWindow.IsOpen;
            persistence.SaveNow();
        }
    }

    public void Dispose()
    {
        persistence.SaveNow();
        detection.PlayerEntered -= visitors.OnPlayerEntered;
        detection.PlayerDoorbellEntered -= visitors.OnPlayerDoorbellEntered;
        detection.PlayerPresentOnArrival -= visitors.OnPlayerPresentOnArrival;
        detection.PlayerLeft -= visitors.OnPlayerLeft;
        DalamudServices.PluginInterface.UiBuilder.Draw -= DrawUi;
        DalamudServices.PluginInterface.UiBuilder.OpenMainUi -= ToggleMainUi;
        DalamudServices.PluginInterface.UiBuilder.OpenConfigUi -= ToggleConfigUi;
        DalamudServices.CommandManager.RemoveHandler(CommandName);
        DalamudServices.CommandManager.RemoveHandler(SettingsCommandName);
        windowSystem.RemoveAllWindows();
        queue.Dispose();
        greetings.Dispose();
        sound.Dispose();
        detection.Dispose();
        persistence.Dispose();
    }
}
