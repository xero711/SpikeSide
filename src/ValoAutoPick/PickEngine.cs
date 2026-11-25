using System.Diagnostics;

namespace ValoAutoPick;

// One serial worker owns this state. No overlapping polling or write requests.
public sealed class PickEngine(
    IGameApi api,
    Action<string> report,
    Func<string, CancellationToken, Task<bool?>>? selectionReady = null,
    Action<string>? mapChanged = null,
    Func<string, string?>? authorizePick = null,
    Func<string, string?>? checkMatchAllowed = null,
    Func<string, bool>? agentAllowed = null,
    Action? matchEnded = null,
    Action<PickDiagnosticEvent>? diagnostic = null)
{
    private const int FastReadyPollIntervalMs = 40;
    private const int PregamePollIntervalMs = 90;
    private const int IdlePollIntervalMs = 250;
    private const int InGamePollIntervalMs = 1000;
    private static readonly TimeSpan UnknownReadinessDelay = TimeSpan.FromSeconds(12);
    private static readonly TimeSpan ReadinessRemoteRefreshInterval = TimeSpan.FromMilliseconds(450);

    private sealed record PendingSelection(
        string MatchId,
        string MapKey,
        string AgentId,
        string SelfCharacterId,
        string SelfSelectionState,
        DateTimeOffset LastRemoteCheck);

    private readonly Dictionary<string, string?> completed = [];
    private readonly Queue<string> history = [];
    private string? current;
    private string lastMapId = "";
    private readonly MapLifecycle maps = new(api, mapChanged, matchEnded);
    private DateTimeOffset fallbackPickAt;
    private long detectedTimestamp;
    private long? readyTimestamp;
    private bool reportedTakenDiagnostic;
    private PendingSelection? awaitingReady;
    private int attempts;

    public bool IsInMatch => maps.HasMatch;
    public bool IsPregameActive => current != null && !maps.InCoreGame && !completed.ContainsKey(current);
    public bool IsAwaitingSelectionReady => awaitingReady != null;
    public int SuggestedPollIntervalMs =>
        awaitingReady != null ? FastReadyPollIntervalMs :
        IsPregameActive ? PregamePollIntervalMs :
        maps.InCoreGame ? InGamePollIntervalMs :
        IdlePollIntervalMs;

    private void Complete(string id, string? message = null)
    {
        awaitingReady = null;
        if (!completed.TryAdd(id, message)) return;
        history.Enqueue(id);
        if (history.Count > 100) completed.Remove(history.Dequeue());
    }

    private void EmitDiagnostic(string matchId, string outcome, long? selectMs = null, long? lockMs = null, string note = "")
    {
        if (diagnostic == null) return;
        var nowStamp = Stopwatch.GetTimestamp();
        var totalMs = detectedTimestamp > 0 ? (long)Stopwatch.GetElapsedTime(detectedTimestamp, nowStamp).TotalMilliseconds : 0;
        long? detectToReadyMs = detectedTimestamp > 0 && readyTimestamp is { } ready
            ? (long)Stopwatch.GetElapsedTime(detectedTimestamp, ready).TotalMilliseconds
            : null;
        diagnostic(new PickDiagnosticEvent(matchId, MapCatalog.Key(lastMapId), outcome, detectToReadyMs, selectMs, lockMs, totalMs, attempts, note));
    }

    private static string ResolveAgent(Settings settings, string mapKey) =>
        !Edition.IsTrial && settings.MapAgents.TryGetValue(mapKey, out var configured) ? configured : settings.AgentId;

    public async Task Tick(Settings settings, DateTimeOffset now, CancellationToken ct)
    {
        var pollStarted = Stopwatch.GetTimestamp();

        // While waiting solely for the local client to enter character select, check local readiness at
        // high frequency without hammering remote pregame endpoints (reduces 429 risk and latency).
        if (awaitingReady is { } pending && selectionReady != null && !completed.ContainsKey(pending.MatchId))
        {
            var expectedAgent = ResolveAgent(settings, pending.MapKey);
            if (string.Equals(expectedAgent, pending.AgentId, StringComparison.OrdinalIgnoreCase) &&
                now - pending.LastRemoteCheck < ReadinessRemoteRefreshInterval)
            {
                var ready = await selectionReady(pending.MatchId, ct);
                var checkedAt = now + Stopwatch.GetElapsedTime(pollStarted);
                if (ready == false || ready == null && checkedAt < fallbackPickAt)
                {
                    report("選択画面の読み込みを待っています");
                    return;
                }
                awaitingReady = null;
                readyTimestamp ??= Stopwatch.GetTimestamp();
                await ExecutePick(pending.MatchId, pending.AgentId, pending.SelfCharacterId, pending.SelfSelectionState, ct);
                return;
            }
            awaitingReady = null;
        }

        var id = await api.GetMatchId(ct);
        var observedAt = now + Stopwatch.GetElapsedTime(pollStarted);
        if (id == null)
        {
            awaitingReady = null;
            await maps.ObserveWithoutPregame(observedAt, ct);
            if (!maps.HasMatch) current = null;
            report(maps.InCoreGame ? "試合中 · マップを表示中" : maps.HasMatch ? "試合の状態を確認中" : "マッチを検索中");
            return;
        }
        if (completed.TryGetValue(id, out var completedMessage)) { awaitingReady = null; if (current == id && lastMapId.Length > 0) mapChanged?.Invoke(lastMapId); report(completedMessage ?? "この試合の処理は完了 · 次の試合も自動で検知します"); return; }
        if (checkMatchAllowed?.Invoke(id) is { } blocked) { Complete(id, blocked); report(blocked); return; }
        if (current != id)
        {
            current = id; lastMapId = ""; attempts = 0; fallbackPickAt = observedAt + UnknownReadinessDelay;
            detectedTimestamp = pollStarted; readyTimestamp = null; reportedTakenDiagnostic = false; awaitingReady = null;
            maps.StartPregame(id);
            report("マッチを検知 · 選択画面を確認中");
        }

        var match = await api.GetMatch(id, ct);
        if (match.Id != id) return;
        lastMapId = match.MapId;
        maps.SetPregameMap(match.MapId);
        if (match.State != "character_select_active") { awaitingReady = null; return; }
        var self = match.Allies.FirstOrDefault(p => p.Subject == api.Subject);
        if (self == null) { awaitingReady = null; report("参加情報を取得中"); return; }
        if (self.CharacterSelectionState == "locked")
        {
            EmitDiagnostic(id, "already-locked");
            Complete(id); report("選択確定済み · 次のマッチを検索中"); return;
        }
        var taken = match.Allies.Where(p => p.Subject != api.Subject && p.CharacterSelectionState is "selected" or "locked")
            .Select(p => p.CharacterID).ToHashSet(StringComparer.OrdinalIgnoreCase);
        var mapKey = MapCatalog.Key(match.MapId);
        if (!Edition.IsTrial && mapKey.Length == 0 && settings.MapAgents.Count > 0) { awaitingReady = null; report("マップ情報を取得中"); return; }
        var agent = ResolveAgent(settings, mapKey);
        if (string.IsNullOrWhiteSpace(agent)) { awaitingReady = null; report("エージェントを設定してください"); return; }
        if (agentAllowed != null && !agentAllowed(agent)) { awaitingReady = null; report("設定したエージェントは未解放です · 別のエージェントを選んでください"); return; }
        if (taken.Contains(agent))
        {
            awaitingReady = null;
            if (!reportedTakenDiagnostic) { reportedTakenDiagnostic = true; EmitDiagnostic(id, "taken"); }
            report("設定したエージェントは選択済みです · 手動で選択してください");
            return;
        }
        if (selectionReady != null)
        {
            var ready = await selectionReady(id, ct);
            observedAt = now + Stopwatch.GetElapsedTime(pollStarted);
            if (ready == false || ready == null && observedAt < fallbackPickAt)
            {
                awaitingReady = new PendingSelection(id, mapKey, agent, self.CharacterID, self.CharacterSelectionState, observedAt);
                report("選択画面の読み込みを待っています");
                return;
            }
        }
        awaitingReady = null;
        readyTimestamp ??= Stopwatch.GetTimestamp();
        await ExecutePick(id, agent, self.CharacterID, self.CharacterSelectionState, ct);
    }

    private async Task ExecutePick(string id, string agent, string selfCharacterId, string selfSelectionState, CancellationToken ct)
    {
        if (attempts >= 6)
        {
            EmitDiagnostic(id, "retries-exhausted");
            Complete(id);
            report("この試合の再試行を終了 · ゲーム内で選択してください");
            return;
        }
        ct.ThrowIfCancellationRequested();
        if (authorizePick?.Invoke(id) is { } denial) { Complete(id, denial); report(denial); return; }
        attempts++;
        long? selectMs = null;
        long? lockMs = null;
        try
        {
            if (!string.Equals(selfCharacterId, agent, StringComparison.OrdinalIgnoreCase) || selfSelectionState != "selected")
            {
                var selectStarted = Stopwatch.GetTimestamp();
                await api.Select(id, agent, ct);
                selectMs = (long)Stopwatch.GetElapsedTime(selectStarted).TotalMilliseconds;
            }
            ct.ThrowIfCancellationRequested();
            var lockStarted = Stopwatch.GetTimestamp();
            var directMatch = await api.LockAndConfirm(id, agent, ct);
            lockMs = (long)Stopwatch.GetElapsedTime(lockStarted).TotalMilliseconds;

            var directLocked = directMatch?.Allies.FirstOrDefault(p => p.Subject == api.Subject);
            if (directLocked?.CharacterSelectionState == "locked")
            {
                EmitDiagnostic(id, "locked-direct", selectMs, lockMs);
                Complete(id);
                report("自動ピック・確定を確認しました");
                return;
            }

            var confirmed = await api.GetMatch(id, ct);
            var locked = confirmed.Allies.FirstOrDefault(p => p.Subject == api.Subject);
            if (locked?.CharacterSelectionState != "locked")
            {
                report("確定結果を再確認します");
                return;
            }
            EmitDiagnostic(id, "locked-confirmed", selectMs, lockMs);
            Complete(id);
            report("自動ピック・確定を確認しました");
        }
        catch (ApiException e) when (e.Status is 400 or 409)
        {
            awaitingReady = null;
            report("選択状態を確認して再試行します");
        }
        catch (ApiException e) when (e.Status == 429)
        {
            // Rate limits are transient throttling rather than agent rejection; preserve the retry budget.
            attempts--;
            awaitingReady = null;
            EmitDiagnostic(id, "rate-limited", selectMs, lockMs, $"retry={e.RetrySeconds}s");
            throw;
        }
    }
}

// Share match lifecycle handling between auto-pick and queue-only monitoring.
// A missing pregame response is normal during the transition to core game.
public sealed class MapLifecycle(IGameApi api, Action<string>? mapChanged, Action? matchEnded = null)
{
    private static readonly TimeSpan CheckInterval = TimeSpan.FromMilliseconds(1500);
    private static readonly TimeSpan PregameExitGrace = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan LobbyConfirmationDelay = TimeSpan.FromSeconds(3);
    private string? pregameId, coreId;
    private string mapId = "";
    private DateTimeOffset nextCoreCheck, lobbySince;
    private bool coreMapPending;
    public bool HasMatch => pregameId != null || coreId != null;
    public bool InCoreGame { get; private set; }

    public void StartPregame(string id)
    {
        if (pregameId == id) return;
        pregameId = id; coreId = null; mapId = ""; InCoreGame = false;
        nextCoreCheck = DateTimeOffset.MinValue; lobbySince = DateTimeOffset.MinValue;
        coreMapPending = false; mapChanged?.Invoke("");
    }
    public void SetPregameMap(string map)
    {
        if (string.IsNullOrWhiteSpace(map)) return;
        mapId = map; mapChanged?.Invoke(map);
    }
    public async Task ObserveWithoutPregame(DateTimeOffset now, CancellationToken ct)
    {
        if (now < nextCoreCheck) return;
        var id = await api.GetCoreMatchId(ct);
        nextCoreCheck = now + CheckInterval;
        if (id != null)
        {
            if (!string.Equals(coreId, id, StringComparison.OrdinalIgnoreCase))
            {
                coreId = id;
                coreMapPending = string.IsNullOrWhiteSpace(mapId) || !string.Equals(pregameId, id, StringComparison.OrdinalIgnoreCase);
            }
            InCoreGame = true; lobbySince = DateTimeOffset.MinValue;
            if (coreMapPending)
            {
                var map = await api.GetCoreMapId(id, ct);
                if (!string.IsNullOrWhiteSpace(map)) { mapId = map; coreMapPending = false; mapChanged?.Invoke(map); }
            }
            else if (!string.IsNullOrWhiteSpace(mapId)) mapChanged?.Invoke(mapId);
            return;
        }
        if (!HasMatch) return;
        var state = await api.GetPartyState(ct);
        if (state is not ("DEFAULT" or "MATCHMAKING")) { lobbySince = DateTimeOffset.MinValue; return; }
        var localState = await api.GetLocalGameFlowState(ct);
        // A matching local client is authoritative during screen transitions, even if
        // both remote endpoints briefly return 404 or party state lags behind.
        if (localState is "TransitionToPregame" or "Pregame" or "TransitionToInGame" or "InGame")
        { lobbySince = DateTimeOffset.MinValue; return; }
        if (lobbySince == DateTimeOffset.MinValue) lobbySince = now;
        if (!InCoreGame && now - lobbySince < PregameExitGrace) return;
        if (InCoreGame && localState != "MainMenu" && now - lobbySince < LobbyConfirmationDelay) return;
        var ended = InCoreGame;
        pregameId = coreId = null; mapId = ""; InCoreGame = false; coreMapPending = false;
        lobbySince = DateTimeOffset.MinValue; mapChanged?.Invoke("");
        if (ended) matchEnded?.Invoke();
    }
}

// Observe matches without issuing select or lock requests when only auto-queue is enabled.
public sealed class MapObserver(IGameApi api, Action<string> mapChanged, Action? matchEnded = null)
{
    private readonly MapLifecycle maps = new(api, mapChanged, matchEnded);
    private string? current;
    public async Task Tick(CancellationToken ct, DateTimeOffset? now = null)
    {
        var id = await api.GetMatchId(ct);
        if (id == null)
        {
            await maps.ObserveWithoutPregame(now ?? DateTimeOffset.UtcNow, ct);
            if (!maps.HasMatch) current = null;
            return;
        }
        if (id != current) { current = id; maps.StartPregame(id); }
        var match = await api.GetMatch(id, ct);
        if (match.Id == id) maps.SetPregameMap(match.MapId);
    }
}
