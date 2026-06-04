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
    private CancellationTokenSource cts = new();
    private Task? worker;
    private bool workerForceMode;
    private readonly object sync = new();

    public QueueService(Configuration config, VenueService venues, PersistenceService persistence, GreetingService greetings, MacroEngine macroEngine, DetectionService detection, EmoteResumeService emoteResume)
    {
        this.config = config;
        this.venues = venues;
        this.persistence = persistence;
        this.greetings = greetings;
        this.macroEngine = macroEngine;
        this.detection = detection;
        this.emoteResume = emoteResume;
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
        if (venue.Queue.Any(q => string.Equals(q.Visitor.ToString(), key.ToString(), StringComparison.OrdinalIgnoreCase) && q.Status is QueueEntryStatus.Waiting or QueueEntryStatus.Running))
        {
            if (forceStart) EnsureWorker(true);
            return;
        }

        // Remove old history rows for this visitor before creating a fresh queue item.
        venue.Queue.RemoveAll(q => string.Equals(q.Visitor.ToString(), key.ToString(), StringComparison.OrdinalIgnoreCase) && q.Status is QueueEntryStatus.Completed or QueueEntryStatus.Failed or QueueEntryStatus.Cancelled);

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

    public void Cancel(VisitorKey key, string reason)
    {
        var venue = venues.ActiveVenueOrNull;
        if (venue is null) return;

        foreach (var entry in venue.Queue.Where(q => string.Equals(q.Visitor.ToString(), key.ToString(), StringComparison.OrdinalIgnoreCase) && q.Status is QueueEntryStatus.Waiting or QueueEntryStatus.Running))
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

            entry.Status = QueueEntryStatus.Running;
            entry.StatusText = "Starting";
            persistence.SaveNow();

            try
            {
                await Task.Delay(TimeSpan.FromSeconds(config.GreetingStartDelaySeconds), token).ConfigureAwait(false);
                var macro = greetings.PickMacro(entry.Visitor) ?? throw new InvalidOperationException("No enabled macro for visitor.");
                var hasTell = greetings.MacroHasTell(macro);
                await macroEngine.ExecuteAsync(entry.Visitor, macro, () => detection.IsPlayerVisible(entry.Visitor), token).ConfigureAwait(false);

                // RaptureShellModule sends native chat commands directly. Some Dalamud builds do not echo
                // plugin-originated outgoing tells back through IChatGui.ChatMessage, so successful command
                // execution is the confirmation signal here.
                if (hasTell)
                {
                    entry.StatusText = "Tell command sent";
                    persistence.SaveNow();
                }

                entry.Status = QueueEntryStatus.Completed;
                entry.StatusText = hasTell ? "Completed - tell sent" : "Completed";
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

    public void Dispose()
    {
        cts.Cancel();
        cts.Dispose();
    }
}
