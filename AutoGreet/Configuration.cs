using Dalamud.Configuration;

namespace AutoGreet;

[Serializable]
public sealed class Configuration : IPluginConfiguration
{
    public int Version { get; set; } = 3;
    public bool WindowVisible { get; set; }
    public bool SettingsWindowVisible { get; set; }
    public bool AutoGreetEnabled { get; set; }
    public bool DoorbellSoundEnabled { get; set; } = true;
    public float DoorbellVolume { get; set; } = 0.25f;
    public string CustomDoorbellSoundPath { get; set; } = string.Empty;
    public bool ChatNotificationsEnabled { get; set; } = true;
    public bool ChatNotificationsForBlacklistedEnabled { get; set; } = true;
    public bool LeaveChatNotificationsEnabled { get; set; } = false;
    public bool ResumePreviousEmoteEnabled { get; set; } = false;
    public float GreetingStartDelaySeconds { get; set; } = 3.0f;
    public float QueueDelaySeconds { get; set; } = 3.0f;
    public Guid ActiveVenueId { get; set; }
    public bool ActiveVenueDisabled { get; set; }
    public bool MonitorWhenNoVenueSelected { get; set; } = false;
    public List<uint> CustomHousingTerritories { get; set; } = [];
}
