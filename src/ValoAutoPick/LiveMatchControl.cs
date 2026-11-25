using System.Drawing.Drawing2D;
using System.Drawing.Imaging;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace ValoAutoPick;

internal sealed class LiveMatchControl : UserControl
{
    private readonly RiotApi? api;
    private IReadOnlyList<Agent> agents;
    private readonly CancellationTokenSource lifetime = new();
    private readonly LiveMatchState state = new();
    private readonly Label connection = NativePageStyle.Label("Riot 接続を確認中", NativePageStyle.Small, Theme.Muted);
    private readonly Label summary = NativePageStyle.Label("試合データを待っています", NativePageStyle.Subheading);
    private readonly Label details = NativePageStyle.Label("プレイヤーを選択すると、試合中の情報を表示します。", NativePageStyle.Reading, Theme.Muted);
    private readonly Label emptyRoster = NativePageStyle.Label("マッチ待機中\n\nピック画面または試合が始まると、\nプレイヤーを表示します。", NativePageStyle.Reading, Theme.Muted);
    private readonly LiveRosterView roster = new() { Dock = DockStyle.Fill };
    private readonly System.Windows.Forms.Timer renderTimer = new() { Interval = 200 };
    private readonly System.Windows.Forms.Timer pollTimer = new() { Interval = 5000 };
    private bool dirty, connected;
    private bool polling;
    private int sessionGeneration;
    private DateTimeOffset nextRetry;
    // When the roster on screen was last confirmed by a successful refresh; null when nothing is on screen.
    private DateTimeOffset? lastSuccess;
    private string? selectedSlot;
    private readonly Dictionary<string, (string GameName, string TagLine)> playerNames = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, LiveRecentRank> playerRanks = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, DateTimeOffset> rankRetries = new(StringComparer.OrdinalIgnoreCase);
    private string nameMatchId = "";
    private int nameGeneration;
    private bool resolvingNames;
    private bool resolvingRanks;
    private DateTimeOffset nextNameRetry;
    private DateTimeOffset nextRankRetry;
    private OverwolfEventBridge? eventBridge;
    private readonly Dictionary<string, LiveScore> liveScores = new(StringComparer.OrdinalIgnoreCase);
    private DateTimeOffset? lastScoreUpdate;
    // When the Overwolf companion last delivered anything, matched or not; separates "not running" from "not matched".
    private DateTimeOffset? lastBridgeMessage;

    public LiveMatchControl() : this(null, []) { }
    public LiveMatchControl(RiotApi? api, IReadOnlyList<Agent> agents)
    {
        this.api = api; this.agents = agents;
        Dock = DockStyle.Fill; BackColor = Theme.Background;
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = Theme.Background, Padding = new Padding(0, UiMetrics.Px(this, 10), 0, 0) };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, UiMetrics.Px(this, 58)));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, UiMetrics.Px(this, 64)));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var header = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty, Padding = new Padding(0, 0, 0, UiMetrics.Px(this, 8)) };
        var title = NativePageStyle.Label("ライブトラッカー", NativePageStyle.Heading); title.Dock = DockStyle.Left; title.Width = UiMetrics.Px(this, 260);
        var refresh = new ThemedButton { Name = "liveRefresh", Text = "更新", Compact = true, Dock = DockStyle.Right, Width = UiMetrics.Px(this, 100) };
        refresh.Click += async (_, _) => await RefreshLiveAsync();
        connection.Dock = DockStyle.Fill; header.Controls.Add(connection); header.Controls.Add(refresh); header.Controls.Add(title);
        summary.Dock = DockStyle.Fill;
        summary.Padding = new Padding(UiMetrics.Px(this, 12), 0, UiMetrics.Px(this, 12), 0);
        var summaryCard = NativePageStyle.Card();
        summaryCard.Margin = new Padding(0, 0, 0, UiMetrics.Px(this, 10));
        summaryCard.Padding = Padding.Empty;
        summaryCard.Controls.Add(summary);
        var content = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Theme.Background, Margin = Padding.Empty };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 65)); content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35));
        var listCard = NativePageStyle.Card(); listCard.Margin = new Padding(0, 0, UiMetrics.Px(this, 10), 0);
        var detailCard = NativePageStyle.Card(); detailCard.Margin = Padding.Empty;
        roster.AccessibleName = "ピック中・試合中のプレイヤー";
        // Only a user selection (mouse, keyboard, accessibility) raises this; re-renders pass the slot back in without firing it.
        roster.SelectionChanged += (_, _) => { selectedSlot = roster.SelectedSlot; RenderDetails(); };
        listCard.Controls.Add(roster);
        emptyRoster.Dock = DockStyle.Fill; emptyRoster.TextAlign = ContentAlignment.MiddleCenter; emptyRoster.AutoEllipsis = false;
        emptyRoster.AccessibleName = "ライブトラッカーの待機状態";
        listCard.Controls.Add(emptyRoster);
        var detailTitle = NativePageStyle.Label("プレイヤー詳細", NativePageStyle.Subheading); detailTitle.Dock = DockStyle.Top; detailTitle.Height = UiMetrics.Px(this, 48);
        details.Dock = DockStyle.Fill; details.TextAlign = ContentAlignment.TopLeft; details.AutoEllipsis = false;
        detailCard.Controls.Add(details); detailCard.Controls.Add(detailTitle);
        content.Controls.Add(listCard, 0, 0); content.Controls.Add(detailCard, 1, 0);
        root.Controls.Add(header, 0, 0); root.Controls.Add(summaryCard, 0, 1); root.Controls.Add(content, 0, 2); Controls.Add(root);
        renderTimer.Tick += (_, _) => { if (ExpireStaleSnapshot() || ExpireScores()) dirty = true; if (dirty) { dirty = false; RenderState(); } };
        renderTimer.Start();
        pollTimer.Tick += async (_, _) => await RefreshLiveAsync();
        // A roster that aged out while the tab was hidden must be gone before the tab is painted, not after the request returns.
        VisibleChanged += async (_, _) => { if (!Visible) return; if (ExpireStaleSnapshot()) { dirty = false; RenderState(); } if (api is not null) await RefreshLiveAsync(); };
        pollTimer.Start();
    }

    public void UpdateAgents(IReadOnlyList<Agent> updated)
    {
        agents = updated;
        if (state.Players.Count > 0) RenderState();
    }

    public void ResetSession()
    {
        sessionGeneration++;
        state.Reset();
        ResetNames();
        selectedSlot = null;
        lastSuccess = null;
        connected = false;
        nextRetry = DateTimeOffset.MinValue;
        connection.Text = "Riot にログインしてください";
        dirty = false;
        if (!IsDisposed && !Disposing) RenderState();
    }

    public Task NotifyAccountChangedAsync()
    {
        if (IsDisposed || Disposing) return Task.CompletedTask;
        if (api is null || !api.HasAccountSession)
        {
            ResetSession();
            return Task.CompletedTask;
        }
        sessionGeneration++;
        state.Reset();
        ResetNames();
        selectedSlot = null;
        lastSuccess = null;
        nextRetry = DateTimeOffset.MinValue;
        dirty = true;
        return Visible ? RefreshLiveAsync() : Task.CompletedTask;
    }

    internal Task RefreshLiveAsync(bool requireVisible = true)
    {
        if (api is null || IsDisposed || Disposing) return Task.CompletedTask;
        if (!api.HasAccountSession)
        {
            ResetSession();
            return Task.CompletedTask;
        }
        if (eventBridge is null)
        {
            try { eventBridge = new OverwolfEventBridge(Storage.Folder, ReceiveGameEvents); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException or System.Net.Sockets.SocketException) { }
        }
        if ((requireVisible && !Visible) || polling || DateTimeOffset.UtcNow < nextRetry) return Task.CompletedTask;
        return RefreshLiveCoreAsync();
    }

    private async Task RefreshLiveCoreAsync()
    {
        if (api is null) return;
        polling = true;
        var generation = sessionGeneration;
        var subject = api.Subject;
        try
        {
            var snapshot = await LiveTrackerReader.ReadAsync(api, subject, lifetime.Token);
            var match = snapshot.Game;
            var pregame = snapshot.Pregame;
            if (IsDisposed || Disposing) return;
            if (generation != sessionGeneration || !api.HasAccountSession || !string.Equals(api.Subject, subject, StringComparison.OrdinalIgnoreCase))
            {
                if (!api.HasAccountSession) ResetSession();
                return;
            }
            connected = true;
            if (match is null && pregame is null)
            {
                state.Reset();
                ResetNames();
                selectedSlot = null;
                connection.Text = "ピック画面・試合を待機中";
            }
            else
            {
                (int Allies, int Enemies)? score = null;
                if (match is not null)
                {
                    try { score = await api.GetLocalLiveScore(match.MapId, lifetime.Token); }
                    catch (Exception e) when (e is IOException or HttpRequestException or JsonException or InvalidOperationException or ClientUnavailableException) { }
                }
                if (IsDisposed || Disposing) return;
                if (generation != sessionGeneration || !api.HasAccountSession || !string.Equals(api.Subject, subject, StringComparison.OrdinalIgnoreCase))
                {
                    if (!api.HasAccountSession) ResetSession();
                    return;
                }
                var previousMatchId = state.Values.TryGetValue("match_id", out var prevId) ? prevId : "";
                var selectedPlayerId = selectedSlot is not null && state.Players.TryGetValue(selectedSlot, out var selectedPlayer)
                    ? LiveMatchState.Text(selectedPlayer, "player_id")
                    : "";
                var currentMatchId = match?.MatchId ?? pregame!.Id;
                if (!string.Equals(nameMatchId, currentMatchId, StringComparison.OrdinalIgnoreCase))
                {
                    ResetNames();
                    nameMatchId = currentMatchId;
                }
                if (match is not null) state.ReplaceRoster(match, subject, api.AccountName, agents);
                else state.ReplacePregame(pregame!, subject, api.AccountName, agents);
                state.ApplyNames(playerNames);
                state.ApplyRanks(playerRanks);
                if (match is not null) state.ApplyScores(liveScores);
                if (!string.Equals(previousMatchId, currentMatchId, StringComparison.OrdinalIgnoreCase))
                {
                    selectedSlot = null;
                }
                else if (selectedPlayerId.Length > 0)
                {
                    selectedSlot = state.Players.FirstOrDefault(pair =>
                        string.Equals(LiveMatchState.Text(pair.Value, "player_id"), selectedPlayerId, StringComparison.OrdinalIgnoreCase)).Key;
                }
                if (score is { } s) state.SetScore(s.Allies, s.Enemies);
                connection.Text = match is not null
                    ? "試合情報 · 約5秒ごとに更新"
                    : "味方のピック · 約5秒ごとに更新";
                StartNameLookup(subject);
                StartRankLookup(subject);
            }
            lastSuccess = DateTimeOffset.UtcNow;
            dirty = true;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (ApiException e) when (e.Status == 429)
        {
            nextRetry = DateTimeOffset.UtcNow.AddSeconds(e.RetrySeconds);
            if (!IsDisposed && !Disposing && generation == sessionGeneration) KeepOrDropSnapshot("通信制限のため、しばらく待って再試行します");
        }
        catch (Exception e) when (e is HttpRequestException or IOException or InvalidOperationException or JsonException or TaskCanceledException or ClientUnavailableException or ApiException)
        {
            if (!IsDisposed && !Disposing && generation == sessionGeneration) KeepOrDropSnapshot("試合情報を取得できませんでした。再試行します");
        }
        finally { polling = false; }
    }

    private void ResetNames()
    {
        nameGeneration++;
        nameMatchId = "";
        playerNames.Clear();
        playerRanks.Clear();
        rankRetries.Clear();
        liveScores.Clear();
        lastScoreUpdate = null;
        lastBridgeMessage = null;
        nextNameRetry = DateTimeOffset.MinValue;
        nextRankRetry = DateTimeOffset.MinValue;
    }

    private void ReceiveGameEvents(string message)
    {
        if (IsDisposed || Disposing || !IsHandleCreated) return;
        try
        {
            BeginInvoke(() =>
            {
                if (IsDisposed || Disposing || !state.Values.TryGetValue("stage", out var stage) || stage != "game" ||
                    !state.Values.TryGetValue("match_id", out var matchId)) return;
                lastBridgeMessage = DateTimeOffset.UtcNow;
                var self = api?.Subject ?? "";
                var targets = state.Players.Values.Select(player => LiveMatchState.Text(player, "player_id")).Select(id =>
                    new LiveScoreTarget(id, id.Equals(self, StringComparison.OrdinalIgnoreCase) ? api?.AccountName ?? "" :
                        playerNames.TryGetValue(id, out var name) && !string.IsNullOrWhiteSpace(name.GameName) && !string.IsNullOrWhiteSpace(name.TagLine)
                            ? $"{name.GameName}#{name.TagLine}" : ""));
                var scores = LiveScoreboard.Parse(message, matchId, targets, self);
                if (scores.Count == 0) { dirty = true; return; }
                foreach (var pair in scores) liveScores[pair.Key] = pair.Value;
                lastScoreUpdate = DateTimeOffset.UtcNow;
                state.ApplyScores(liveScores);
                dirty = true;
            });
        }
        catch (InvalidOperationException) { }
    }

    private bool ExpireScores()
    {
        if (lastScoreUpdate is not { } updated || DateTimeOffset.UtcNow - updated < TimeSpan.FromSeconds(15)) return false;
        liveScores.Clear(); lastScoreUpdate = null; state.ClearScores();
        return true;
    }

    // Overwolf status for the summary line while a match is running.
    private string LiveDataStatus()
    {
        if (lastScoreUpdate is not null) return "K/D/A LIVE";
        if (lastBridgeMessage is { } at && DateTimeOffset.UtcNow - at < TimeSpan.FromSeconds(15)) return "K/D/A 照合中";
        return "K/D/A: Overwolf 未接続";
    }

    private void StartNameLookup(string subject)
    {
        if (api is null || resolvingNames || DateTimeOffset.UtcNow < nextNameRetry) return;
        var missing = state.Players.Values.Select(player => LiveMatchState.Text(player, "player_id"))
            .Where(id => Guid.TryParse(id, out _) && !id.Equals(subject, StringComparison.OrdinalIgnoreCase) && !playerNames.ContainsKey(id))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (missing.Length == 0) return;
        _ = ResolveNamesAsync(missing, subject, nameGeneration);
    }

    private async Task ResolveNamesAsync(string[] ids, string subject, int generation)
    {
        if (api is null) return;
        resolvingNames = true;
        try
        {
            var names = await api.GetPlayerNames(ids, lifetime.Token);
            if (IsDisposed || Disposing || generation != nameGeneration || !api.HasAccountSession ||
                !string.Equals(subject, api.Subject, StringComparison.OrdinalIgnoreCase)) return;
            foreach (var id in ids)
                if (names.TryGetValue(id, out var name) && !string.IsNullOrWhiteSpace(name.GameName)) playerNames[id] = name;
            state.ApplyNames(playerNames);
            dirty = true;
            nextNameRetry = DateTimeOffset.UtcNow.AddSeconds(30);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception e) when (e is ApiException or HttpRequestException or IOException or InvalidOperationException or JsonException or TaskCanceledException)
        {
            if (generation == nameGeneration)
                nextNameRetry = DateTimeOffset.UtcNow.AddSeconds(e is ApiException { Status: 429 } limited ? limited.RetrySeconds : 15);
        }
        finally { resolvingNames = false; }
    }

    private void StartRankLookup(string subject)
    {
        if (api is null || resolvingRanks || DateTimeOffset.UtcNow < nextRankRetry) return;
        var now = DateTimeOffset.UtcNow;
        var ids = state.Players.Values.Select(player => LiveMatchState.Text(player, "player_id"))
            .Where(id => Guid.TryParse(id, out _) && !playerRanks.ContainsKey(id) &&
                (!rankRetries.TryGetValue(id, out var retry) || now >= retry))
            .Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (ids.Length > 0) _ = ResolveRanksAsync(ids, subject, nameGeneration);
    }

    private async Task ResolveRanksAsync(string[] ids, string subject, int generation)
    {
        if (api is null) return;
        resolvingRanks = true;
        try
        {
            foreach (var id in ids)
            {
                if (generation != nameGeneration || IsDisposed || Disposing || !api.HasAccountSession ||
                    !string.Equals(subject, api.Subject, StringComparison.OrdinalIgnoreCase)) return;
                try
                {
                    var rank = await api.GetPlayerRecentRank(id, lifetime.Token);
                    if (generation != nameGeneration || IsDisposed || Disposing || !api.HasAccountSession ||
                        !string.Equals(subject, api.Subject, StringComparison.OrdinalIgnoreCase)) return;
                    if (rank is not null) { playerRanks[id] = rank; state.ApplyRanks(playerRanks); dirty = true; }
                    else rankRetries[id] = DateTimeOffset.UtcNow.AddMinutes(1);
                }
                catch (ApiException e) when (e.Status == 429)
                {
                    if (generation == nameGeneration) nextRankRetry = DateTimeOffset.UtcNow.AddSeconds(e.RetrySeconds);
                    return;
                }
                catch (ApiException e) when (e.Status is 403 or 404)
                {
                    if (generation == nameGeneration) rankRetries[id] = DateTimeOffset.UtcNow.AddMinutes(1);
                }
                await Task.Delay(200, lifetime.Token);
            }
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception e) when (e is ApiException or HttpRequestException or IOException or InvalidOperationException or JsonException or TaskCanceledException)
        {
            if (generation == nameGeneration) nextRankRetry = DateTimeOffset.UtcNow.AddSeconds(15);
        }
        finally { resolvingRanks = false; }
    }

    // After a failed refresh: keep the last confirmed roster only while LiveMatchState.IsExpired says it is still
    // trustworthy, and say when it was confirmed. Anything older is dropped together with the selection.
    private void KeepOrDropSnapshot(string reason)
    {
        connected = false;
        if (lastSuccess is { } at && state.Players.Count > 0 && !LiveMatchState.IsExpired(DateTimeOffset.UtcNow - at))
            connection.Text = $"{reason}（{at.ToLocalTime():HH:mm:ss} 時点の情報を表示中）";
        else
        {
            state.Reset(); ResetNames(); selectedSlot = null; lastSuccess = null;
            connection.Text = reason;
        }
        dirty = true;
    }

    // Runs on every render tick and when the tab is shown, so a retained roster also ages out during a 429 wait
    // or while the tab was hidden (no poll runs in either case). Returns true when it cleared the screen.
    private bool ExpireStaleSnapshot()
    {
        if (lastSuccess is not { } at || state.Players.Count == 0 || !LiveMatchState.IsExpired(DateTimeOffset.UtcNow - at)) return false;
        state.Reset(); ResetNames(); selectedSlot = null; lastSuccess = null; connected = false;
        connection.Text = "古い試合情報を消しました。再取得します";
        return true;
    }

    private void RenderState()
    {
        string Field(string key) => state.Values.TryGetValue(key, out var value) && value.Length > 0 ? value : "—";
        var score = Field("score");
        try { if (JsonNode.Parse(score) is JsonObject s) score = $"{LiveMatchState.Text(s, "won")} : {LiveMatchState.Text(s, "lost")}"; } catch (JsonException) { }
        var map = Field("map");
        // While a failed refresh is holding the last roster, say so and hide the score: the score is the value that goes stale fastest.
        var holding = !connected && state.Players.Count > 0;
        var stateLabel = state.MatchEnded ? "試合終了" : holding ? "再接続待ち" : !connected ? "未接続" : state.Players.Count == 0 && map == "—" ? "試合待機中" : Field("stage") == "pregame" ? "ピック中" : "LIVE";
        var phase = Field("round_phase") switch { "shopping" => "購入フェーズ", "combat" => "戦闘中", "end" => "ラウンド終了", "game_end" => "試合終了", "—" => "", var other => other };
        var mode = ModeName(Field("game_mode"));
        var parts = new List<string> { stateLabel };
        if (map != "—") parts.Add(MapCatalog.Name(map));
        if (!string.IsNullOrEmpty(mode) && !Guid.TryParse(mode, out _)) parts.Add(mode);
        if (score != "—" && !holding) parts.Add(score);
        if (Field("round_number") is { } round && round != "—") parts.Add("ラウンド " + round);
        if (phase.Length > 0) parts.Add(phase);
        if (Field("stage") == "game") parts.Add(LiveDataStatus());
        summary.Text = string.Join("    ", parts);
        emptyRoster.Text = !connected && state.Players.Count == 0
            ? "接続を確認中\n\nRiot に接続すると、試合情報を表示します。"
            : "マッチ待機中\n\nピック画面または試合が始まると、\nプレイヤーを表示します。";
        emptyRoster.Visible = state.Players.Count == 0;
        roster.Visible = state.Players.Count > 0;
        if (emptyRoster.Visible) emptyRoster.BringToFront();
        var rows = BuildRosterRows(out var arrangement);
        roster.SetRows(rows, arrangement, selectedSlot);
        // A slot that is no longer in the roster cannot stay selected, or the details would describe a player who left.
        selectedSlot = roster.SelectedSlot;
        RenderDetails();
    }

    // One row per player actually present in the state: nothing is invented to fill a side, and players whose team
    // is unknown are kept in their own group instead of being dropped.
    private List<LiveRosterRow> BuildRosterRows(out LiveRosterLayout arrangement)
    {
        var stage = state.Values.TryGetValue("stage", out var s) ? s : "";
        var isPregame = stage == "pregame";
        var rows = new List<LiveRosterRow>();
        foreach (var pair in state.Players.OrderBy(p => int.TryParse(p.Key, out var n) ? n : int.MaxValue).ThenBy(p => p.Key, StringComparer.Ordinal))
        {
            var p = pair.Value;
            string V(string key) { var text = LiveMatchState.Text(p, key); return text.Length == 0 ? "—" : text; }
            var side = V("teammate") switch { "true" => LiveRosterSide.Ally, "false" => LiveRosterSide.Enemy, _ => LiveRosterSide.Unknown };
            var agentName = AgentDisplayName(p);
            var kda = V("kills") == "—" || V("deaths") == "—" || V("assists") == "—" ? "—" : $"{V("kills")} / {V("deaths")} / {V("assists")}";
            int? tier = int.TryParse(LiveMatchState.Text(p, "rank_tier"), out var t) ? t : null;
            var status = isPregame ? V("selection") : LiveStatus(p) ?? side switch { LiveRosterSide.Ally => "味方", LiveRosterSide.Enemy => "相手", _ => "不明" };
            rows.Add(new LiveRosterRow(pair.Key, side, V("name"), agentName, AgentId(p, agentName), tier, V("rank"), kda, status,
                LiveMatchState.Text(p, "is_local") == "true", isPregame && V("selection") != "確定"));
        }
        var allies = rows.Count(r => r.Side == LiveRosterSide.Ally);
        var enemies = rows.Count(r => r.Side == LiveRosterSide.Enemy);
        var freeForAll = !isPregame && (ModeName(state.Values.TryGetValue("game_mode", out var mode) ? mode : "") == "デスマッチ" ||
            stage == "game" && Math.Max(allies, enemies) > 5 && Math.Min(allies, enemies) <= 1);
        arrangement = isPregame ? LiveRosterLayout.Pregame : freeForAll ? LiveRosterLayout.FreeForAll : LiveRosterLayout.Teams;
        if (freeForAll) rows = rows.Select(r => r.Status is "味方" or "相手" or "不明" ? r with { Status = r.IsLocal ? "自分" : "相手" } : r).ToList();
        return rows;
    }

    private string AgentDisplayName(System.Text.Json.Nodes.JsonObject player)
    {
        var id = LiveMatchState.Text(player, "agent_id");
        if (Guid.TryParse(id, out _) && agents.FirstOrDefault(a => a.Id.Equals(id, StringComparison.OrdinalIgnoreCase)) is { } agent) return agent.Name;
        var character = LiveMatchState.Text(player, "character");
        return character.Length == 0 ? "—" : AgentName(character);
    }

    private string AgentId(System.Text.Json.Nodes.JsonObject player, string agentName)
    {
        var id = LiveMatchState.Text(player, "agent_id");
        if (id.Length > 0) return id;
        // Event-style data names the agent instead of giving its ID; map it back so the embedded icon can still be shown.
        return agents.FirstOrDefault(a => a.Name.Equals(agentName, StringComparison.OrdinalIgnoreCase))?.Id ?? "";
    }

    private void RenderDetails()
    {
        if (selectedSlot is null || !state.Players.TryGetValue(selectedSlot, out var p)) { details.Text = "プレイヤーを選択すると、試合中の情報を表示します。"; return; }
        string V(string key) { var s = LiveMatchState.Text(p, key); return s.Length == 0 ? "—" : s; }
        var level = V("account_level");
        var isPregame = state.Values.TryGetValue("stage", out var stage) && stage == "pregame";
        var rank = V("rank") == "—" ? "取得中 / データなし" : V("rank") + (V("rank_rr") == "—" ? "" : $" · {V("rank_rr")} RR");
        var team = V("teammate") switch { "true" => "味方", "false" => "相手", _ => "不明" };
        details.Text = $"{V("name")}\n\nエージェント   {AgentDisplayName(p)}\n\n直近コンペ後   {rank}" +
            (isPregame ? $"\n\n選択状況   {V("selection")}" : $"\n\nチーム   {team}" + (level == "—" ? "" : $"\n\nアカウントレベル   {level}")) +
            (isPregame ? "\n\nピック中は味方を表示します。" : V("kills") == "—" ? "\n\nK/D/A: Overwolf イベントを待機中" : LiveDetails(p));
    }

    // Shown in the status column during a match when Overwolf sends it: a downed player first, then a ready ultimate.
    private static string? LiveStatus(System.Text.Json.Nodes.JsonObject p)
    {
        if (LiveMatchState.Text(p, "alive") == "false") return "ダウン";
        if (int.TryParse(LiveMatchState.Text(p, "ult_points"), out var points) && int.TryParse(LiveMatchState.Text(p, "ult_max"), out var max) && max > 0 && points >= max)
            return "ULT 準備完了";
        return null;
    }

    private static string LiveDetails(System.Text.Json.Nodes.JsonObject p)
    {
        string T(string key) => LiveMatchState.Text(p, key);
        var lines = new List<string> { $"K / D / A   {T("kills")} / {T("deaths")} / {T("assists")}" };
        if (int.TryParse(T("kills"), out var k) && int.TryParse(T("deaths"), out var d))
            lines.Add($"K/D   {(d == 0 ? k : k / (double)d):0.00}");
        if (int.TryParse(T("headshots"), out var hs)) lines.Add($"ヘッドショットキル   {hs}");
        if (int.TryParse(T("money"), out var money)) lines.Add($"クレジット   {money:N0}");
        if (T("ult_points").Length > 0 && T("ult_max").Length > 0) lines.Add($"アルティメット   {T("ult_points")} / {T("ult_max")}");
        if (T("alive") is "true" or "false") lines.Add($"状態   {(T("alive") == "true" ? "生存" : "ダウン")}");
        var shield = T("shield") switch { "1" => "ライト (25)", "2" => "ヘビー (50)", "4" => "リジェネ (25)", "0" => "なし", _ => "" };
        if (shield.Length > 0) lines.Add($"シールド   {shield}");
        if (T("weapon").Length > 0) lines.Add($"武器   {WeaponName(T("weapon"))}");
        if (T("spike") == "true") lines.Add("スパイク所持");
        return "\n\n" + string.Join("\n\n", lines);
    }

    internal static string ModeName(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value == "—") return "";
        try
        {
            if (value.TrimStart().StartsWith('{') && JsonNode.Parse(value) is JsonObject mode)
            {
                var id = LiveMatchState.Text(mode, "mode");
                if (id == "bomb" && LiveMatchState.Text(mode, "ranked") == "1") return "コンペティティブ";
                return id switch { "bomb" => "アンレート", "quickbomb" or "quick bomb" => "スパイクラッシュ", "deathmatch" => "デスマッチ", "team_deathmatch" => "チームデスマッチ", "swift" => "スイフトプレイ", "escalation" => "エスカレーション", _ => id };
            }
        }
        catch (JsonException) { }
        if (value.Contains("Swiftplay", StringComparison.OrdinalIgnoreCase) || value.Equals("swiftplay", StringComparison.OrdinalIgnoreCase) || value.Equals("swift", StringComparison.OrdinalIgnoreCase)) return "スイフトプレイ";
        if (value.Contains("QuickBomb", StringComparison.OrdinalIgnoreCase) || value.Equals("spikerush", StringComparison.OrdinalIgnoreCase)) return "スパイクラッシュ";
        if (value.Contains("HURM", StringComparison.OrdinalIgnoreCase) || value.Contains("TeamDeathmatch", StringComparison.OrdinalIgnoreCase) || value.Equals("team_deathmatch", StringComparison.OrdinalIgnoreCase)) return "チームデスマッチ";
        if (value.Contains("Deathmatch", StringComparison.OrdinalIgnoreCase)) return "デスマッチ";
        if (value.Contains("GunGame", StringComparison.OrdinalIgnoreCase) || value.Contains("Escalation", StringComparison.OrdinalIgnoreCase) || value.Equals("ggteam", StringComparison.OrdinalIgnoreCase)) return "エスカレーション";
        if (value.Contains("OneForAll", StringComparison.OrdinalIgnoreCase) || value.Contains("Replication", StringComparison.OrdinalIgnoreCase) || value.Equals("onefa", StringComparison.OrdinalIgnoreCase)) return "レプリケーション";
        if (value.Contains("Snowball", StringComparison.OrdinalIgnoreCase)) return "スノーボールファイト";
        if (value.Contains("ShootingRange", StringComparison.OrdinalIgnoreCase)) return "射撃場";
        if (value.Equals("competitive", StringComparison.OrdinalIgnoreCase)) return "コンペティティブ";
        if (value.Equals("unrated", StringComparison.OrdinalIgnoreCase)) return "アンレート";
        if (value.Equals("premier", StringComparison.OrdinalIgnoreCase)) return "プレミア";
        // BombGameMode_C is shared across competitive, unrated, premier and custom standard matches;
        // do not guess which queue it is when only the asset path is available.
        if (value.Contains("BombGameMode", StringComparison.OrdinalIgnoreCase) || value.StartsWith("/Game/", StringComparison.OrdinalIgnoreCase)) return "";
        return value;
    }

    private static string WeaponName(string value) => value switch
    {
        "TX_Hud_Pistol_Classic" => "クラシック", "TX_Hud_Pistol_Slim" => "ショーティー", "TX_Hud_Pistol_AutoPistol" => "フレンジー",
        "TX_Hud_Pistol_Luger" => "ゴースト", "TX_Hud_Pistol_Sheriff" => "シェリフ", "TX_Hud_SMGs_Vector" => "スティンガー",
        "TX_Hud_SMGs_Ninja" => "スペクター", "TX_Hud_Rifles_Burst" => "ブルドッグ", "TX_Hud_Rifles_DMR" => "ガーディアン",
        "TX_Hud_Rifles_Ghost" => "ファントム", "TX_Hud_Rifles_Volcano" => "ヴァンダル", "TX_Hud_Sniper_Bolt" => "マーシャル",
        "TX_Hud_Sniper_Operater" => "オペレーター", "TX_Hud_Sniper_DoubleSniper" => "アウトロー", "TX_Hud_LMG" => "アレス", "TX_Hud_HMG" => "オーディン",
        _ => value
    };

    private static string AgentName(string name) => name.Replace("_PC_C", "", StringComparison.OrdinalIgnoreCase) switch
    {
        "Clay" => "レイズ", "Pandemic" => "ヴァイパー", "Wraith" => "オーメン", "Hunter" => "ソーヴァ",
        "Thorne" => "セージ", "Wushu" => "ジェット", "Gumshoe" => "サイファー", "Sarge" => "ブリムストーン",
        "Vampire" => "レイナ", "Guide" => "スカイ", "Stealth" => "ヨル", "Rift" => "アストラ", "Grenadier" => "KAY/O",
        "Deadeye" => "チェンバー", "Sprinter" => "ネオン", "BountyHunter" => "フェイド", "Mage" => "ハーバー",
        "AggroBot" => "ゲッコー", "Cable" => "デッドロック", "Sequoia" => "アイソ", "Smonk" => "クローヴ",
        "Nox" => "ヴァイス", "Cashew" => "テホ", "Terra" => "ウェイレイ", _ => name
    };

    internal void ShowPreview()
    {
        state.Apply("""{"info":{"match_info":{"map":"Ascent","round_number":"8","score":"{\"won\":4,\"lost\":3}","scoreboard_0":"{\"name\":\"Player One\",\"character\":\"Jett\",\"player_id\":\"one\",\"teammate\":true,\"kills\":12,\"deaths\":5,\"assists\":3,\"money\":2400,\"ult_points\":5,\"ult_max\":7}","scoreboard_1":"{\"name\":\"Player Two\",\"character\":\"Sage\",\"player_id\":\"two\",\"teammate\":false,\"kills\":8,\"deaths\":7,\"assists\":4}"}}}""");
        connected = true; connection.Text = "プレビュー"; selectedSlot = "0"; RenderState();
    }

    internal void ShowFullPreview(bool pregame)
    {
        var sampleAgents = agents.Where(a => Guid.TryParse(a.Id, out _)).Take(10).ToArray();
        if (sampleAgents.Length < 10) { ShowPreview(); return; }
        var ids = Enumerable.Range(0, 10).Select(i => $"preview-{i}").ToArray();
        if (pregame)
            state.ReplacePregame(new MatchSnapshot("preview", "character_select_active", ids.Take(5)
                .Select((id, i) => new Player(id, sampleAgents[i].Id, i == 4 ? "selected" : "locked")).ToArray(), "Ascent"), ids[0], "Player 1#JP", sampleAgents);
        else
            state.ReplaceRoster(new LiveRosterMatch("preview", "Ascent", "competitive", ids
                .Select((id, i) => new LiveRosterPlayer(id, i < 5 ? "Blue" : "Red", sampleAgents[i].Id, false, false, 100 + i)).ToArray()), ids[0], "Player 1#JP", sampleAgents);
        var names = ids.Select((id, i) => (id, name: ($"Player {i + 1}", "JP")))
            .ToDictionary(item => item.id, item => item.name, StringComparer.OrdinalIgnoreCase);
        state.ApplyNames(names);
        state.ApplyRanks(ids.Select((id, i) => (id, rank: new LiveRecentRank(15 + i % 10, 40 + i)))
            .ToDictionary(item => item.id, item => item.rank, StringComparer.OrdinalIgnoreCase));
        if (!pregame)
        {
            state.SetScore(7, 5);
            state.ApplyScores(ids.Select((id, i) => (id, score: new LiveScore(10 + i, 5 + i, 2)
                {
                    Money = 800 + i * 400, UltPoints = i % 4 == 0 ? 7 : i % 7, UltMax = 7, Alive = i % 3 != 2,
                    Shield = i < 5 ? 2 : null, Weapon = i < 5 ? "TX_Hud_Rifles_Volcano" : null, Spike = i == 0 ? true : null, Headshots = i == 0 ? 4 : null
                }))
                .ToDictionary(item => item.id, item => item.score, StringComparer.OrdinalIgnoreCase));
            lastScoreUpdate = DateTimeOffset.UtcNow;
        }
        connected = true; connection.Text = "プレビュー";
        selectedSlot = state.Players.First(pair => LiveMatchState.Text(pair.Value, "player_id") == ids[0]).Key;
        RenderState();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !lifetime.IsCancellationRequested) { lifetime.Cancel(); eventBridge?.Dispose(); pollTimer.Stop(); pollTimer.Dispose(); renderTimer.Stop(); renderTimer.Dispose(); }
        base.Dispose(disposing);
    }
}

internal enum LiveRosterSide { Ally, Enemy, Unknown }
internal enum LiveRosterLayout { Teams, Pregame, FreeForAll }

internal sealed record LiveRosterRow(string Slot, LiveRosterSide Side, string Name, string Agent, string AgentId, int? RankTier,
    string Rank, string Kda, string Status, bool IsLocal, bool Tentative);

// Allies fill the top half and opponents the bottom half; each half is sized for five rows so a full team fills it.
// During agent select the bottom half only says that opponents are not available yet: no placeholder players are drawn.
// Larger groups (deathmatch, custom games) shrink rows down to a readable minimum and then scroll instead of being cut.
internal sealed class LiveRosterView : ScrollableControl
{
    private sealed record Section(string Title, LiveRosterSide Side, IReadOnlyList<LiveRosterRow> Rows, string Placeholder)
    {
        public int Weight => Math.Max(5, Rows.Count);
    }

    private IReadOnlyList<LiveRosterRow> rows = [];
    private IReadOnlyList<LiveRosterRow> ordered = [];
    private List<Section> sections = [];
    private readonly List<Rectangle> sectionRects = [];
    private readonly Dictionary<string, Rectangle> rowRects = new(StringComparer.Ordinal);
    private LiveRosterLayout arrangement;
    private string? selectedSlot, hoverSlot;
    private int headerHeight, rowHeight;
    private bool layingOut;

    public event EventHandler? SelectionChanged;

    public LiveRosterView()
    {
        SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint |
            ControlStyles.ResizeRedraw | ControlStyles.Selectable, true);
        TabStop = true;
        AutoScroll = true;
        BackColor = Theme.Surface;
        ForeColor = Theme.Text;
        Font = NativePageStyle.Reading;
    }

    /// <summary>Rows in display order (top section first).</summary>
    public IReadOnlyList<LiveRosterRow> Rows => ordered;
    public string? SelectedSlot => selectedSlot;
    public LiveRosterLayout Arrangement => arrangement;
    public bool ShowsPendingOpponents => arrangement == LiveRosterLayout.Pregame;
    public int SectionCount => sectionRects.Count;
    public int RowHeight => rowHeight;
    public Rectangle SectionBounds(int index) => index >= 0 && index < sectionRects.Count ? ToView(sectionRects[index]) : Rectangle.Empty;
    public Rectangle RowBounds(string slot) => rowRects.TryGetValue(slot, out var r) ? ToView(r) : Rectangle.Empty;
    public int ContentHeight => sectionRects.Count == 0 ? 0 : sectionRects[^1].Bottom;

    // Replaces the rows without raising SelectionChanged. Repaints only when something visible changed, so the 200 ms
    // render tick does not make the list flicker or lose its scroll position.
    public void SetRows(IReadOnlyList<LiveRosterRow> updated, LiveRosterLayout layout, string? selected)
    {
        if (selected is not null && !updated.Any(r => r.Slot == selected)) selected = null;
        if (hoverSlot is not null && !updated.Any(r => r.Slot == hoverSlot)) hoverSlot = null;
        var changed = layout != arrangement || !rows.SequenceEqual(updated);
        var selectionChanged = !string.Equals(selected, selectedSlot, StringComparison.Ordinal);
        selectedSlot = selected;
        if (changed)
        {
            rows = updated.ToArray();
            arrangement = layout;
            sections = BuildSections();
            ordered = sections.SelectMany(s => s.Rows).ToArray();
            LayoutRows();
        }
        if (changed || selectionChanged)
        {
            Invalidate();
            if (selectionChanged && selectedSlot is not null) NotifySelection();
        }
    }

    // Same path as a click: updates the view and raises SelectionChanged.
    public void SelectSlot(string slot)
    {
        if (!ordered.Any(r => r.Slot == slot)) return;
        EnsureVisible(slot);
        if (string.Equals(slot, selectedSlot, StringComparison.Ordinal)) return;
        selectedSlot = slot;
        Invalidate();
        NotifySelection();
        SelectionChanged?.Invoke(this, EventArgs.Empty);
    }

    private List<Section> BuildSections()
    {
        IReadOnlyList<LiveRosterRow> Of(params LiveRosterSide[] sides) => rows.Where(r => sides.Contains(r.Side)).OrderByDescending(r => r.IsLocal).ToArray();
        var list = new List<Section>();
        switch (arrangement)
        {
            case LiveRosterLayout.Pregame:
                // Agent select only exposes allies; anything else that arrives still stays visible in the top half.
                list.Add(new("味方チーム", LiveRosterSide.Ally, Of(LiveRosterSide.Ally, LiveRosterSide.Unknown, LiveRosterSide.Enemy), "味方の情報を待っています。"));
                list.Add(new("相手チーム", LiveRosterSide.Enemy, [], "ピック中は相手チームの情報を取得できません。\n試合が始まると表示します。"));
                break;
            case LiveRosterLayout.FreeForAll:
                list.Add(new("プレイヤー（個人戦）", LiveRosterSide.Unknown, Of(LiveRosterSide.Ally, LiveRosterSide.Enemy, LiveRosterSide.Unknown), "プレイヤー情報がありません。"));
                break;
            default:
                var allies = Of(LiveRosterSide.Ally); var enemies = Of(LiveRosterSide.Enemy); var unknown = Of(LiveRosterSide.Unknown);
                if (allies.Count > 0 || enemies.Count > 0 || unknown.Count == 0)
                {
                    list.Add(new("味方チーム", LiveRosterSide.Ally, allies, "味方の情報がありません。"));
                    list.Add(new("相手チーム", LiveRosterSide.Enemy, enemies, "相手チームの情報がありません。"));
                }
                if (unknown.Count > 0) list.Add(new("チーム不明", LiveRosterSide.Unknown, unknown, ""));
                break;
        }
        return list;
    }

    private int Px(int value) => UiMetrics.Px(this, value);
    private int MinRowHeight => Px(40);
    private int MaxRowHeight => Px(120);
    private int SectionGap => Px(10);

    private void LayoutRows()
    {
        if (layingOut) return;
        layingOut = true;
        try
        {
            if (sections.Count == 0) { sectionRects.Clear(); rowRects.Clear(); AutoScrollMinSize = Size.Empty; return; }
            headerHeight = Math.Max(Px(30), NativePageStyle.TextHeight(this, NativePageStyle.SmallBold, 1, 10));
            var height = Math.Max(0, ClientSize.Height);
            var content = Arrange(Math.Max(0, ClientSize.Width), height);
            AutoScrollMinSize = content > height ? new Size(0, content) : Size.Empty;
            // Showing or hiding the vertical scroll bar changes only the width; lay the rows out again to match it.
            if (ClientSize.Width != (sectionRects.Count > 0 ? sectionRects[0].Width : 0)) Arrange(Math.Max(0, ClientSize.Width), height);
        }
        finally { layingOut = false; }
    }

    private int Arrange(int width, int height)
    {
        sectionRects.Clear(); rowRects.Clear();
        var totalWeight = sections.Sum(s => s.Weight);
        var body = height - headerHeight * sections.Count - SectionGap * (sections.Count - 1);
        // unit is the row pitch that makes each section exactly weight/total of the height (50/50 for two teams).
        var unit = Math.Max(MinRowHeight, body / Math.Max(1, totalWeight));
        rowHeight = Math.Min(unit, MaxRowHeight);
        var y = 0;
        for (var i = 0; i < sections.Count; i++)
        {
            var section = sections[i];
            var sectionHeight = headerHeight + section.Weight * unit;
            if (i == sections.Count - 1) sectionHeight = Math.Max(sectionHeight, height - y);
            sectionRects.Add(new Rectangle(0, y, width, sectionHeight));
            var rowY = y + headerHeight;
            foreach (var row in section.Rows) { rowRects[row.Slot] = new Rectangle(0, rowY, width, rowHeight); rowY += rowHeight; }
            y += sectionHeight + SectionGap;
        }
        return y - SectionGap;
    }

    private Rectangle ToView(Rectangle content) { content.Offset(0, AutoScrollPosition.Y); return content; }

    private string? HitTest(Point point)
    {
        var content = point with { Y = point.Y - AutoScrollPosition.Y };
        foreach (var pair in rowRects) if (pair.Value.Contains(content)) return pair.Key;
        return null;
    }

    private void EnsureVisible(string slot)
    {
        if (!rowRects.TryGetValue(slot, out var r) || AutoScrollMinSize.Height == 0) return;
        var top = -AutoScrollPosition.Y;
        if (r.Top < top) AutoScrollPosition = new Point(0, r.Top);
        else if (r.Bottom > top + ClientSize.Height) AutoScrollPosition = new Point(0, r.Bottom - ClientSize.Height);
        Invalidate();
    }

    private void NotifySelection()
    {
        if (!IsHandleCreated || selectedSlot is null) return;
        var index = ordered.ToList().FindIndex(r => r.Slot == selectedSlot);
        if (index < 0) return;
        AccessibilityNotifyClients(AccessibleEvents.Selection, index);
        if (Focused) AccessibilityNotifyClients(AccessibleEvents.Focus, index);
    }

    internal static string Describe(LiveRosterRow row)
    {
        var side = row.Side switch { LiveRosterSide.Ally => "味方", LiveRosterSide.Enemy => "相手", _ => "チーム不明" };
        return $"{side}、{row.Name}{(row.IsLocal ? "（自分）" : "")}、{row.Agent}、ランク {row.Rank}、K/D/A {row.Kda}、{row.Status}";
    }

    private static Color SideColor(LiveRosterSide side) => side switch
    {
        LiveRosterSide.Ally => Theme.Friendly,
        LiveRosterSide.Enemy => Theme.Opposing,
        _ => Theme.Muted
    };

    private static Color StatusColor(string status) => status switch
    {
        "確定" or "自分" or "ULT 準備完了" => Theme.Green,
        "味方" => Theme.Friendly,
        "相手" => Theme.Opposing,
        "選択中" => Theme.Text,
        _ => Theme.Muted
    };

    private const TextFormatFlags LineFlags = TextFormatFlags.Left | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine |
        TextFormatFlags.EndEllipsis | TextFormatFlags.NoPrefix | TextFormatFlags.NoPadding;

    private static int LineHeight(Font font) => TextRenderer.MeasureText("国Ag", font, Size.Empty, TextFormatFlags.NoPadding).Height;

    private (Rectangle Icon, Rectangle Name, Rectangle Rank, Rectangle Kda, Rectangle Status) Columns(Rectangle row)
    {
        var left = row.X + Px(18);
        var right = row.Right - Px(12);
        var icon = Math.Clamp(rowHeight - Px(12), Px(22), Px(72));
        var iconRect = new Rectangle(left, row.Y + (row.Height - icon) / 2, icon, icon);
        var x = iconRect.Right + Px(12);
        var rest = Math.Max(0, right - x);
        var gap = Px(8);
        int status = rest * 15 / 100, kda = rest * 18 / 100, rank = rest * 29 / 100, name = rest - status - kda - rank;
        Rectangle Column(int start, int w) => new(start, row.Y, Math.Max(0, w), row.Height);
        var nameRect = Column(x, name - gap); x += name;
        var rankRect = Column(x, rank - gap); x += rank;
        var kdaRect = Column(x, kda - gap); x += kda;
        return (iconRect, nameRect, rankRect, kdaRect, Column(x, right - x));
    }

    protected override void OnPaint(PaintEventArgs e)
    {
        var g = e.Graphics;
        g.Clear(Theme.Surface);
        for (var i = 0; i < sections.Count && i < sectionRects.Count; i++)
        {
            var bounds = ToView(sectionRects[i]);
            if (!bounds.IntersectsWith(e.ClipRectangle)) continue;
            DrawSection(g, sections[i], bounds, e.ClipRectangle);
        }
        if (Focused && ShowFocusCues && (selectedSlot is null || !rowRects.ContainsKey(selectedSlot)))
            ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(ClientRectangle, -Px(2), -Px(2)), Theme.Text, Theme.Surface);
    }

    private void DrawSection(Graphics g, Section section, Rectangle bounds, Rectangle clip)
    {
        var header = new Rectangle(bounds.X, bounds.Y, bounds.Width, headerHeight);
        var accent = SideColor(section.Side);
        using (var bar = new SolidBrush(accent))
            g.FillRectangle(bar, header.X + Px(4), header.Y + Px(8), Math.Max(2, Px(4)), Math.Max(1, header.Height - Px(16)));
        var titleRect = new Rectangle(header.X + Px(14), header.Y, Math.Max(0, header.Width - Px(14)), header.Height);
        var title = section.Title;
        TextRenderer.DrawText(g, title, NativePageStyle.SmallBold, titleRect, section.Side == LiveRosterSide.Unknown ? Theme.Text : accent, LineFlags);
        var titleWidth = TextRenderer.MeasureText(g, title, NativePageStyle.SmallBold, Size.Empty, TextFormatFlags.NoPadding).Width;
        var countText = section.Rows.Count == 0 ? "" : $"{section.Rows.Count}人";
        var columns = Columns(header);
        var countRect = new Rectangle(titleRect.X + titleWidth + Px(8), header.Y, Math.Max(0, columns.Rank.X - titleRect.X - titleWidth - Px(8)), header.Height);
        if (countText.Length > 0) TextRenderer.DrawText(g, countText, NativePageStyle.Small, countRect, Theme.Muted, LineFlags);
        if (section.Rows.Count > 0)
        {
            TextRenderer.DrawText(g, "ランク", NativePageStyle.Small, columns.Rank, Theme.Muted, LineFlags);
            TextRenderer.DrawText(g, "K / D / A", NativePageStyle.Small, columns.Kda, Theme.Muted, LineFlags);
            TextRenderer.DrawText(g, arrangement == LiveRosterLayout.Pregame ? "選択状況" : "状態", NativePageStyle.Small, columns.Status, Theme.Muted, LineFlags);
        }
        using (var line = new Pen(Theme.Border))
            g.DrawLine(line, header.X + Px(4), header.Bottom - 1, header.Right - Px(4), header.Bottom - 1);

        var body = Rectangle.FromLTRB(bounds.X, header.Bottom, bounds.Right, bounds.Bottom);
        if (section.Rows.Count == 0)
        {
            if (section.Placeholder.Length > 0)
                TextRenderer.DrawText(g, section.Placeholder, NativePageStyle.Reading, Rectangle.Inflate(body, -Px(16), -Px(8)), Theme.Muted,
                    TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.WordBreak | TextFormatFlags.NoPrefix);
            return;
        }
        foreach (var row in section.Rows)
        {
            if (!rowRects.TryGetValue(row.Slot, out var rect)) continue;
            var view = ToView(rect);
            if (view.IntersectsWith(clip)) DrawRow(g, row, view);
        }
    }

    private void DrawRow(Graphics g, LiveRosterRow row, Rectangle bounds)
    {
        var selected = string.Equals(row.Slot, selectedSlot, StringComparison.Ordinal);
        var hovered = !selected && string.Equals(row.Slot, hoverSlot, StringComparison.Ordinal);
        var card = Rectangle.Inflate(bounds, -Px(4), -Math.Max(1, Px(3)));
        var fill = selected ? Theme.Selected : hovered ? Theme.Elevated : Theme.Surface;
        var smoothing = g.SmoothingMode;
        g.SmoothingMode = SmoothingMode.AntiAlias;
        if (fill != Theme.Surface)
        {
            using var path = ThemedButton.Rounded(card, Px(8));
            using var brush = new SolidBrush(fill);
            g.FillPath(brush, path);
        }
        else
        {
            using var divider = new Pen(Theme.Elevated);
            g.DrawLine(divider, card.X + Px(14), bounds.Bottom - 1, card.Right, bounds.Bottom - 1);
        }
        using (var bar = new SolidBrush(SideColor(row.Side)))
            g.FillRectangle(bar, card.X, card.Y + Px(6), Math.Max(2, Px(4)), Math.Max(1, card.Height - Px(12)));

        var columns = Columns(bounds);
        DrawAgentIcon(g, row, columns.Icon);
        g.SmoothingMode = smoothing;

        // Name with the agent underneath when the row has room for two lines; the icon still identifies the agent otherwise.
        var large = rowHeight >= Px(64);
        var nameFont = large ? NativePageStyle.Subheading : NativePageStyle.ReadingBold;
        var subFont = large ? NativePageStyle.Body : NativePageStyle.Small;
        var nameHeight = LineHeight(nameFont);
        var subHeight = LineHeight(subFont);
        var subText = row.Agent + (row.IsLocal ? " · 自分" : "");
        if (nameHeight + subHeight + Px(2) <= bounds.Height - Px(8))
        {
            var top = bounds.Y + (bounds.Height - nameHeight - subHeight - Px(2)) / 2;
            TextRenderer.DrawText(g, row.Name, nameFont, new Rectangle(columns.Name.X, top, columns.Name.Width, nameHeight), Theme.Text, LineFlags);
            TextRenderer.DrawText(g, subText, subFont, new Rectangle(columns.Name.X, top + nameHeight + Px(2), columns.Name.Width, subHeight), Theme.Muted, LineFlags);
        }
        else TextRenderer.DrawText(g, row.Name + (row.IsLocal ? "（自分）" : ""), nameFont, columns.Name, Theme.Text, LineFlags);

        var rankText = columns.Rank;
        if (row.RankTier is { } tier && RankImages.Get(tier) is { } rankImage && rankText.Width > Px(24))
        {
            var size = Math.Clamp(bounds.Height - Px(16), Px(18), Px(44));
            var iconRect = new Rectangle(rankText.X, bounds.Y + (bounds.Height - size) / 2, size, size);
            DrawContained(g, rankImage, iconRect, 1f);
            rankText = Rectangle.FromLTRB(iconRect.Right + Px(8), rankText.Y, rankText.Right, rankText.Bottom);
        }
        var valueFont = large ? NativePageStyle.Reading : NativePageStyle.Body;
        TextRenderer.DrawText(g, row.Rank, valueFont, rankText, row.Rank == "—" ? Theme.Muted : Theme.Text, LineFlags);
        var kdaFont = SharedFonts.Get("Segoe UI", large ? 12f : 10.5f, FontStyle.Bold);
        TextRenderer.DrawText(g, row.Kda, kdaFont, columns.Kda, row.Kda == "—" ? Theme.Muted : Theme.Text, LineFlags);
        TextRenderer.DrawText(g, row.Status, large ? NativePageStyle.ReadingBold : NativePageStyle.BodyBold, columns.Status, StatusColor(row.Status), LineFlags);

        if (selected && Focused && ShowFocusCues)
            ControlPaint.DrawFocusRectangle(g, Rectangle.Inflate(card, -Px(2), -Px(2)), Theme.Text, fill);
    }

    private void DrawAgentIcon(Graphics g, LiveRosterRow row, Rectangle rect)
    {
        using var frame = ThemedButton.Rounded(rect, Px(6));
        using (var back = new SolidBrush(Theme.Elevated)) g.FillPath(back, frame);
        var image = AgentImages.Get(row.AgentId, "icon");
        if (image is not null)
        {
            var saved = g.Save();
            g.SetClip(frame, CombineMode.Intersect);
            DrawContained(g, image, rect, row.Tentative ? .45f : 1f);
            g.Restore(saved);
        }
        else
        {
            var initial = row.Agent is { Length: > 0 } a && a != "—" && a != "未選択" && a != "不明" ? a[..1] : "?";
            TextRenderer.DrawText(g, initial, NativePageStyle.Subheading, rect, Theme.Muted,
                TextFormatFlags.HorizontalCenter | TextFormatFlags.VerticalCenter | TextFormatFlags.SingleLine | TextFormatFlags.NoPrefix);
        }
    }

    private static void DrawContained(Graphics g, Image image, Rectangle rect, float opacity)
    {
        if (image.Width <= 0 || image.Height <= 0 || rect.Width <= 0 || rect.Height <= 0) return;
        var scale = Math.Min(rect.Width / (float)image.Width, rect.Height / (float)image.Height);
        var w = Math.Max(1, (int)Math.Round(image.Width * scale));
        var h = Math.Max(1, (int)Math.Round(image.Height * scale));
        var target = new Rectangle(rect.X + (rect.Width - w) / 2, rect.Y + (rect.Height - h) / 2, w, h);
        var interpolation = g.InterpolationMode; var pixel = g.PixelOffsetMode;
        g.InterpolationMode = InterpolationMode.HighQualityBicubic;
        g.PixelOffsetMode = PixelOffsetMode.HighQuality;
        if (opacity >= 1f) g.DrawImage(image, target);
        else
        {
            using var attributes = new ImageAttributes();
            attributes.SetColorMatrix(new ColorMatrix { Matrix33 = opacity });
            g.DrawImage(image, target, 0, 0, image.Width, image.Height, GraphicsUnit.Pixel, attributes);
        }
        g.InterpolationMode = interpolation; g.PixelOffsetMode = pixel;
    }

    protected override void OnClientSizeChanged(EventArgs e)
    {
        base.OnClientSizeChanged(e);
        LayoutRows();
        Invalidate();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        LayoutRows();
        Invalidate();
    }

    protected override void OnScroll(ScrollEventArgs se) { base.OnScroll(se); Invalidate(); }
    protected override void OnMouseWheel(MouseEventArgs e) { base.OnMouseWheel(e); Invalidate(); }

    protected override void OnMouseMove(MouseEventArgs e)
    {
        base.OnMouseMove(e);
        var slot = HitTest(e.Location);
        if (string.Equals(slot, hoverSlot, StringComparison.Ordinal)) return;
        hoverSlot = slot;
        Cursor = slot is null ? Cursors.Default : Cursors.Hand;
        Invalidate();
    }

    protected override void OnMouseLeave(EventArgs e)
    {
        base.OnMouseLeave(e);
        if (hoverSlot is null) return;
        hoverSlot = null; Cursor = Cursors.Default; Invalidate();
    }

    protected override void OnMouseDown(MouseEventArgs e)
    {
        if (CanFocus && !Focused) Focus();
        base.OnMouseDown(e);
        if (e.Button == MouseButtons.Left && HitTest(e.Location) is { } slot) SelectSlot(slot);
    }

    protected override bool IsInputKey(Keys keyData) =>
        (keyData & Keys.KeyCode) is Keys.Up or Keys.Down or Keys.Home or Keys.End || base.IsInputKey(keyData);

    protected override void OnKeyDown(KeyEventArgs e)
    {
        base.OnKeyDown(e);
        if (ordered.Count == 0 || e.Modifiers != Keys.None) return;
        var index = ordered.ToList().FindIndex(r => r.Slot == selectedSlot);
        var next = e.KeyCode switch
        {
            Keys.Up => index < 0 ? 0 : Math.Max(0, index - 1),
            Keys.Down => index < 0 ? 0 : Math.Min(ordered.Count - 1, index + 1),
            Keys.Home => 0,
            Keys.End => ordered.Count - 1,
            _ => -1
        };
        if (next < 0) return;
        SelectSlot(ordered[next].Slot);
        e.Handled = true;
    }

    protected override void OnHandleCreated(EventArgs e) { base.OnHandleCreated(e); DarkForm.ApplyDarkScrollBars(this); }
    protected override void OnGotFocus(EventArgs e) { base.OnGotFocus(e); Invalidate(); NotifySelection(); }
    protected override void OnLostFocus(EventArgs e) { base.OnLostFocus(e); Invalidate(); }

    protected override AccessibleObject CreateAccessibilityInstance() => new RosterAccessible(this);

    private sealed class RosterAccessible : ControlAccessibleObject
    {
        private readonly LiveRosterView owner;
        public RosterAccessible(LiveRosterView view) : base(view) => owner = view;
        public override AccessibleRole Role => AccessibleRole.List;
        public override int GetChildCount() => owner.ordered.Count;
        public override AccessibleObject? GetChild(int index) =>
            index >= 0 && index < owner.ordered.Count ? new RowAccessible(owner, owner.ordered[index]) : null;
        public override AccessibleObject? GetSelected() =>
            owner.ordered.FirstOrDefault(r => r.Slot == owner.selectedSlot) is { } row ? new RowAccessible(owner, row) : null;
        public override AccessibleObject? GetFocused() => owner.Focused ? GetSelected() ?? this : null;
    }

    private sealed class RowAccessible(LiveRosterView owner, LiveRosterRow row) : AccessibleObject
    {
        public override string? Name => Describe(row);
        public override AccessibleRole Role => AccessibleRole.ListItem;
        public override AccessibleObject? Parent => owner.AccessibilityObject;
        public override Rectangle Bounds => owner.IsHandleCreated ? owner.RectangleToScreen(owner.RowBounds(row.Slot)) : Rectangle.Empty;
        public override string? DefaultAction => "選択";
        public override AccessibleStates State
        {
            get
            {
                var state = AccessibleStates.Selectable | AccessibleStates.Focusable;
                if (string.Equals(row.Slot, owner.selectedSlot, StringComparison.Ordinal))
                    state |= AccessibleStates.Selected | (owner.Focused ? AccessibleStates.Focused : 0);
                return state;
            }
        }
        public override void DoDefaultAction() => owner.SelectSlot(row.Slot);
        public override void Select(AccessibleSelection flags)
        {
            if ((flags & (AccessibleSelection.TakeSelection | AccessibleSelection.AddSelection | AccessibleSelection.TakeFocus)) != 0)
            {
                if ((flags & AccessibleSelection.TakeFocus) != 0) owner.Focus();
                owner.SelectSlot(row.Slot);
            }
        }
    }
}
