using System.Threading;
using AutoGreet.Models;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace AutoGreet.Services;

public sealed class PersistenceService : IDisposable
{
    private const int StorageVersion = 1;
    private readonly Configuration configuration;

    public List<VenueProfile> Venues { get; private set; } = [];
    public List<CustomDetectionRegion> CustomRegions { get; private set; } = [];
    private readonly SemaphoreSlim gate = new(1, 1);
    private bool disposed;

    public string DataDirectory { get; }

    private static readonly JsonSerializerSettings JsonSettings = new()
    {
        Formatting = Formatting.Indented,
        TypeNameHandling = TypeNameHandling.None,
        ObjectCreationHandling = ObjectCreationHandling.Replace,
    };

    public PersistenceService(Configuration configuration)
    {
        this.configuration = configuration;
        DataDirectory = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncher", "pluginConfigs", "AutoGreet");
        LoadExternalDataOrMigrateLegacy();
    }

    public void SaveNow()
    {
        if (disposed) return;
        try
        {
            gate.Wait(1000);
            Directory.CreateDirectory(DataDirectory);
            SaveExternalDataUnsafe();
            DalamudServices.PluginInterface.SavePluginConfig(configuration);
            PruneLegacyBaseConfigFileUnsafe();
        }
        catch (Exception ex)
        {
            DalamudServices.Log.Error(ex, "Failed to save AutoGreet configuration.");
        }
        finally
        {
            if (gate.CurrentCount == 0) gate.Release();
        }
    }

    private void LoadExternalDataOrMigrateLegacy()
    {
        try
        {
            Directory.CreateDirectory(DataDirectory);
            if (HasSplitData())
            {
                LoadExternalDataUnsafe();
                return;
            }

            if (TryLoadLegacyVenues(out var legacyVenues) && legacyVenues.Count > 0)
            {
                Venues = legacyVenues;
                SaveExternalDataUnsafe();
                return;
            }

            EnsureDefaultVenue();
            SaveExternalDataUnsafe();
        }
        catch (Exception ex)
        {
            DalamudServices.Log.Error(ex, "Failed to load AutoGreet split storage. Falling back to default venue data.");
            EnsureDefaultVenue();
        }
    }

    private bool HasSplitData() =>
        File.Exists(Path.Combine(DataDirectory, "VenueProfiles.json")) ||
        File.Exists(Path.Combine(DataDirectory, "GreetingProfiles.json")) ||
        File.Exists(Path.Combine(DataDirectory, "VisitorHistory.json")) ||
        File.Exists(Path.Combine(DataDirectory, "CustomRegions.json"));

    private void LoadExternalDataUnsafe()
    {
        var venueFile = LoadFile<VenueProfilesFile>("VenueProfiles.json") ?? new VenueProfilesFile();
        var greetingsFile = LoadFile<GreetingProfilesFile>("GreetingProfiles.json") ?? new GreetingProfilesFile();
        var visitorsFile = LoadFile<VisitorHistoryFile>("VisitorHistory.json") ?? new VisitorHistoryFile();
        var sessionsFile = LoadFile<SessionsFile>("Sessions.json") ?? new SessionsFile();
        var queuesFile = LoadFile<QueuesFile>("Queues.json") ?? new QueuesFile();
        var regionsFile = LoadFile<CustomRegionsFile>("CustomRegions.json") ?? new CustomRegionsFile();
        CustomRegions = regionsFile.Regions
            .Where(r => r.Id != Guid.Empty && r.TerritoryType != 0)
            .ToList();

        configuration.ActiveVenueId = venueFile.ActiveVenueId != Guid.Empty ? venueFile.ActiveVenueId : configuration.ActiveVenueId;
        Venues = [];

        foreach (var record in venueFile.Venues.Where(v => v.Id != Guid.Empty))
        {
            var id = record.Id.ToString();
            var venue = new VenueProfile
            {
                Id = record.Id,
                Name = string.IsNullOrWhiteSpace(record.Name) ? "Venue" : record.Name.Trim(),
                ActiveGreetingProfileId = record.ActiveGreetingProfileId,
                ActiveFirstTimeMacroId = record.ActiveFirstTimeMacroId,
                ActiveReturningMacroId = record.ActiveReturningMacroId,
                ActiveVipMacroId = record.ActiveVipMacroId,
                ActiveBlacklistedMacroId = record.ActiveBlacklistedMacroId,
                DoorbellRegionId = record.DoorbellRegionId,
                VisitorListRegionId = record.VisitorListRegionId != Guid.Empty ? record.VisitorListRegionId : record.DoorbellRegionId,
                FirstTimeGreetingRegionId = record.FirstTimeGreetingRegionId,
                ReturningGreetingRegionId = record.ReturningGreetingRegionId,
                VipGreetingRegionId = record.VipGreetingRegionId,
                PlotLock = record.PlotLock ?? new VenuePlotLock(),
                CustomRegionMacroRoutes = record.CustomRegionMacroRoutes ?? [],
                Blacklist = new HashSet<string>(record.Blacklist ?? [], StringComparer.OrdinalIgnoreCase),
                GreetingProfiles = greetingsFile.GreetingProfilesByVenue.TryGetValue(id, out var profiles) ? profiles : [],
                LifetimeVisitors = visitorsFile.LifetimeVisitorsByVenue.TryGetValue(id, out var visitors)
                    ? new Dictionary<string, Visitor>(visitors, StringComparer.OrdinalIgnoreCase)
                    : new Dictionary<string, Visitor>(StringComparer.OrdinalIgnoreCase),
                Session = sessionsFile.SessionsByVenue.TryGetValue(id, out var session) ? session : new SessionData(),
                Queue = queuesFile.QueuesByVenue.TryGetValue(id, out var queue) ? queue : [],
            };

            Venues.Add(venue);
        }

        EnsureDefaultVenue();
    }

    private void SaveExternalDataUnsafe()
    {
        EnsureDefaultVenue();

        var venueFile = new VenueProfilesFile
        {
            ActiveVenueId = configuration.ActiveVenueId,
            Venues = this.Venues.Select(v => new VenueRecord
            {
                Id = v.Id,
                Name = v.Name,
                ActiveGreetingProfileId = v.ActiveGreetingProfileId,
                ActiveFirstTimeMacroId = v.ActiveFirstTimeMacroId,
                ActiveReturningMacroId = v.ActiveReturningMacroId,
                ActiveVipMacroId = v.ActiveVipMacroId,
                ActiveBlacklistedMacroId = v.ActiveBlacklistedMacroId,
                DoorbellRegionId = v.DoorbellRegionId,
                VisitorListRegionId = v.VisitorListRegionId,
                FirstTimeGreetingRegionId = v.FirstTimeGreetingRegionId,
                ReturningGreetingRegionId = v.ReturningGreetingRegionId,
                VipGreetingRegionId = v.VipGreetingRegionId,
                PlotLock = v.PlotLock,
                CustomRegionMacroRoutes = v.CustomRegionMacroRoutes,
                Blacklist = v.Blacklist.Order(StringComparer.OrdinalIgnoreCase).ToList(),
            }).ToList(),
        };

        var greetingsFile = new GreetingProfilesFile
        {
            GreetingProfilesByVenue = this.Venues.ToDictionary(v => v.Id.ToString(), v => v.GreetingProfiles)
        };

        var visitorsFile = new VisitorHistoryFile
        {
            LifetimeVisitorsByVenue = this.Venues.ToDictionary(v => v.Id.ToString(), v => new Dictionary<string, Visitor>(v.LifetimeVisitors, StringComparer.OrdinalIgnoreCase))
        };

        var sessionsFile = new SessionsFile
        {
            SessionsByVenue = this.Venues.ToDictionary(v => v.Id.ToString(), v => v.Session)
        };

        var queuesFile = new QueuesFile
        {
            QueuesByVenue = this.Venues.ToDictionary(v => v.Id.ToString(), v => v.Queue)
        };

        SaveFile("VenueProfiles.json", venueFile);
        SaveFile("GreetingProfiles.json", greetingsFile);
        SaveFile("VisitorHistory.json", visitorsFile);
        SaveFile("Sessions.json", sessionsFile);
        SaveFile("Queues.json", queuesFile);

        var regionsFile = new CustomRegionsFile
        {
            Regions = this.CustomRegions
                .Where(r => r.Id != Guid.Empty && r.TerritoryType != 0)
                .ToList(),
        };
        SaveFile("CustomRegions.json", regionsFile);
    }


    private void PruneLegacyBaseConfigFileUnsafe()
    {
        // Defensive cleanup for users migrating from the original single-file config.
        // Older builds stored huge venue/session/visitor objects on AutoGreet.json.
        // If Dalamud preserves unknown JSON members or an old build wrote the file during
        // migration testing, remove those legacy payloads so the base config remains small.
        try
        {
            var baseConfigPath = Path.Combine(
                Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData),
                "XIVLauncher",
                "pluginConfigs",
                "AutoGreet.json");

            if (!File.Exists(baseConfigPath)) return;

            var root = JObject.Parse(File.ReadAllText(baseConfigPath));
            var changed = false;
            foreach (var legacyKey in new[]
                     {
                         "Venues",
                         "ActiveVenue",
                         "GreetingProfiles",
                         "LifetimeVisitors",
                         "Session",
                         "Queue",
                         "Blacklist"
                     })
            {
                if (root.Property(legacyKey) is { } prop)
                {
                    prop.Remove();
                    changed = true;
                }
            }

            if (root["Version"] is null || root["Version"]!.Value<int>() < 2)
            {
                root["Version"] = 2;
                changed = true;
            }

            if (changed)
                File.WriteAllText(baseConfigPath, root.ToString(Formatting.Indented));
        }
        catch (Exception ex)
        {
            DalamudServices.Log.Warning(ex, "AutoGreet could not prune legacy data from base config file.");
        }
    }

    private bool TryLoadLegacyVenues(out List<VenueProfile> venues)
    {
        venues = [];
        try
        {
            var legacyPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ApplicationData), "XIVLauncher", "pluginConfigs", "AutoGreet.json");
            if (!File.Exists(legacyPath)) return false;

            var json = File.ReadAllText(legacyPath);
            var root = JObject.Parse(json);
            var venuesToken = root["Venues"];
            if (venuesToken is null) return false;

            var serializer = JsonSerializer.Create(new JsonSerializerSettings
            {
                TypeNameHandling = TypeNameHandling.Auto,
                ObjectCreationHandling = ObjectCreationHandling.Replace,
            });

            venues = venuesToken.ToObject<List<VenueProfile>>(serializer) ?? [];
            return venues.Count > 0;
        }
        catch (Exception ex)
        {
            DalamudServices.Log.Warning(ex, "AutoGreet legacy config migration could not read Venues from the old base config.");
            return false;
        }
    }

    private T? LoadFile<T>(string fileName) where T : class
    {
        var path = Path.Combine(DataDirectory, fileName);
        if (!File.Exists(path)) return null;
        return JsonConvert.DeserializeObject<T>(File.ReadAllText(path), JsonSettings);
    }

    private void SaveFile<T>(string fileName, T value)
    {
        var path = Path.Combine(DataDirectory, fileName);
        var temp = path + ".tmp";
        File.WriteAllText(temp, JsonConvert.SerializeObject(value, JsonSettings));
        if (File.Exists(path)) File.Delete(path);
        File.Move(temp, path);
    }

    private void EnsureDefaultVenue()
    {
        if (Venues.Count == 0)
        {
            var venue = VenueProfile.CreateDefault("Default Venue");
            Venues.Add(venue);
            configuration.ActiveVenueId = venue.Id;
        }

        if (configuration.ActiveVenueId == Guid.Empty || Venues.All(v => v.Id != configuration.ActiveVenueId))
            configuration.ActiveVenueId = Venues[0].Id;
    }

    public void Dispose()
    {
        disposed = true;
        gate.Dispose();
    }

    private sealed class VenueProfilesFile
    {
        public int Version { get; set; } = StorageVersion;
        public Guid ActiveVenueId { get; set; }
        public List<VenueRecord> Venues { get; set; } = [];
    }

    private sealed class VenueRecord
    {
        public Guid Id { get; set; }
        public string Name { get; set; } = "Venue";
        public Guid ActiveGreetingProfileId { get; set; }
        public Guid ActiveFirstTimeMacroId { get; set; }
        public Guid ActiveReturningMacroId { get; set; }
        public Guid ActiveVipMacroId { get; set; }
        public Guid ActiveBlacklistedMacroId { get; set; }
        public Guid DoorbellRegionId { get; set; }
        public Guid VisitorListRegionId { get; set; }
        public Guid FirstTimeGreetingRegionId { get; set; }
        public Guid ReturningGreetingRegionId { get; set; }
        public Guid VipGreetingRegionId { get; set; }
        public VenuePlotLock PlotLock { get; set; } = new();
        public List<CustomRegionMacroRoute> CustomRegionMacroRoutes { get; set; } = [];
        public List<string> Blacklist { get; set; } = [];
    }

    private sealed class GreetingProfilesFile
    {
        public int Version { get; set; } = StorageVersion;
        public Dictionary<string, List<GreetingProfile>> GreetingProfilesByVenue { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class VisitorHistoryFile
    {
        public int Version { get; set; } = StorageVersion;
        public Dictionary<string, Dictionary<string, Visitor>> LifetimeVisitorsByVenue { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class SessionsFile
    {
        public int Version { get; set; } = StorageVersion;
        public Dictionary<string, SessionData> SessionsByVenue { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class QueuesFile
    {
        public int Version { get; set; } = StorageVersion;
        public Dictionary<string, List<QueueEntry>> QueuesByVenue { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    }

    private sealed class CustomRegionsFile
    {
        public int Version { get; set; } = StorageVersion;
        public List<CustomDetectionRegion> Regions { get; set; } = [];
    }
}
