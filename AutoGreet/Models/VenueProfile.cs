namespace AutoGreet.Models;

[Serializable]
public sealed class VenueProfile
{
    public Guid Id { get; set; } = Guid.NewGuid();
    public string Name { get; set; } = "Venue";
    public Dictionary<string, Visitor> LifetimeVisitors { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public SessionData Session { get; set; } = new();
    public List<GreetingProfile> GreetingProfiles { get; set; } = [];
    public Guid ActiveGreetingProfileId { get; set; }
    public Guid ActiveFirstTimeMacroId { get; set; }
    public Guid ActiveReturningMacroId { get; set; }
    public Guid ActiveVipMacroId { get; set; }
    public Guid ActiveBlacklistedMacroId { get; set; }
    public HashSet<string> Blacklist { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public List<QueueEntry> Queue { get; set; } = [];

    // Optional region routing. Guid.Empty means AutoGreet's default area:
    // whole housing interior when inside housing, or any enabled custom region in non-housing zones.
    public Guid DoorbellRegionId { get; set; }
    public Guid FirstTimeGreetingRegionId { get; set; }
    public Guid ReturningGreetingRegionId { get; set; }
    public Guid VipGreetingRegionId { get; set; }

    public GreetingProfile ActiveGreetingProfile
    {
        get
        {
            if (GreetingProfiles.Count == 0)
            {
                var profile = GreetingProfile.CreateDefault();
                GreetingProfiles.Add(profile);
                ActiveGreetingProfileId = profile.Id;
            }

            var active = GreetingProfiles.FirstOrDefault(x => x.Id == ActiveGreetingProfileId);
            if (active is not null) return active;
            ActiveGreetingProfileId = GreetingProfiles[0].Id;
            return GreetingProfiles[0];
        }
    }

    public Guid GetActiveMacroId(GreetingCategory category) => category switch
    {
        GreetingCategory.FirstTime => ActiveFirstTimeMacroId,
        GreetingCategory.Returning => ActiveReturningMacroId,
        GreetingCategory.Vip => ActiveVipMacroId,
        GreetingCategory.Blacklisted => ActiveBlacklistedMacroId,
        _ => Guid.Empty,
    };

    public void SetActiveMacroId(GreetingCategory category, Guid id)
    {
        switch (category)
        {
            case GreetingCategory.FirstTime:
                ActiveFirstTimeMacroId = id;
                break;
            case GreetingCategory.Returning:
                ActiveReturningMacroId = id;
                break;
            case GreetingCategory.Vip:
                ActiveVipMacroId = id;
                break;
            case GreetingCategory.Blacklisted:
                ActiveBlacklistedMacroId = id;
                break;
        }
    }

    public static VenueProfile CreateDefault(string name)
    {
        var profile = GreetingProfile.CreateDefault();
        var venue = new VenueProfile
        {
            Name = name,
            GreetingProfiles = [profile],
            ActiveGreetingProfileId = profile.Id,
        };
        venue.ActiveFirstTimeMacroId = profile.Macros.FirstOrDefault(m => m.Category == GreetingCategory.FirstTime)?.Id ?? Guid.Empty;
        venue.ActiveReturningMacroId = profile.Macros.FirstOrDefault(m => m.Category == GreetingCategory.Returning)?.Id ?? Guid.Empty;
        venue.ActiveVipMacroId = profile.Macros.FirstOrDefault(m => m.Category == GreetingCategory.Vip)?.Id ?? Guid.Empty;
        venue.ActiveBlacklistedMacroId = profile.Macros.FirstOrDefault(m => m.Category == GreetingCategory.Blacklisted)?.Id ?? Guid.Empty;
        return venue;
    }
}
