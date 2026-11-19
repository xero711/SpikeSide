namespace ValoAutoPick;

internal enum MatchHistorySaveOutcome
{
    /// <summary>The snapshot was written; only the entries it contained were removed from the pending set.</summary>
    Saved,
    /// <summary>The save ran out of its own time budget; the entries stay pending for the next save.</summary>
    TimedOut,
    /// <summary>The archive write failed; the entries stay pending for the next save.</summary>
    Failed,
    /// <summary>The session was reset (account switch) or the owner shut down; nothing should be reported.</summary>
    Superseded
}

internal sealed record MatchHistorySaveResult(MatchHistorySaveOutcome Outcome, Exception? Error = null);

/// <summary>
/// Persists fetched matches outside the network-fetch timeout. Saves never overlap: each one waits for
/// the previous archive write to actually stop (no write is abandoned in the background), and a save only
/// clears the exact entries it wrote, so matches queued while it was running stay pending.
/// <see cref="Reset"/> starts a new session (account switch): the in-flight write is cancelled before it
/// can update the last-account marker, and its completion never touches the new session's pending set.
/// </summary>
internal sealed class MatchHistorySaveQueue
{
    public delegate void SaveAction(string subject, IReadOnlyList<MatchAnalytics> matches, IReadOnlyDictionary<string, string> weapons, CancellationToken token);

    private readonly SaveAction save;
    private readonly SemaphoreSlim writer = new(1, 1);
    private readonly object gate = new();
    private readonly Dictionary<string, MatchAnalytics> pending = new(StringComparer.OrdinalIgnoreCase);
    private CancellationTokenSource session = new();
    private int epoch;

    public MatchHistorySaveQueue(MatchHistoryArchive archive) : this(archive.Save) { }

    public MatchHistorySaveQueue(SaveAction save) => this.save = save;

    public int PendingCount { get { lock (gate) return pending.Count; } }

    /// <summary>Queues a match for the next save; a newer analysis replaces an older pending one with the same id.</summary>
    public void Enqueue(MatchAnalytics match)
    {
        var id = match.Info.MatchId;
        if (id.Length == 0) return;
        lock (gate) pending[id] = match;
    }

    /// <summary>Drops the pending entries of the current session and cancels its in-flight save.</summary>
    public void Reset()
    {
        CancellationTokenSource previous;
        lock (gate)
        {
            epoch++;
            pending.Clear();
            previous = session;
            session = new CancellationTokenSource();
        }
        // Not disposed: an in-flight save may still be linked to its token.
        previous.Cancel();
    }

    public async Task<MatchHistorySaveResult> SaveAsync(string subject, IReadOnlyDictionary<string, string> weapons, TimeSpan timeout, CancellationToken shutdown)
    {
        int startEpoch;
        CancellationToken sessionToken;
        lock (gate)
        {
            startEpoch = epoch;
            sessionToken = session.Token;
        }
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(shutdown, sessionToken);
        try { await writer.WaitAsync(cancellation.Token).ConfigureAwait(false); }
        catch (OperationCanceledException) { return new(MatchHistorySaveOutcome.Superseded); }
        try
        {
            MatchAnalytics[] batch;
            lock (gate)
            {
                if (startEpoch != epoch) return new(MatchHistorySaveOutcome.Superseded);
                // Snapshot after the previous write finished so it includes everything queued meanwhile.
                batch = pending.Values.ToArray();
            }
            // The time budget only starts once this save owns the writer.
            cancellation.CancelAfter(timeout);
            try
            {
                // Awaited to completion (no WaitAsync): the archive observes the token between files, so the
                // writer is only released once no write from this save can still be running.
                await Task.Run(() => save(subject, batch, weapons, cancellation.Token), CancellationToken.None).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (cancellation.IsCancellationRequested)
            {
                return new(shutdown.IsCancellationRequested || sessionToken.IsCancellationRequested
                    ? MatchHistorySaveOutcome.Superseded
                    : MatchHistorySaveOutcome.TimedOut);
            }
            catch (Exception ex)
            {
                return new(sessionToken.IsCancellationRequested || shutdown.IsCancellationRequested
                    ? MatchHistorySaveOutcome.Superseded
                    : MatchHistorySaveOutcome.Failed, ex);
            }
            lock (gate)
            {
                if (startEpoch != epoch) return new(MatchHistorySaveOutcome.Superseded);
                foreach (var match in batch)
                {
                    var id = match.Info.MatchId;
                    // A newer analysis of the same match queued during the write is still unsaved.
                    if (pending.TryGetValue(id, out var current) && ReferenceEquals(current, match)) pending.Remove(id);
                }
            }
            return new(MatchHistorySaveOutcome.Saved);
        }
        finally
        {
            writer.Release();
        }
    }
}
