using AutoGreet.Models;

namespace AutoGreet.Services;

public sealed class QueueService : IDisposable
{
    private readonly Configuration config;
    private readonly VenueService venues;
    private readonly PersistenceService persistence;
    private readonly GreetingService greetings;
    private readonly MacroEngine macroEngine;
    private readonly DetectionService detection;
    private readonly EmoteResumeService emoteResume;
    private readonly DiagnosticLogService logs;
    private CancellationTokenSource cts = new();
    private Task? worker;
    private bool workerForceMode;
    private readonly object sync = new();

    public QueueService(Configuration config, VenueService venues, PersistenceService persistence, GreetingService greetings, MacroEngine macroEngine, DetectionService detection, EmoteResumeService emoteResume, DiagnosticLogService logs)
    {
        this.config = config;
        this.venues = venues;
        this.persistence = persistence;
        this.greetings = greetings;
        this.macroEngine = macroEngine;
        this.detection = detection;
        this.emoteResume = emoteResume;
        this.logs = logs;
    }

    public IReadOnlyList<QueueEntry> Entries => venues.ActiveVenueOrNull?.Queue ?? [];
    public bool IsRunning => worker is { IsCompleted: false };

    public void EnqueueEligibleUngreeted(bool forceStart = false)
    {
        var venue = venues.ActiveVenueOrNull;
        if (venue is null) return;
        foreach (var key in venue.Session.Ungreeted.ToArray())
        {
            if (venue.Blacklist.Contains(key.ToString())) continue;
            if (VenueService.ContainsKey(venue.Session.Greeted, key) || VenueService.ContainsKey(venue.Session.Skipped, key)) continue;
            Enqueue(key, forceStart);
        }

        if (forceStart || config.AutoGreetEnabled) EnsureWorker(forceStart);
    }

    public void Enqueue(VisitorKey key, bool forceStart = false)
    {
        var venue = venues.ActiveVenueOrNull;
        if (venue is null) return;
        if (venue.Blacklist.Contains(key.ToString())) return;
        if (venue.Queue.Any(q => string.Equals(q.Visitor.ToString(), key.ToString(), StringComparison.OrdinalIgnoreCase) && q.Status is (QueueEntryStatus.Waiting or QueueEntryStatus.Running) && q.CustomRegionRouteId == Guid.Empty))
        {
            if (forceStart) EnsureWorker(true);
            return;
        }

        // Remove old history rows for this visitor before creating a fresh queue item.
        venue.Queue.RemoveAll(q => string.Equals(q.Visitor.ToString(), key.ToString(), StringComparison.OrdinalIgnoreCase) && q.Status is (QueueEntryStatus.Completed or QueueEntryStatus.Failed or QueueEntryStatus.Cancelled) && q.CustomRegionRouteId == Guid.Empty);

        if (VenueService.ContainsKey(venue.Session.Greeted, key) || VenueService.ContainsKey(venue.Session.Skipped, key)) return;

        var macro = greetings.PickMacro(key);
        if (macro is null) return;

        venue.Queue.Add(new QueueEntry
        {
            Visitor = key,
            GreetingProfileId = venue.ActiveGreetingProfileId,
            Category = macro.Category,
            StatusText = "Queued"
        });
        persistence.SaveNow();
        EnsureWorker(forceStart);
    }

    public void EnqueueCustomRegionMacro(VisitorKey key, Guid routeId, Guid macroId)
    {
        var venue = venues.ActiveVenueOrNull;
        if (venue is null) return;
        if (venue.Blacklist.Contains(key.ToString())) return;
        if (routeId == Guid.Empty || macroId == Guid.Empty) return;

        if (HasCustomRegionGreeting(venue, routeId, key))
            return;

        if (venue.Queue.Any(q => q.CustomRegionRouteId == routeId && string.Equals(q.Visitor.ToString(), key.ToString(), StringComparison.OrdinalIgnoreCase) && q.Status is (QueueEntryStatus.Waiting or QueueEntryStatus.Running)))
            return;

        var macro = greetings.PickMacroById(macroId);
        if (macro is null)
        {
            logs.Warning("Custom region macro missing", $"A custom region route for {venue.Name} points at a missing or disabled macro. Open Settings > Venues and select an enabled macro for the route.");
            return;
        }

        venue.Queue.Add(new QueueEntry
        {
            Visitor = key,
            GreetingProfileId = venue.ActiveGreetingProfileId,
            Category = macro.Category,
            MacroOverrideId = macro.Id,
            CustomRegionRouteId = routeId,
            StatusText = "Queued custom region macro",
        });
        persistence.SaveNow();
        EnsureWorker(false);
    }

    public void Cancel(VisitorKey key, string reason)
    {
        var venue = venues.ActiveVenueOrNull;
        if (venue is null) return;

        foreach (var entry in venue.Queue.Where(q => string.Equals(q.Visitor.ToString(), key.ToString(), StringComparison.OrdinalIgnoreCase) && q.Status is (QueueEntryStatus.Waiting or QueueEntryStatus.Running)))
        {
            entry.Status = QueueEntryStatus.Cancelled;
            entry.StatusText = reason;
        }
        persistence.SaveNow();
    }

    public void EnsureWorker(bool forceStart = false)
    {
        lock (sync)
        {
            workerForceMode |= forceStart;
            if (worker is { IsCompleted: false }) return;
            cts.Dispose();
            cts = new CancellationTokenSource();
            worker = Task.Run(() => ProcessAsync(cts.Token));
        }
    }

    private bool ShouldProcessQueue()
    {
        lock (sync) return workerForceMode || config.AutoGreetEnabled;
    }

    private void ClearForceModeIfAppropriate()
    {
        lock (sync)
        {
            if (!config.AutoGreetEnabled) workerForceMode = false;
        }
    }

    private void StopQueueAfterMacroSyntaxError()
    {
        lock (sync)
        {
            workerForceMode = false;
            config.AutoGreetEnabled = false;
        }
    }

    private async Task ProcessAsync(CancellationToken token)
    {
        var previousEmoteCommand = await emoteResume.CaptureAsync(token).ConfigureAwait(false);

        try
        {
            while (!token.IsCancellationRequested && ShouldProcessQueue())
            {
                if (config.AutoGreetEnabled) EnqueueEligibleUngreeted(false);

                var venue = venues.ActiveVenueOrNull;
                if (venue is null)
                {
                    ClearForceModeIfAppropriate();
                    return;
                }

                var entry = venue.Queue.FirstOrDefault(q => q.Status == QueueEntryStatus.Waiting);
                if (entry is null)
                {
                    ClearForceModeIfAppropriate();
                    return;
                }

                if (venue.Blacklist.Contains(entry.Visitor.ToString()))
                {
                    entry.Status = QueueEntryStatus.Cancelled;
                    entry.StatusText = "Blacklisted";
                    persistence.SaveNow();
                    continue;
                }

                if (entry.CustomRegionRouteId == Guid.Empty && (VenueService.ContainsKey(venue.Session.Greeted, entry.Visitor) || VenueService.ContainsKey(venue.Session.Skipped, entry.Visitor)))
                {
                    entry.Status = QueueEntryStatus.Cancelled;
                    entry.StatusText = "Visitor was already greeted or skipped";
                    persistence.SaveNow();
                    continue;
                }

                entry.Status = QueueEntryStatus.Running;
                entry.StatusText = "Starting";
                persistence.SaveNow();

                try
                {
                    await Task.Delay(TimeSpan.FromSeconds(config.GreetingStartDelaySeconds), token).ConfigureAwait(false);

                    // If the user manually marked/skipped/cancelled the visitor during the start delay,
                    // do not continue running the macro on a stale queue entry.
                    if (entry.Status != QueueEntryStatus.Running)
                        throw new OperationCanceledException("Queue entry was cancelled before greeting started.", token);

                    if (entry.CustomRegionRouteId == Guid.Empty && (VenueService.ContainsKey(venue.Session.Greeted, entry.Visitor) || VenueService.ContainsKey(venue.Session.Skipped, entry.Visitor)))
                        throw new OperationCanceledException("Visitor was already greeted or skipped before greeting started.", token);

                    var macro = entry.MacroOverrideId != Guid.Empty
                        ? greetings.PickMacroById(entry.MacroOverrideId)
                        : greetings.PickMacro(entry.Visitor);

                    if (macro is null)
                        throw new InvalidOperationException("No enabled macro for visitor.");

                    var hasTell = greetings.MacroHasTell(macro);
                    var markVisitorGreeted = entry.CustomRegionRouteId == Guid.Empty;
                    await macroEngine.ExecuteAsync(entry.Visitor, macro, () => detection.IsPlayerVisible(entry.Visitor), token, markVisitorGreeted).ConfigureAwait(false);

                    // RaptureShellModule sends native chat commands directly. Some Dalamud builds do not echo
                    // plugin-originated outgoing tells back through IChatGui.ChatMessage, so successful command
                    // execution is the confirmation signal here.
                    if (hasTell)
                    {
                        entry.StatusText = "Tell command sent";
                        persistence.SaveNow();
                    }

                    if (entry.CustomRegionRouteId != Guid.Empty)
                        MarkCustomRegionGreeting(venue, entry.CustomRegionRouteId, entry.Visitor);

                    entry.Status = QueueEntryStatus.Completed;
                    entry.StatusText = entry.CustomRegionRouteId == Guid.Empty
                        ? hasTell ? "Completed - tell sent" : "Completed"
                        : hasTell ? "Completed custom region macro - tell sent" : "Completed custom region macro";
                }
                catch (MacroSyntaxException ex)
                {
                    entry.Status = QueueEntryStatus.Failed;
                    entry.StatusText = "Macro syntax error - AutoGreet paused. Open the Log tab.";
                    StopQueueAfterMacroSyntaxError();
                    logs.Warning("AutoGreet paused", $"Queue processing stopped because {ex.Message} Fix the macro syntax shown in the Log tab, then turn AutoGreet back on.");
                    DalamudServices.Log.Error(ex, "AutoGreet queue stopped because a macro had unsupported syntax.");
                    return;
                }
                catch (OperationCanceledException)
                {
                    entry.Status = QueueEntryStatus.Cancelled;
                    entry.StatusText = "Cancelled";
                }
                catch (Exception ex)
                {
                    entry.Status = QueueEntryStatus.Failed;
                    entry.StatusText = ex.Message;
                    DalamudServices.Log.Error(ex, "AutoGreet queue failed.");
                }
                finally
                {
                    persistence.SaveNow();
                }

                await Task.Delay(TimeSpan.FromSeconds(config.QueueDelaySeconds), token).ConfigureAwait(false);
            }
        }
        finally
        {
            // Resume only after the current manual greet or auto-greet queue worker fully finishes.
            // Resuming inside MacroEngine can interrupt multi-line macros because native chat commands
            // are queued through the game shell asynchronously.
            await emoteResume.ResumeAsync(previousEmoteCommand, CancellationToken.None).ConfigureAwait(false);
        }
    }

    private static bool HasCustomRegionGreeting(VenueProfile venue, Guid routeId, VisitorKey key)
    {
        return venue.Session.CustomRegionGreetings.TryGetValue(routeId, out var greeted)
               && VenueService.ContainsKey(greeted, key);
    }

    private static void MarkCustomRegionGreeting(VenueProfile venue, Guid routeId, VisitorKey key)
    {
        if (!venue.Session.CustomRegionGreetings.TryGetValue(routeId, out var greeted))
        {
            greeted = [];
            venue.Session.CustomRegionGreetings[routeId] = greeted;
        }

        if (!VenueService.ContainsKey(greeted, key))
            greeted.Add(key);
    }

    public void Dispose()
    {
        cts.Cancel();
        cts.Dispose();
    }
}
