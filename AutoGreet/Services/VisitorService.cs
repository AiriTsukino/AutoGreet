using AutoGreet.Models;

namespace AutoGreet.Services;

public sealed class VisitorService
{
    private readonly VenueService venues;
    private readonly PersistenceService persistence;
    private readonly QueueService queue;
    private readonly Configuration config;
    private readonly SoundService sound;

    public VisitorService(VenueService venues, PersistenceService persistence, QueueService queue, Configuration config, SoundService sound)
    {
        this.venues = venues;
        this.persistence = persistence;
        this.queue = queue;
        this.config = config;
        this.sound = sound;
    }


    public void OnPlayerPresentOnArrival(VisitorKey key)
    {
        var venue = venues.ActiveVenueOrNull;
        if (venue is null) return;
        if (venue.Blacklist.Contains(key.ToString()))
        {
            EnsureBlacklistedSessionPresence(venue, key, hereWhenArrived: true, countVisit: false);
            if (config.ChatNotificationsEnabled && config.ChatNotificationsForBlacklistedEnabled)
                DalamudServices.ChatGui.Print($"[AutoGreet] {key.Display} was already in {venue.Name} when you arrived.");
            return;
        }

        var session = venue.Session;
        var wasKnown = venue.LifetimeVisitors.TryGetValue(key.ToString(), out var visitor);
        visitor ??= Visitor.FromKey(key);
        visitor.LastSeenUtc = DateTimeOffset.UtcNow;
        venue.LifetimeVisitors[key.ToString()] = visitor;

        var existing = session.NightlyVisitors.FirstOrDefault(v => SameKey(v.Key, key));
        if (existing is null)
        {
            existing = new SessionVisitorState
            {
                Key = key,
                EnteredUtc = DateTimeOffset.UtcNow,
                LastSeenUtc = DateTimeOffset.UtcNow,
                ReturningThisSession = HasBeenGreetedBefore(visitor),
                Present = true,
                HereWhenArrived = true,
            };
            session.NightlyVisitors.Add(existing);
        }
        else
        {
            existing.Present = true;
            existing.LastSeenUtc = DateTimeOffset.UtcNow;
            existing.ReturningThisSession = HasBeenGreetedBefore(visitor);
            if (!VenueService.ContainsKey(session.Ungreeted, key) && !VenueService.ContainsKey(session.Skipped, key))
                existing.HereWhenArrived = true;
        }

        // People already present when the greeter arrives should not be auto-queued.
        // Put them in the greeted list with a visible tag so shift handoffs do not
        // accidentally greet an already-active crowd.
        if (!VenueService.ContainsKey(session.Ungreeted, key) && !VenueService.ContainsKey(session.Skipped, key) && !VenueService.ContainsKey(session.Greeted, key))
            session.Greeted.Add(key);

        if (config.ChatNotificationsEnabled)
            DalamudServices.ChatGui.Print($"[AutoGreet] {key.Display} was already in {venue.Name} when you arrived.");

        venues.RepairActiveVenueData();
    }

    public void OnPlayerDoorbellEntered(VisitorKey key)
    {
        var venue = venues.ActiveVenueOrNull;
        if (venue is null)
        {
            NotifyPausedMonitorEntry(key);
            return;
        }
        if (venue.Blacklist.Contains(key.ToString()))
        {
            EnsureBlacklistedSessionPresence(venue, key, hereWhenArrived: false, countVisit: true);
            if (config.DoorbellSoundEnabled)
                sound.PlayDoorbell();

            if (config.ChatNotificationsEnabled && config.ChatNotificationsForBlacklistedEnabled)
                DalamudServices.ChatGui.Print($"[AutoGreet] {key.Display} entered {venue.Name}.");
            return;
        }

        var session = venue.Session;
        var existing = session.NightlyVisitors.FirstOrDefault(v => SameKey(v.Key, key));
        var wasKnown = venue.LifetimeVisitors.TryGetValue(key.ToString(), out var visitor);
        visitor ??= Visitor.FromKey(key);
        visitor.LastSeenUtc = DateTimeOffset.UtcNow;
        if (existing is null || !existing.Present)
            visitor.TotalVisitCount++;
        venue.LifetimeVisitors[key.ToString()] = visitor;

        if (existing is null)
        {
            session.NightlyVisitors.Add(new SessionVisitorState
            {
                Key = key,
                EnteredUtc = DateTimeOffset.UtcNow,
                LastSeenUtc = DateTimeOffset.UtcNow,
                ReturningThisSession = HasBeenGreetedBefore(visitor),
                Present = true,
            });
        }
        else
        {
            existing.Present = true;
            existing.LastSeenUtc = DateTimeOffset.UtcNow;
            existing.ReturningThisSession = HasBeenGreetedBefore(visitor);
        }

        if (config.DoorbellSoundEnabled)
            sound.PlayDoorbell();

        if (config.ChatNotificationsEnabled)
            DalamudServices.ChatGui.Print($"[AutoGreet] {key.Display} entered {venue.Name}.");

        venues.RepairActiveVenueData();
    }

    private void NotifyPausedMonitorEntry(VisitorKey key)
    {
        if (!config.ActiveVenueDisabled || !config.MonitorWhenNoVenueSelected) return;

        if (config.DoorbellSoundEnabled)
            sound.PlayDoorbell();

        if (config.ChatNotificationsEnabled)
            DalamudServices.ChatGui.Print($"[AutoGreet] {key.Display} entered the monitored area.");
    }

    private void NotifyPausedMonitorLeave(VisitorKey key)
    {
        if (!config.ActiveVenueDisabled || !config.MonitorWhenNoVenueSelected) return;

        if (config.ChatNotificationsEnabled && config.LeaveChatNotificationsEnabled)
            DalamudServices.ChatGui.Print($"[AutoGreet] {key.Display} left the monitored area.");
    }

    public void OnPlayerEntered(VisitorKey key)
    {
        var venue = venues.ActiveVenueOrNull;
        if (venue is null)
        {
            NotifyPausedMonitorEntry(key);
            return;
        }
        if (venue.Blacklist.Contains(key.ToString()))
        {
            EnsureBlacklistedSessionPresence(venue, key, hereWhenArrived: false, countVisit: true);
            if (config.DoorbellSoundEnabled)
                sound.PlayDoorbell();

            if (config.ChatNotificationsEnabled && config.ChatNotificationsForBlacklistedEnabled)
                DalamudServices.ChatGui.Print($"[AutoGreet] {key.Display} entered {venue.Name}.");
            return;
        }

        var session = venue.Session;
        var existing = session.NightlyVisitors.FirstOrDefault(v => SameKey(v.Key, key));
        var alreadyPresentFromDoorbell = existing?.Present == true;

        var wasKnown = venue.LifetimeVisitors.TryGetValue(key.ToString(), out var visitor);
        visitor ??= Visitor.FromKey(key);
        visitor.LastSeenUtc = DateTimeOffset.UtcNow;
        if (existing is null || !existing.Present)
            visitor.TotalVisitCount++;
        venue.LifetimeVisitors[key.ToString()] = visitor;

        // If this visitor already triggered the configured doorbell area, do not print/play
        // a second entry notification when they later enter a greeting-only region.
        if (!alreadyPresentFromDoorbell)
        {
            if (config.DoorbellSoundEnabled)
                sound.PlayDoorbell();

            if (config.ChatNotificationsEnabled)
                DalamudServices.ChatGui.Print($"[AutoGreet] {key.Display} entered {venue.Name}.");
        }

        if (existing is null)
        {
            session.NightlyVisitors.Add(new SessionVisitorState
            {
                Key = key,
                EnteredUtc = DateTimeOffset.UtcNow,
                LastSeenUtc = DateTimeOffset.UtcNow,
                ReturningThisSession = HasBeenGreetedBefore(visitor),
                Present = true,
            });

            if (!VenueService.ContainsKey(session.Greeted, key) && !VenueService.ContainsKey(session.Skipped, key) && !VenueService.ContainsKey(session.Ungreeted, key))
                session.Ungreeted.Add(key);

            if (config.AutoGreetEnabled && !VenueService.ContainsKey(session.Greeted, key) && !VenueService.ContainsKey(session.Skipped, key))
                queue.Enqueue(key);
        }
        else
        {
            existing.Present = true;
            existing.LastSeenUtc = DateTimeOffset.UtcNow;
            existing.ReturningThisSession = HasBeenGreetedBefore(visitor);

            if (existing.HereWhenArrived && !VenueService.ContainsKey(session.Skipped, key))
            {
                existing.HereWhenArrived = false;
                VenueService.RemoveKey(session.Greeted, key);
                if (!VenueService.ContainsKey(session.Ungreeted, key))
                    session.Ungreeted.Add(key);

                if (config.AutoGreetEnabled)
                    queue.Enqueue(key);
            }
            else if (!VenueService.ContainsKey(session.Greeted, key) && !VenueService.ContainsKey(session.Skipped, key) && !VenueService.ContainsKey(session.Ungreeted, key))
            {
                session.Ungreeted.Add(key);
                if (config.AutoGreetEnabled)
                    queue.Enqueue(key);
            }
            else if (VenueService.ContainsKey(session.Greeted, key))
            {
                VenueService.RemoveKey(session.Greeted, key);
                session.Greeted.Insert(0, key);
            }
        }

        venues.RepairActiveVenueData();
    }

    public void OnPlayerLeft(VisitorKey key)
    {
        var venue = venues.ActiveVenueOrNull;
        if (venue is null)
        {
            NotifyPausedMonitorLeave(key);
            return;
        }
        var existing = venue.Session.NightlyVisitors.FirstOrDefault(v => SameKey(v.Key, key));
        if (existing is not null)
        {
            existing.Present = false;
            existing.LastSeenUtc = DateTimeOffset.UtcNow;
        }

        if (config.ChatNotificationsEnabled && config.LeaveChatNotificationsEnabled)
        {
            var prefix = venue.Blacklist.Contains(key.ToString()) ? "" : string.Empty;
            DalamudServices.ChatGui.Print($"[AutoGreet] {prefix}{key.Display} left {venue.Name}.");
        }

        queue.Cancel(key, "Visitor left");
        venues.RepairActiveVenueData();
    }

    public void MarkGreeted(VisitorKey key)
    {
        var venue = venues.ActiveVenueOrNull;
        if (venue is null) return;
        var session = venue.Session;
        VenueService.RemoveKey(session.Ungreeted, key);
        VenueService.RemoveKey(session.Skipped, key);
        VenueService.RemoveKey(session.Greeted, key);
        session.Greeted.Insert(0, key);
        queue.Cancel(key, "Manually marked greeted");
        var visitor = venue.LifetimeVisitors.TryGetValue(key.ToString(), out var existingVisitor) ? existingVisitor : Visitor.FromKey(key);
        visitor.LastSeenUtc = DateTimeOffset.UtcNow;
        visitor.HasBeenGreeted = true;
        venue.LifetimeVisitors[key.ToString()] = visitor;
        var existing = EnsureSessionVisitor(key);
        existing.HereWhenArrived = false;
        existing.ReturningThisSession = false;
        venues.RepairActiveVenueData();
    }

    public void Skip(VisitorKey key)
    {
        var venue = venues.ActiveVenueOrNull;
        if (venue is null) return;
        var session = venue.Session;
        VenueService.RemoveKey(session.Ungreeted, key);
        VenueService.RemoveKey(session.Skipped, key);
        session.Skipped.Add(key);
        queue.Cancel(key, "Skipped");
        EnsureSessionVisitor(key);
        venues.RepairActiveVenueData();
    }

    public void MoveToUngreeted(VisitorKey key)
    {
        var venue = venues.ActiveVenueOrNull;
        if (venue is null) return;
        var session = venue.Session;
        VenueService.RemoveKey(session.Greeted, key);
        VenueService.RemoveKey(session.Skipped, key);
        if (!VenueService.ContainsKey(session.Ungreeted, key)) session.Ungreeted.Add(key);
        var existing = EnsureSessionVisitor(key);
        existing.HereWhenArrived = false;
        venues.RepairActiveVenueData();
    }

    public void ToggleBlacklist(VisitorKey key)
    {
        var venue = venues.ActiveVenueOrNull;
        if (venue is null) return;
        if (!venue.Blacklist.Remove(key.ToString())) venue.Blacklist.Add(key.ToString());
        queue.Cancel(key, "Blacklisted");
        VenueService.RemoveKey(venue.Session.Ungreeted, key);
        VenueService.RemoveKey(venue.Session.Greeted, key);
        VenueService.RemoveKey(venue.Session.Skipped, key);
        venues.RepairActiveVenueData();
    }


    public void SetVip(VisitorKey key, bool isVip)
    {
        var venue = venues.ActiveVenueOrNull;
        if (venue is null) return;
        if (!venue.LifetimeVisitors.TryGetValue(key.ToString(), out var visitor))
            visitor = Visitor.FromKey(key);

        visitor.Vip = isVip;
        visitor.LastSeenUtc = DateTimeOffset.UtcNow;
        venue.LifetimeVisitors[key.ToString()] = visitor;
        venues.RepairActiveVenueData();
    }

    public void ToggleVip(VisitorKey key)
    {
        var venue = venues.ActiveVenueOrNull;
        if (venue is null) return;
        var currentlyVip = venue.LifetimeVisitors.TryGetValue(key.ToString(), out var visitor) && visitor.Vip;
        SetVip(key, !currentlyVip);
    }

    public void SetBlacklist(VisitorKey key, bool blacklisted)
    {
        var venue = venues.ActiveVenueOrNull;
        if (venue is null) return;
        if (blacklisted)
        {
            venue.Blacklist.Add(key.ToString());
            queue.Cancel(key, "Blacklisted");
            VenueService.RemoveKey(venue.Session.Ungreeted, key);
            VenueService.RemoveKey(venue.Session.Greeted, key);
            VenueService.RemoveKey(venue.Session.Skipped, key);
        }
        else
        {
            venue.Blacklist.Remove(key.ToString());
        }

        if (!venue.LifetimeVisitors.TryGetValue(key.ToString(), out var visitor))
            venue.LifetimeVisitors[key.ToString()] = Visitor.FromKey(key);

        venues.RepairActiveVenueData();
    }

    public void SaveNightlySnapshot()
    {
        var venue = venues.ActiveVenueOrNull;
        if (venue is null) return;
        var s = venue.Session;
        s.Snapshots.Add(new NightlySnapshot
        {
            TotalVisitors = s.NightlyVisitors.Count,
            GreetedCount = s.Greeted.Count,
            UngreetedCount = s.Ungreeted.Count
        });
        persistence.SaveNow();
    }

    public void ResetSession()
    {
        var venue = venues.ActiveVenueOrNull;
        if (venue is null) return;
        venue.Session.Reset();
        venue.Queue.Clear();
        persistence.SaveNow();
    }

    public int ImportCurrentVisitorsForGreeting(IEnumerable<VisitorKey> keys)
    {
        var venue = venues.ActiveVenueOrNull;
        if (venue is null) return 0;

        var imported = 0;
        foreach (var key in keys)
        {
            if (venue.Blacklist.Contains(key.ToString())) continue;

            if (!venue.LifetimeVisitors.TryGetValue(key.ToString(), out var visitor))
                visitor = Visitor.FromKey(key);
            visitor.LastSeenUtc = DateTimeOffset.UtcNow;
            venue.LifetimeVisitors[key.ToString()] = visitor;

            var session = venue.Session;
            var state = EnsureSessionVisitor(key);
            state.Present = true;
            state.LastSeenUtc = DateTimeOffset.UtcNow;
            state.HereWhenArrived = false;
            state.ReturningThisSession = HasBeenGreetedBefore(visitor);

            // Manual scan means the greeter intentionally wants to greet currently present people.
            // If they were only in Greeted because they were here on arrival, move them to Ungreeted.
            VenueService.RemoveKey(session.Greeted, key);
            if (VenueService.ContainsKey(session.Skipped, key) || VenueService.ContainsKey(session.Ungreeted, key))
                continue;

            session.Ungreeted.Add(key);
            imported++;
            if (config.AutoGreetEnabled)
                queue.Enqueue(key);
        }

        venues.RepairActiveVenueData();
        if (imported > 0 && config.ChatNotificationsEnabled)
            DalamudServices.ChatGui.Print($"[AutoGreet] Manual scan added {imported} visitor{(imported == 1 ? string.Empty : "s")} to the ungreeted list.");
        return imported;
    }

    private void EnsureBlacklistedSessionPresence(VenueProfile venue, VisitorKey key, bool hereWhenArrived, bool countVisit)
    {
        if (!config.ShowBlacklistedInActiveVisitors)
            return;

        var now = DateTimeOffset.UtcNow;
        var session = venue.Session;
        var existing = session.NightlyVisitors.FirstOrDefault(v => SameKey(v.Key, key));
        var visitor = venue.LifetimeVisitors.TryGetValue(key.ToString(), out var existingVisitor) ? existingVisitor : Visitor.FromKey(key);

        if (countVisit && (existing is null || !existing.Present))
            visitor.TotalVisitCount++;

        visitor.LastSeenUtc = now;
        venue.LifetimeVisitors[key.ToString()] = visitor;

        if (existing is null)
        {
            session.NightlyVisitors.Add(new SessionVisitorState
            {
                Key = key,
                EnteredUtc = now,
                LastSeenUtc = now,
                ReturningThisSession = HasBeenGreetedBefore(visitor),
                Present = true,
                HereWhenArrived = hereWhenArrived,
            });
        }
        else
        {
            existing.Present = true;
            existing.LastSeenUtc = now;
            existing.ReturningThisSession = HasBeenGreetedBefore(visitor);
            if (hereWhenArrived)
                existing.HereWhenArrived = true;
        }

        venues.RepairActiveVenueData();
    }

    private SessionVisitorState EnsureSessionVisitor(VisitorKey key)
    {
        var venue = venues.ActiveVenueOrNull ?? venues.ActiveVenue;
        var session = venue.Session;
        var existing = session.NightlyVisitors.FirstOrDefault(v => SameKey(v.Key, key));
        if (existing is not null) return existing;

        existing = new SessionVisitorState { Key = key, Present = false, ReturningThisSession = false };
        session.NightlyVisitors.Add(existing);
        return existing;
    }

    private static bool HasBeenGreetedBefore(Visitor? visitor) => visitor?.HasBeenGreeted == true;

    private static bool SameKey(VisitorKey a, VisitorKey b) =>
        string.Equals(a.ToString(), b.ToString(), StringComparison.OrdinalIgnoreCase);
}
