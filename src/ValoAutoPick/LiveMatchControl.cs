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
    private readonly ListView roster = new() { Dock = DockStyle.Fill, View = View.Details, FullRowSelect = true, MultiSelect = false, HideSelection = false, BorderStyle = BorderStyle.None, BackColor = Theme.Surface, ForeColor = Theme.Text, Font = NativePageStyle.Reading };
    private readonly System.Windows.Forms.Timer renderTimer = new() { Interval = 200 };
    private readonly System.Windows.Forms.Timer pollTimer = new() { Interval = 5000 };
    private bool dirty, connected;
    private bool polling;
    private int sessionGeneration;
    private DateTimeOffset nextRetry;
    // When the roster on screen was last confirmed by a successful refresh; null when nothing is on screen.
    private DateTimeOffset? lastSuccess;
    private string? selectedSlot;

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
        var header = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty };
        var title = NativePageStyle.Label("ライブトラッカー", NativePageStyle.Heading); title.Dock = DockStyle.Left; title.Width = UiMetrics.Px(this, 260);
        var refresh = new ThemedButton { Text = "更新", Compact = true, Dock = DockStyle.Right, Width = UiMetrics.Px(this, 100) };
        refresh.Click += async (_, _) => await RefreshLiveAsync();
        connection.Dock = DockStyle.Fill; header.Controls.Add(connection); header.Controls.Add(refresh); header.Controls.Add(title);
        summary.Dock = DockStyle.Fill;
        var content = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Theme.Background, Margin = Padding.Empty };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 65)); content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 35));
        var listCard = NativePageStyle.Card(); listCard.Margin = new Padding(0, 0, UiMetrics.Px(this, 10), 0);
        var detailCard = NativePageStyle.Card(); detailCard.Margin = Padding.Empty;
        roster.AccessibleName = "試合中のプレイヤー";
        roster.Columns.Add("プレイヤー", 240); roster.Columns.Add("エージェント", 140); roster.Columns.Add("チーム", 90);
        void SizeColumns()
        {
            var available = Math.Max(100, roster.ClientSize.Width - 2);
            roster.Columns[0].Width = available * 45 / 100;
            roster.Columns[1].Width = available * 35 / 100;
            roster.Columns[2].Width = available - roster.Columns[0].Width - roster.Columns[1].Width;
        }
        roster.SizeChanged += (_, _) => SizeColumns();
        roster.OwnerDraw = true;
        roster.DrawColumnHeader += (_, e) => { using var fill = new SolidBrush(Theme.Elevated); e.Graphics.FillRectangle(fill, e.Bounds); TextRenderer.DrawText(e.Graphics, e.Header?.Text ?? "", NativePageStyle.SmallBold, e.Bounds, Theme.Muted, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis); };
        roster.DrawItem += (_, _) => { };
        roster.DrawSubItem += (_, e) =>
        {
            if (e.Item is null || e.SubItem is null) return;
            using var fill = new SolidBrush(e.Item.Selected ? Theme.Selected : Theme.Surface);
            e.Graphics.FillRectangle(fill, e.Bounds);
            var bounds = Rectangle.Inflate(e.Bounds, -4, 0);
            TextRenderer.DrawText(e.Graphics, e.SubItem.Text, roster.Font, bounds, Theme.Text, TextFormatFlags.VerticalCenter | TextFormatFlags.Left | TextFormatFlags.EndEllipsis);
            if (e.Item.Selected && roster.Focused) ControlPaint.DrawFocusRectangle(e.Graphics, e.Bounds, Theme.Text, Theme.Selected);
        };
        roster.SelectedIndexChanged += (_, _) => { if (roster.SelectedItems.Count > 0) { selectedSlot = (string)roster.SelectedItems[0].Tag!; RenderDetails(); } };
        listCard.Controls.Add(roster);
        var detailTitle = NativePageStyle.Label("プレイヤー詳細", NativePageStyle.Subheading); detailTitle.Dock = DockStyle.Top; detailTitle.Height = UiMetrics.Px(this, 48);
        details.Dock = DockStyle.Fill; details.TextAlign = ContentAlignment.TopLeft; details.AutoEllipsis = false;
        detailCard.Controls.Add(details); detailCard.Controls.Add(detailTitle);
        content.Controls.Add(listCard, 0, 0); content.Controls.Add(detailCard, 1, 0);
        root.Controls.Add(header, 0, 0); root.Controls.Add(summary, 0, 1); root.Controls.Add(content, 0, 2); Controls.Add(root);
        renderTimer.Tick += (_, _) => { if (ExpireStaleSnapshot()) dirty = true; if (dirty) { dirty = false; RenderState(); } };
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
            var matchId = await api.GetCoreMatchId(lifetime.Token);
            var match = matchId is null ? null : await api.GetCoreLiveRoster(matchId, lifetime.Token);
            if (IsDisposed || Disposing) return;
            if (generation != sessionGeneration || !api.HasAccountSession || !string.Equals(api.Subject, subject, StringComparison.OrdinalIgnoreCase))
            {
                if (!api.HasAccountSession) ResetSession();
                return;
            }
            connected = true;
            if (match is null)
            {
                state.Reset();
                selectedSlot = null;
                connection.Text = "試合開始を待機中";
            }
            else
            {
                (int Allies, int Enemies)? score = null;
                try { score = await api.GetLocalLiveScore(match.MapId, lifetime.Token); }
                catch (Exception e) when (e is IOException or HttpRequestException or JsonException or InvalidOperationException or ClientUnavailableException) { }
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
                state.ReplaceRoster(match, subject, api.AccountName, agents);
                if (!string.Equals(previousMatchId, match.MatchId, StringComparison.OrdinalIgnoreCase))
                {
                    selectedSlot = null;
                }
                else if (selectedPlayerId.Length > 0)
                {
                    selectedSlot = state.Players.FirstOrDefault(pair =>
                        string.Equals(LiveMatchState.Text(pair.Value, "player_id"), selectedPlayerId, StringComparison.OrdinalIgnoreCase)).Key;
                }
                if (score is { } s) state.SetScore(s.Allies, s.Enemies);
                connection.Text = "試合情報を更新中・K/D は取得できません";
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

    // After a failed refresh: keep the last confirmed roster only while LiveMatchState.IsExpired says it is still
    // trustworthy, and say when it was confirmed. Anything older is dropped together with the selection.
    private void KeepOrDropSnapshot(string reason)
    {
        connected = false;
        if (lastSuccess is { } at && state.Players.Count > 0 && !LiveMatchState.IsExpired(DateTimeOffset.UtcNow - at))
            connection.Text = $"{reason}（{at.ToLocalTime():HH:mm:ss} 時点の情報を表示中）";
        else
        {
            state.Reset(); selectedSlot = null; lastSuccess = null;
            connection.Text = reason;
        }
        dirty = true;
    }

    // Runs on every render tick and when the tab is shown, so a retained roster also ages out during a 429 wait
    // or while the tab was hidden (no poll runs in either case). Returns true when it cleared the screen.
    private bool ExpireStaleSnapshot()
    {
        if (lastSuccess is not { } at || state.Players.Count == 0 || !LiveMatchState.IsExpired(DateTimeOffset.UtcNow - at)) return false;
        state.Reset(); selectedSlot = null; lastSuccess = null; connected = false;
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
        var stateLabel = state.MatchEnded ? "試合終了" : holding ? "再接続待ち" : !connected ? "未接続" : state.Players.Count == 0 && map == "—" ? "試合待機中" : "LIVE";
        var phase = Field("round_phase") switch { "shopping" => "購入フェーズ", "combat" => "戦闘中", "end" => "ラウンド終了", "game_end" => "試合終了", "—" => "", var other => other };
        var mode = ModeName(Field("game_mode"));
        var parts = new List<string> { stateLabel };
        if (map != "—") parts.Add(MapCatalog.Name(map));
        if (!string.IsNullOrEmpty(mode) && !Guid.TryParse(mode, out _)) parts.Add(mode);
        if (score != "—" && !holding) parts.Add(score);
        if (Field("round_number") is { } round && round != "—") parts.Add("ラウンド " + round);
        if (phase.Length > 0) parts.Add(phase);
        summary.Text = string.Join("    ", parts);
        roster.BeginUpdate();
        try
        {
            roster.Items.Clear();
            foreach (var pair in state.Players.OrderBy(p => int.Parse(p.Key)))
            {
                var p = pair.Value;
                string V(string key) { var s = LiveMatchState.Text(p, key); return s.Length == 0 ? "—" : s; }
                var side = V("teammate") switch { "true" => "味方", "false" => "相手", _ => "—" };
                var item = new ListViewItem(V("name")) { Tag = pair.Key };
                item.SubItems.Add(AgentName(V("character"))); item.SubItems.Add(side);
                roster.Items.Add(item);
                if (pair.Key == selectedSlot) item.Selected = true;
            }
        }
        finally { roster.EndUpdate(); }
        RenderDetails();
    }

    private void RenderDetails()
    {
        if (selectedSlot is null || !state.Players.TryGetValue(selectedSlot, out var p)) { details.Text = "プレイヤーを選択すると、試合中の情報を表示します。"; return; }
        string V(string key) { var s = LiveMatchState.Text(p, key); return s.Length == 0 ? "—" : s; }
        var level = V("account_level");
        details.Text = $"{V("name")}\n\nエージェント   {AgentName(V("character"))}" + (level == "—" ? "" : $"\n\nアカウントレベル   {level}") + "\n\n試合中の K/D や戦績は取得できません。";
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

    protected override void Dispose(bool disposing)
    {
        if (disposing && !lifetime.IsCancellationRequested) { lifetime.Cancel(); pollTimer.Stop(); pollTimer.Dispose(); renderTimer.Stop(); renderTimer.Dispose(); }
        base.Dispose(disposing);
    }
}
