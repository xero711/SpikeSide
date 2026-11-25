using System.Net.Http.Json;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace ValoAutoPick;

public sealed class MatchHistoryControl : UserControl
{
    private const int PageSize = 10;
    private static readonly Uri WeaponCatalogUri = new("https://valorant-api.com/v1/weapons?language=ja-JP");
    private readonly RiotApi api;
    private IReadOnlyDictionary<string, Agent> agents;
    private readonly HttpClient assets = new() { Timeout = TimeSpan.FromSeconds(8) };
    private readonly CancellationTokenSource lifetime = new();
    private readonly object archiveOperationsGate = new();
    private readonly HashSet<Task> archiveOperations = [];
    private readonly List<MatchAnalytics> matches = [];
    private readonly Dictionary<string, int> matchPositions = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(Label Value, Label Caption)> metricLabels = [];
    private readonly Panel matchList = new() { Dock = DockStyle.Fill, BackColor = Theme.Surface, Margin = Padding.Empty };
    private readonly Panel matchRows = new() { Dock = DockStyle.Fill, BackColor = Theme.Surface, Margin = Padding.Empty };
    private readonly Dictionary<MatchAnalytics, Control> visibleMatchRows = new(ReferenceEqualityComparer.Instance);
    private readonly TableLayoutPanel scoreboard = new() { Dock = DockStyle.Fill, BackColor = Theme.Surface, Margin = Padding.Empty, CellBorderStyle = TableLayoutPanelCellBorderStyle.None, AutoScroll = false };
    private readonly FlowLayoutPanel rounds = new() { Dock = DockStyle.Fill, AutoScroll = false, WrapContents = true, FlowDirection = FlowDirection.LeftToRight, BackColor = Theme.Surface, Margin = Padding.Empty };
    private readonly FlowLayoutPanel mapRows = new() { Dock = DockStyle.Fill, AutoScroll = false, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Theme.Surface, Margin = Padding.Empty };
    private readonly FlowLayoutPanel agentRows = new() { Dock = DockStyle.Fill, AutoScroll = false, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Theme.Surface, Margin = Padding.Empty };
    private readonly FlowLayoutPanel weaponRows = new() { Dock = DockStyle.Fill, AutoScroll = false, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Theme.Surface, Margin = Padding.Empty };
    private readonly ThemedButton matchPrevious = new() { Text = "前へ", Size = new(76, UiMetrics.CompactButtonHeight), Compact = true };
    private readonly ThemedButton matchNext = new() { Text = "次へ", Size = new(76, UiMetrics.CompactButtonHeight), Compact = true };
    private readonly Label matchPageLabel = NativePageStyle.Label("1 / 1", NativePageStyle.Small, Theme.Muted);
    private readonly ThemedButton roundsPrevious = new() { Text = "前", Size = new(56, 24), Compact = true };
    private readonly ThemedButton roundsNext = new() { Text = "次", Size = new(56, 24), Compact = true };
    private readonly Label roundsPageLabel = NativePageStyle.Label("1 / 1", NativePageStyle.Small, Theme.Muted);
    private readonly ThemedButton mapPrevious = new() { Text = "前", Size = new(56, 22), Compact = true };
    private readonly ThemedButton mapNext = new() { Text = "次", Size = new(56, 22), Compact = true };
    private readonly Label mapPageLabel = NativePageStyle.Label("1 / 1", NativePageStyle.Small, Theme.Muted);
    private readonly ThemedButton agentPrevious = new() { Text = "前", Size = new(56, 22), Compact = true };
    private readonly ThemedButton agentNext = new() { Text = "次", Size = new(56, 22), Compact = true };
    private readonly Label agentPageLabel = NativePageStyle.Label("1 / 1", NativePageStyle.Small, Theme.Muted);
    private readonly ThemedButton weaponPrevious = new() { Text = "前", Size = new(56, 20), Compact = true };
    private readonly ThemedButton weaponNext = new() { Text = "次", Size = new(56, 20), Compact = true };
    // Sub-page switches use the Tab style; the coral fill is kept for actions only.
    private readonly ThemedButton historyTab = new() { Text = "試合履歴", Size = new(144, UiMetrics.ButtonHeight), Compact = true, Variant = ButtonVariant.Tab, Selected = true };
    private readonly ThemedButton careerTab = new() { Text = "成績分析", Size = new(144, UiMetrics.ButtonHeight), Compact = true, Variant = ButtonVariant.Tab };
    private readonly ThemedButton mapTab = new() { Text = "マップ別", Size = new(128, 34), Compact = true, Variant = ButtonVariant.Tab, Selected = true };
    private readonly ThemedButton agentTab = new() { Text = "エージェント別", Size = new(164, 34), Compact = true, Variant = ButtonVariant.Tab };
    private readonly ThemedButton trendTab = new() { Text = "推移", Size = new(96, 34), Compact = true, Variant = ButtonVariant.Tab };
    private readonly MatchTrendChart trendChart = new() { Dock = DockStyle.Fill };
    private readonly Panel trendPage = new() { Dock = DockStyle.Fill, Visible = false };
    private readonly Label todaySummary = NativePageStyle.Label("", NativePageStyle.Small, Theme.Muted);
    private string trendMetric = "ACS";
    private readonly ThemedButton scoreboardTab = new() { Text = "スコアボード", Size = new(156, 34), Compact = true, Variant = ButtonVariant.Tab, Selected = true };
    private readonly ThemedButton roundsTab = new() { Text = "ラウンド別", Size = new(136, 34), Compact = true, Variant = ButtonVariant.Tab };
    private readonly ThemedButton matchAnalysisTab = new() { Text = "ファースト・武器", Size = new(184, 34), Compact = true, Variant = ButtonVariant.Tab };
    private readonly ThemedSelect modeFilter = new(["すべてのモード"]) { AccessibleName = "モードで絞り込み", AccessibleDescription = "試合一覧を選んだモードの試合だけで表示します" };
    private readonly Label competitiveOnlyLabel = NativePageStyle.Label("コンペティティブのみ", NativePageStyle.SmallBold, Theme.Muted);
    // Queue id per modeFilter item; null = all modes, "" = matches whose mode is unknown.
    private string?[] modeFilterIds = [null];
    private string? selectedMode;
    private bool updatingModeFilter;
    private readonly ThemedButton emptyLoginButton = new() { Text = "Riotにログイン", Size = new(190, 44), Compact = true, Primary = true, Visible = false };
    private static Color DrawColor => Theme.IsLight ? Color.FromArgb(132, 82, 0) : Color.FromArgb(235, 192, 106);
    private int wheelDelta;
    private readonly Label weaponPageLabel = NativePageStyle.Label("1 / 1", NativePageStyle.Small, Theme.Muted);
    private readonly Label pageStatus = NativePageStyle.Label("Riotアカウントにログインすると、試合履歴を表示します。", NativePageStyle.Small, Theme.Muted);
    private readonly Label listTitle = NativePageStyle.Label("試合履歴", NativePageStyle.SmallBold);
    private readonly ThemedButton favoritesFilter = new() { Text = "お気に入り", Size = new(130, UiMetrics.ChipHeight), Compact = true, Variant = ButtonVariant.Chip, AccessibleName = "お気に入りの試合だけを表示" };
    private readonly ThemedButton favoriteButton = new() { Text = "☆ お気に入り", Size = new(152, 36), Compact = true, Variant = ButtonVariant.Chip };
    private readonly Label noFavorites = NativePageStyle.Label("お気に入り登録した試合がここに表示されます。", NativePageStyle.Reading, Theme.Muted);
    private readonly Label matchCount = NativePageStyle.Label("0 試合", NativePageStyle.Small, Theme.Muted);
    private readonly Label detailTitle = NativePageStyle.Label("試合を選ぶと、スコアボードとラウンド分析を表示します。", NativePageStyle.Subheading);
    private readonly Label detailSubtitle = NativePageStyle.Label("", NativePageStyle.Small, Theme.Muted);
    private readonly Label scoreLabel = NativePageStyle.Label("—", NativePageStyle.Heading, Theme.Green);
    private readonly Label detailNote = NativePageStyle.Label("", NativePageStyle.Small, Theme.Muted);
    private readonly Label openerInsight = NativePageStyle.Label("ファーストキル / ファーストデス", NativePageStyle.Small, Theme.Muted);
    private readonly Label firstKillValue = NativePageStyle.Label("—", NativePageStyle.Metric, Theme.Text);
    private readonly Label firstDeathValue = NativePageStyle.Label("—", NativePageStyle.Metric, Theme.Text);
    private readonly Label firstKillRateValue = NativePageStyle.Label("—", NativePageStyle.Metric, Theme.Text);
    private readonly Label kdDiffValue = NativePageStyle.Label("—", NativePageStyle.Metric, Theme.Text);
    private readonly ThemedButton refreshButton = new() { Text = "最新に更新", Size = new(140, 40), Compact = true };
    private readonly ThemedButton loadMoreButton = new() { Text = "さらに10試合", Size = new(152, 40), Compact = true };
    private readonly RoundedCard detailCard = NativePageStyle.Card();
    private readonly Panel historyPage = new() { Dock = DockStyle.Fill, BackColor = Theme.Background };
    private readonly Panel historyContent = new() { Dock = DockStyle.Fill, BackColor = Theme.Background };
    private readonly SkeletonScreen historySkeleton = new(SkeletonLayout.MatchHistory) { Dock = DockStyle.Fill, BackColor = Theme.Background, Visible = false };
    private readonly Label historyEmptyText = NativePageStyle.Label("", NativePageStyle.Subheading, Theme.Muted);
    private readonly Panel careerPage = new() { Dock = DockStyle.Fill, BackColor = Theme.Background, Visible = false };
    private readonly Panel analysisChartHost = new() { Dock = DockStyle.Fill, BackColor = Theme.Background };
    private readonly Panel mapChartPage = new() { Dock = DockStyle.Fill, BackColor = Theme.Background };
    private readonly Panel agentChartPage = new() { Dock = DockStyle.Fill, BackColor = Theme.Background, Visible = false };
    private readonly Panel detailPageHost = new() { Dock = DockStyle.Fill, BackColor = Theme.Surface };
    private readonly Panel scoreboardPage = new() { Dock = DockStyle.Fill, BackColor = Theme.Surface };
    private readonly Panel roundsPage = new() { Dock = DockStyle.Fill, BackColor = Theme.Surface, Visible = false };
    private readonly Panel matchAnalysisPage = new() { Dock = DockStyle.Fill, BackColor = Theme.Surface, Visible = false };
    private readonly Label detailEmpty = NativePageStyle.Label("", NativePageStyle.Subheading, Theme.Muted);
    private TableLayoutPanel? detailLayout;
    private IReadOnlyDictionary<string, string> weapons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
    private bool loading;
    private bool shuttingDown;
    private bool favoriteSaving;
    private bool hasMore = true;
    private int nextIndex;
    private int unavailableDetailsCount;
    private int loadGeneration;
    private CancellationTokenSource? activeLoad;
    private Task? activeLoadTask;
    private Task? shutdownTask;
    private readonly MatchHistoryArchive archive = new(Path.Combine(Storage.Folder, "MatchHistory"));
    private readonly HashSet<string> favorites = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan ArchiveSaveTimeout = TimeSpan.FromSeconds(30);
    private const string ArchiveSaveFailedMessage = "このPCへの保存に失敗しました。「最新に更新」で再試行できます。";
    private readonly MatchHistorySaveQueue saveQueue;
    private bool archiveSaveFailed;
    private string archiveSubject = "";
    private Task? archiveLoad;
    private CancellationTokenSource? archiveLoadStop;
    private bool archiveLoaded;
    private bool archiveLoading;
    private bool favoritesOnly;
    private DateTimeOffset latestSync;
    private string HistorySubject => archiveSubject.Length > 0 ? archiveSubject : api.Subject;
    private bool CanSync => api.HasAccountSession && archiveSubject.Equals(api.Subject, StringComparison.OrdinalIgnoreCase) && !archiveLoading;
    private string selectedMatchId = "";
    private IReadOnlyList<MatchAnalytics> orderedMatches = [];
    private MatchAnalytics? detailMatch;
    private MatchAnalytics? weaponMatch;
    private IReadOnlyList<HistoryBreakdownEntry> lastMapEntries = [];
    private IReadOnlyList<HistoryBreakdownEntry> lastAgentEntries = [];
    private IReadOnlyList<PlayerHistoryEntry> lastHistoryRows = [];
    private int matchPageIndex;
    private int roundsPageIndex;
    private int mapPageIndex;
    private int agentPageIndex;
    private int weaponPageIndex;
    private readonly List<Label> scoreboardHeaders = [];
    private readonly Dictionary<PlayerMatchStats, Control[]> scoreboardRows = new(ReferenceEqualityComparer.Instance);
    private (MatchAnalytics Match, int Dpi)? scoreboardContent;
    private int scoreboardLineMode = -1;
    private string scoreboardRenderedColumns = "";
    private int collapsedStatsIndex = -1;
    private int scoreboardMetricCapacity = 7;
    private int scoreboardAvailableWidth;
    private bool scoreboardPageActive = true;
    private int[] visibleScoreboardColumns = [0, 1, 2, 3, 4, 5, 6, 7];
    private readonly ToolTip scoreboardToolTips = new() { ShowAlways = true, InitialDelay = 500 };
    private readonly ThemedButton collapsedStatsButton = new()
    {
        Text = "+1 指標", Size = new(86, UiMetrics.CompactButtonHeight), Compact = true,
        Variant = ButtonVariant.Chip, Visible = false, AccessibleName = "表示するスコア指標を切り替え"
    };
    private int scoreboardHeight = -1;
    // -1 keeps the default team / ACS order until a sortable header is clicked.
    private int scoreboardSortColumn = -1;
    private bool scoreboardSortDescending = true;
    private (MatchAnalytics? Match, Size Size, int Page, int Dpi)? roundsRenderKey;
    private (IReadOnlyList<HistoryBreakdownEntry> Entries, IReadOnlyList<PlayerHistoryEntry> Rows, Size Size, int Page, int Dpi)? mapRenderKey, agentRenderKey;
    private (MatchAnalytics? Match, Size Size, int Page, int Dpi)? weaponRenderKey;

    public MatchHistoryControl(RiotApi api, IEnumerable<Agent> agents)
    {
        this.api = api;
        saveQueue = new MatchHistorySaveQueue(archive);
        this.agents = AgentLookup(agents);

        BackColor = Theme.Background;
        BuildInterface();
        BindHistoryActions();
        BindPagingActions();
        Disposed += (_, _) =>
        {
            shuttingDown = true;
            scoreboardToolTips.Dispose();
            activeLoad?.Cancel();
            archiveLoadStop?.Cancel();
            _ = CompleteDisposalAsync();
        };
        RenderEmptyState("Riotアカウントにログインすると、試合履歴を表示します。");
    }

    private void BindHistoryActions()
    {
        refreshButton.Click += async (_, _) => await RunHistoryAction(RefreshHistoryAsync);
        loadMoreButton.Click += async (_, _) => await RunHistoryAction(() => LoadNextPageAsync());
        favoritesFilter.Click += (_, _) =>
        {
            favoritesOnly = !favoritesOnly;
            favoritesFilter.Selected = favoritesOnly;
            matchPageIndex = 0;
            RenderHistorySelection();
        };
        modeFilter.SelectedIndexChanged += (_, _) =>
        {
            if (updatingModeFilter) return;
            var index = modeFilter.SelectedIndex;
            selectedMode = index >= 0 && index < modeFilterIds.Length ? modeFilterIds[index] : null;
            matchPageIndex = mapPageIndex = agentPageIndex = 0;
            if (matches.Count > 0) RenderHistory();
        };
        emptyLoginButton.Click += (_, _) => RequestLogin();
        historyEmptyText.VisibleChanged += (_, _) => UpdateEmptyLogin();
        historyPage.Resize += (_, _) => PlaceEmptyLogin();
        favoriteButton.Click += (_, _) => ToggleFavorite();
        matchRows.SizeChanged += (_, _) => RenderMatchList(orderedMatches);
    }

    private async Task RunHistoryAction(Func<Task> action)
    {
        try { await action(); }
        catch (Exception ex)
        {
            if (!IsDisposed && !Disposing && !shuttingDown)
                ShowHistoryLoadError(ex, "試合履歴を表示できませんでした。最新に更新してください。");
        }
    }

    private void BindPagingActions()
    {
        AttachWheelPaging(matchRows, step => ChangeMatchPage(step));
        AttachWheelPaging(rounds, step => { roundsPageIndex = Math.Max(0, roundsPageIndex + step); RenderRoundPage(); });
        AttachWheelPaging(mapRows, step => { mapPageIndex = Math.Max(0, mapPageIndex + step); RenderBreakdowns(mapRows, lastMapEntries, lastHistoryRows, map: true); });
        AttachWheelPaging(agentRows, step => { agentPageIndex = Math.Max(0, agentPageIndex + step); RenderBreakdowns(agentRows, lastAgentEntries, lastHistoryRows, map: false); });
        AttachWheelPaging(weaponRows, step => { weaponPageIndex = Math.Max(0, weaponPageIndex + step); RenderWeaponBreakdown(); });
        matchPrevious.Click += (_, _) => { if (matchPageIndex > 0) { matchPageIndex--; RenderMatchList(orderedMatches); } };
        matchNext.Click += (_, _) => { matchPageIndex++; RenderMatchList(orderedMatches); };
        roundsPrevious.Click += (_, _) => { if (roundsPageIndex > 0) { roundsPageIndex--; RenderRoundPage(); } };
        roundsNext.Click += (_, _) => { roundsPageIndex++; RenderRoundPage(); };
        mapPrevious.Click += (_, _) => { if (mapPageIndex > 0) { mapPageIndex--; RenderBreakdowns(mapRows, lastMapEntries, lastHistoryRows, map: true); } };
        mapNext.Click += (_, _) => { mapPageIndex++; RenderBreakdowns(mapRows, lastMapEntries, lastHistoryRows, map: true); };
        agentPrevious.Click += (_, _) => { if (agentPageIndex > 0) { agentPageIndex--; RenderBreakdowns(agentRows, lastAgentEntries, lastHistoryRows, map: false); } };
        agentNext.Click += (_, _) => { agentPageIndex++; RenderBreakdowns(agentRows, lastAgentEntries, lastHistoryRows, map: false); };
        weaponPrevious.Click += (_, _) => { if (weaponPageIndex > 0) { weaponPageIndex--; RenderWeaponBreakdown(); } };
        weaponNext.Click += (_, _) => { weaponPageIndex++; RenderWeaponBreakdown(); };
    }

    private const string SignedOutEmptyText = "Riotアカウントにログインすると、試合履歴と分析を表示します。";
    private const string SignedOutHeaderHint = "\n上部の「Riotにログイン」から接続してください。";
    private EventHandler? loginRequested;

    /// <summary>Raised when the page's own login button is pressed; the shell runs the Riot login.</summary>
    public event EventHandler? LoginRequested
    {
        // The empty state is first drawn before the shell subscribes, so refresh it once a handler exists.
        add { loginRequested += value; UpdateEmptyLogin(); }
        remove { loginRequested -= value; UpdateEmptyLogin(); }
    }

    private void RequestLogin()
    {
        if (api.HasAccountSession || loginRequested is null) return;
        SetStatus("ブラウザーが開いたらRiotにログインしてください。", error: false);
        loginRequested.Invoke(this, EventArgs.Empty);
    }

    // The empty state offers a direct login only when signing in is what is missing.
    private void UpdateEmptyLogin()
    {
        var show = historyEmptyText.Visible && !api.HasAccountSession && loginRequested is not null;
        if (show && historyEmptyText.Text == SignedOutEmptyText + SignedOutHeaderHint) historyEmptyText.Text = SignedOutEmptyText;
        emptyLoginButton.Visible = show;
        if (show) { emptyLoginButton.BringToFront(); PlaceEmptyLogin(); }
    }

    private void PlaceEmptyLogin()
    {
        if (!emptyLoginButton.Visible) return;
        var textHeight = NativePageStyle.TextBlockHeight(this, historyEmptyText.Text, historyEmptyText.Font,
            Math.Max(1, historyPage.ClientSize.Width - UiMetrics.Px(this, 40)));
        emptyLoginButton.Size = new Size(UiMetrics.Px(this, 190), UiMetrics.Px(this, 44));
        emptyLoginButton.Location = new Point(
            Math.Max(0, (historyPage.ClientSize.Width - emptyLoginButton.Width) / 2),
            Math.Min(Math.Max(0, historyPage.ClientSize.Height - emptyLoginButton.Height),
                historyPage.ClientSize.Height / 2 + textHeight / 2 + UiMetrics.Px(this, 16)));
    }

    /// <summary>F5 from the shell: fetch the latest matches when that is currently possible.</summary>
    public async Task RefreshFromShortcutAsync()
    {
        if (IsDisposed || !Visible || loading || !refreshButton.Enabled) return;
        try { await RefreshHistoryAsync(); }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { /* The page is closing. */ }
        catch (ObjectDisposedException) { /* The page closed while the refresh was queued. */ }
    }

    /// <summary>
    /// Turns mouse-wheel input over a paged list (or any of its children, which pass unhandled wheel
    /// messages up to their parent) into page changes: one notch forward or back per wheel detent.
    /// </summary>
    private void AttachWheelPaging(Control host, Action<int> page)
    {
        host.MouseWheel += (_, e) =>
        {
            if (e is HandledMouseEventArgs handled) handled.Handled = true;
            wheelDelta += e.Delta;
            var steps = wheelDelta / 120;
            if (steps == 0) return;
            wheelDelta -= steps * 120;
            page(-steps);
        };
    }

    private void ChangeMatchPage(int step)
    {
        var next = Math.Max(0, matchPageIndex + step);
        if (next == matchPageIndex) return;
        matchPageIndex = next;
        RenderMatchList(orderedMatches);
    }

    // Up/Down move the selected match (turning pages as needed), PageUp/PageDown change the list page.
    // Keys typed into the mode selector or other inputs keep their own meaning.
    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        if (historyPage.Visible && historyContent.Visible && orderedMatches.Count > 0 &&
            FindFocusedLeaf(this) is not (ThemedSelect or TextBoxBase))
        {
            switch (keyData)
            {
                case Keys.Down: case Keys.Up:
                    MoveMatchSelection(keyData == Keys.Down ? 1 : -1);
                    return true;
                case Keys.PageDown: case Keys.PageUp:
                    ChangeMatchPage(keyData == Keys.PageDown ? 1 : -1);
                    return true;
            }
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }

    private static Control? FindFocusedLeaf(ContainerControl container)
    {
        Control? active = container.ActiveControl;
        while (active is ContainerControl nested && nested.ActiveControl is { } inner) active = inner;
        return active;
    }

    private void MoveMatchSelection(int step)
    {
        var index = -1;
        for (var i = 0; i < orderedMatches.Count; i++)
            if (orderedMatches[i].Info.MatchId.Equals(selectedMatchId, StringComparison.OrdinalIgnoreCase)) { index = i; break; }
        var next = Math.Clamp(index < 0 ? 0 : index + step, 0, orderedMatches.Count - 1);
        if (next == index) return;
        var match = orderedMatches[next];
        selectedMatchId = match.Info.MatchId;
        var pageSize = MatchListPageSize();
        matchPageIndex = next / Math.Max(1, pageSize);
        RenderMatchList(orderedMatches);
        RenderMatchDetail(match);
    }

    private int MatchListPageSize() => Math.Max(1, Math.Min(PageSize, matchRows.ClientSize.Height / Math.Max(1, MatchListMinimumRowHeight())));

    private bool MatchesMode(MatchAnalytics match) =>
        selectedMode is null || MatchAnalyticsEngine.DisplayQueueId(match.Match).Equals(selectedMode, StringComparison.OrdinalIgnoreCase);

    // Rebuilds the mode list from the loaded matches (most played first), keeping the current choice.
    private void UpdateModeFilterItems()
    {
        var modes = matches.GroupBy(match => MatchAnalyticsEngine.DisplayQueueId(match.Match), StringComparer.OrdinalIgnoreCase)
            .OrderByDescending(group => group.Count()).ThenBy(group => group.Key, StringComparer.Ordinal)
            .Select(group => (Id: group.Key, Count: group.Count())).ToArray();
        if (selectedMode is not null && !modes.Any(mode => mode.Id.Equals(selectedMode, StringComparison.OrdinalIgnoreCase)))
            selectedMode = null;
        modeFilterIds = [null, .. modes.Select(mode => (string?)mode.Id)];
        var labels = new List<string> { $"すべてのモード（{matches.Count}）" };
        labels.AddRange(modes.Select(mode => $"{QueueName(mode.Id)}（{mode.Count}）"));
        var selectedIndex = selectedMode is null ? 0 : Array.FindIndex(modeFilterIds, id => id is not null && id.Equals(selectedMode, StringComparison.OrdinalIgnoreCase));
        updatingModeFilter = true;
        try
        {
            modeFilter.SetItems(labels, labels[Math.Max(0, selectedIndex)]);
            // Two queue ids can share a display name, so select by position rather than by label.
            modeFilter.SelectedIndex = Math.Max(0, selectedIndex);
        }
        finally { updatingModeFilter = false; }
        modeFilter.Enabled = matches.Count > 0;
    }

    internal Task ShutdownAsync()
    {
        shutdownTask ??= ShutdownCoreAsync();
        return shutdownTask;
    }

    private async Task ShutdownCoreAsync()
    {
        shuttingDown = true;
        if (!IsDisposed) Enabled = false;
        var cancelActive = activeLoad?.CancelAsync() ?? Task.CompletedTask;
        var cancelArchive = archiveLoadStop?.CancelAsync() ?? Task.CompletedTask;
        await Task.WhenAll(cancelActive, cancelArchive);
        if (activeLoadTask is { } activeTask)
        {
            try { await activeTask; }
            catch (Exception) { /* Shutdown continues after a failed or canceled load. */ }
        }
        if (archiveLoad is { } archiveTask)
        {
            try { await archiveTask; }
            catch (Exception) { /* Shutdown continues after a failed or canceled archive load. */ }
        }
        if (saveQueue.PendingCount > 0 && Guid.TryParse(archiveSubject, out _))
        {
            try { await PersistHistoryAsync(archiveSubject, weapons, loadGeneration); }
            catch (Exception) { /* Failed writes stay queued; continue shutdown. */ }
        }
        await DrainArchiveOperationsAsync();
        await saveQueue.WaitForIdleAsync();
        if (!lifetime.IsCancellationRequested) await lifetime.CancelAsync();
    }

    private async Task CompleteDisposalAsync()
    {
        try { await ShutdownAsync().ConfigureAwait(false); }
        catch (Exception) { /* Disposal must release resources even if shutdown fails. */ }
        assets.Dispose();
        lifetime.Dispose();
    }

    private void TrackArchiveOperation(Task operation)
    {
        lock (archiveOperationsGate) archiveOperations.Add(operation);
        _ = operation.ContinueWith(completed =>
        {
            lock (archiveOperationsGate) archiveOperations.Remove(completed);
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
    }

    private async Task DrainArchiveOperationsAsync()
    {
        while (true)
        {
            Task[] pending;
            lock (archiveOperationsGate) pending = archiveOperations.ToArray();
            if (pending.Length == 0) return;
            try { await Task.WhenAll(pending); }
            catch (Exception) { /* A failed operation must not prevent the rest from draining. */ }
        }
    }

    private static Dictionary<string, Agent> AgentLookup(IEnumerable<Agent> agents) =>
        agents.Where(a => Guid.TryParse(a.Id, out _))
            .GroupBy(a => a.Id, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.First(), StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// Replaces the agent names used for display. Agents missing from <paramref name="catalogue"/> keep
    /// their previous names. When history is already on screen, only the views that show agent names are
    /// redrawn; the selected match, page positions and scoreboard sort order are preserved.
    /// </summary>
    public void UpdateAgents(IEnumerable<Agent> catalogue)
    {
        if (IsDisposed || shuttingDown) return;
        if (InvokeRequired) { BeginInvoke(() => UpdateAgents(catalogue)); return; }
        using var redraw = PageRedrawScope.Begin(this);
        var updated = AgentLookup(catalogue);
        foreach (var (id, agent) in agents) updated.TryAdd(id, agent);
        agents = updated;

        if (visibleMatchRows.Count > 0)
        {
            // Rows cache their labels; rebuild the visible ones on the current page.
            foreach (var row in visibleMatchRows.Values) row.Dispose();
            visibleMatchRows.Clear();
            RenderMatchList(orderedMatches);
        }
        if (detailMatch is { } match && !detailEmpty.Visible)
        {
            detailTitle.Text = DetailTitle(match);
            if (scoreboardContent != null)
            {
                scoreboardContent = null;
                RenderScoreboard(match);
            }
        }
        if (agentRenderKey != null)
        {
            agentRenderKey = null;
            RenderBreakdowns(agentRows, lastAgentEntries, lastHistoryRows, map: false);
        }
    }

    public async Task ActivatePageAsync(bool silent = false)
    {
        if (IsDisposed || shuttingDown) return;
        UpdateEmptyLogin();
        var subject = api.HasAccountSession ? api.Subject : archiveSubject.Length > 0 ? archiveSubject : archive.LastSubject();
        if (api.HasAccountSession) subject = api.Subject;
        if (!archiveSubject.Equals(subject, StringComparison.OrdinalIgnoreCase))
        {
            ResetSession();
            archiveSubject = subject;
        }
        if (subject.Length == 0) return;
        var generation = loadGeneration;
        if (!archiveLoaded)
        {
            if (archiveLoad is null)
            {
                var operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
                archiveLoadStop = operation;
                var loadTask = LoadArchiveAsync(subject, generation, operation);
                archiveLoad = loadTask;
                TrackArchiveOperation(loadTask);
            }
            if (archiveLoad is { } archiveTask) await archiveTask;
        }
        if (generation != loadGeneration || IsDisposed || !CanSync || loading ||
            DateTimeOffset.UtcNow - latestSync < TimeSpan.FromSeconds(30)) return;
        await LoadNextPageAsync(refresh: true, silent: silent);
    }

    private async Task LoadArchiveAsync(string subject, int generation, CancellationTokenSource operation)
    {
        var token = operation.Token;
        var succeeded = false;
        archiveLoading = true;
        UpdateHistoryButtons();
        SetStatus("ローカル試合データを参照中", error: false);
        if (matches.Count == 0)
        {
            historyContent.Visible = false;
            historyEmptyText.Visible = false;
            historySkeleton.BringToFront();
            historySkeleton.Loading = true;
        }
        await Task.Yield();
        try
        {
            var saved = await Task.Run(() => archive.Load(subject, token), token);
            if (generation != loadGeneration || IsDisposed) return;
            foreach (var match in saved.Matches) AddMatch(match);
            favorites.UnionWith(saved.Favorites);
            weapons = saved.Weapons;
            if (matches.Count > 0) RenderHistory();
            var message = matches.Count > 0 && !api.HasAccountSession
                ? "このPCに保存した履歴を表示中 · ログインすると最新の試合を取得できます" : "";
            if (saved.UnreadableCount > 0) message = "一部の保存データを読み込めませんでした。";
            SetStatus(message, error: saved.UnreadableCount > 0);
            archiveLoaded = true;
            succeeded = true;
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { /* The account changed or the page is closing. */ }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            if (generation == loadGeneration && !IsDisposed)
                ShowHistoryLoadError(ex, "このPCに保存した試合履歴を読み込めませんでした。");
        }
        finally
        {
            if (generation == loadGeneration && !IsDisposed)
            {
                archiveLoading = false;
                UpdateHistoryButtons();
                if (matches.Count == 0 && !api.HasAccountSession) RenderEmptyState("Riotアカウントにログインすると、最新の試合を取得できます。");
            }
            if (ReferenceEquals(archiveLoadStop, operation))
            {
                archiveLoadStop = null;
                archiveLoad = null;
                if (!succeeded) archiveLoaded = false;
            }
            operation.Dispose();
        }
    }

    internal void ReportActivationError(string message)
    {
        SetStatus(message, error: true);
        if (matches.Count == 0)
        {
            historySkeleton.Loading = false;
            historyContent.Visible = false;
            historyEmptyText.Text = message;
            historyEmptyText.Visible = true;
        }
    }

    internal void ShowCareerForPreview() => ShowMainPage(history: false);
    internal void ShowTrendForPreview() { ShowMainPage(history: false); ShowTrend(); }
    internal void ShowNextHistoryPageForPreview()
    {
        if (!matchNext.Enabled) return;
        matchPageIndex++;
        RenderMatchList(orderedMatches);
    }

    public void ResetSession()
    {
        using var redraw = PageRedrawScope.Begin(this);
        loadGeneration++;
        activeLoad?.Cancel();
        activeLoad = null;
        archiveLoadStop?.Cancel();
        archiveLoadStop = null;
        archiveLoaded = false;
        loading = false;
        favoriteSaving = false;
        loadMoreButton.Text = "さらに読み込む";
        archiveSubject = "";
        archiveLoad = null;
        archiveLoading = false;
        latestSync = default;
        matches.Clear();
        matchPositions.Clear();
        favorites.Clear();
        // Cancels the previous account's in-flight save and drops its queue so nothing leaks into the new session.
        saveQueue.Reset();
        archiveSaveFailed = false;
        favoritesOnly = false;
        favoritesFilter.Selected = false;
        selectedMode = null;
        UpdateModeFilterItems();
        refreshButton.Text = "最新に更新";
        lastMapEntries = [];
        lastAgentEntries = [];
        lastHistoryRows = [];
        nextIndex = 0;
        unavailableDetailsCount = 0;
        hasMore = true;
        selectedMatchId = "";
        RenderEmptyState("Riotアカウントにログインすると、試合履歴を表示します。");
    }

    private void BuildInterface()
    {
        var root = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            BackColor = Theme.Background,
            Padding = Padding.Empty,
            ColumnCount = 1,
            RowCount = 3,
            AutoScroll = false,
            Margin = Padding.Empty
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var headerRow = new RowStyle(SizeType.Absolute, UiMetrics.Px(this, 50));
        var navigationRow = new RowStyle(SizeType.Absolute, UiMetrics.Px(this, UiMetrics.ButtonHeight + UiMetrics.ControlGap));
        root.RowStyles.Add(headerRow);
        root.RowStyles.Add(navigationRow);
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        root.Controls.Add(BuildHeader(), 0, 0);
        void ResizeHeader()
        {
            // The page name is already shown by the selected navigation tab and the window caption, so the
            // header row holds the mode filter, the status line and the actions on a single line.
            var statusHeight = NativePageStyle.TextHeight(this, pageStatus.Font, verticalPadding: 4);
            var height = Math.Max(Math.Max(refreshButton.Height, loadMoreButton.Height), Math.Max(modeFilter.Height, statusHeight)) + UiMetrics.Px(this, UiMetrics.ControlGap);
            if (Math.Abs(headerRow.Height - height) >= .5f) headerRow.Height = height;
        }
        root.DpiChangedAfterParent += (_, _) => ResizeHeader();
        root.Layout += (_, _) => ResizeHeader();
        modeFilter.SizeChanged += (_, _) => ResizeHeader();
        refreshButton.SizeChanged += (_, _) => ResizeHeader();
        loadMoreButton.SizeChanged += (_, _) => ResizeHeader();
        ResizeHeader();
        root.Controls.Add(BuildMainNavigation(), 0, 1);
        void ResizeNavigation()
        {
            var height = Math.Max(historyTab.Height, careerTab.Height) + UiMetrics.Px(this, UiMetrics.ControlGap);
            if (Math.Abs(navigationRow.Height - height) >= .5f) navigationRow.Height = height;
        }
        // Keep the two gaps equal after WinForms applies its DPI / compact scaling.
        root.Layout += (_, _) => ResizeNavigation();
        historyTab.SizeChanged += (_, _) => ResizeNavigation();
        careerTab.SizeChanged += (_, _) => ResizeNavigation();
        ResizeNavigation();
        historyContent.Controls.Add(BuildMainContent());
        historyEmptyText.AutoSize = false;
        historyEmptyText.AutoEllipsis = false;
        historyEmptyText.TextAlign = ContentAlignment.MiddleCenter;
        historyEmptyText.Dock = DockStyle.Fill;
        historyPage.Controls.Add(historyContent);
        historyPage.Controls.Add(historyEmptyText);
        historyPage.Controls.Add(historySkeleton);
        historyPage.Controls.Add(emptyLoginButton);
        historySkeleton.BringToFront();
        careerPage.Controls.Add(BuildCareerPage());
        var pageHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Background, Margin = Padding.Empty };
        pageHost.Controls.Add(careerPage);
        pageHost.Controls.Add(historyPage);
        root.Controls.Add(pageHost, 0, 2);
        pageStatus.Font = NativePageStyle.Reading;
        ResizeHeader();
        Controls.Add(root);
        scoreboard.SizeChanged += (_, _) =>
        {
            UpdateScoreboardColumnWidths();
            if (detailMatch is not null) RenderScoreboard(detailMatch);
        };
        mapRows.SizeChanged += (_, _) => { ResizeAnalysisRows(mapRows, horizontalPadding: 24, minimumWidth: 200); if (matches.Count > 0) RenderBreakdowns(mapRows, lastMapEntries, lastHistoryRows, map: true); };
        agentRows.SizeChanged += (_, _) => { ResizeAnalysisRows(agentRows, horizontalPadding: 24, minimumWidth: 200); if (matches.Count > 0) RenderBreakdowns(agentRows, lastAgentEntries, lastHistoryRows, map: false); };
        weaponRows.SizeChanged += (_, _) => { ResizeAnalysisRows(weaponRows, horizontalPadding: 20, minimumWidth: 140); RenderWeaponBreakdown(); };
        rounds.SizeChanged += (_, _) => RenderRoundPage();
    }

    private Control BuildMainNavigation()
    {
        var tabs = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Theme.Background,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        historyTab.Margin = new Padding(0, 0, UiMetrics.Px(this, UiMetrics.ControlGap), 0);
        careerTab.Margin = Padding.Empty;
        tabs.Controls.Add(historyTab);
        tabs.Controls.Add(careerTab);
        historyTab.Click += (_, _) => ShowMainPage(history: true);
        careerTab.Click += (_, _) => ShowMainPage(history: false);
        return tabs;
    }

    private void ShowMainPage(bool history)
    {
        using var redraw = PageRedrawScope.Begin(historyPage.Parent!);
        historyPage.Visible = history;
        careerPage.Visible = !history;
        modeFilter.Visible = history;
        competitiveOnlyLabel.Visible = !history;
        historyTab.Selected = history;
        careerTab.Selected = !history;
        historyPage.Parent?.PerformLayout();
        (history ? historyPage : careerPage).PerformLayout();
        if (!history && !trendTab.Selected) ShowAnalysisChart(mapTab.Selected);
    }

    private Control BuildCareerPage()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 4,
            BackColor = Theme.Background,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var metricsRow = new RowStyle(SizeType.Absolute, 144);
        layout.RowStyles.Add(metricsRow);
        var todayRow = new RowStyle(SizeType.Absolute, 0);
        layout.RowStyles.Add(todayRow);
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(BuildMetrics(), 0, 0);
        void ResizeMetricsRow()
        {
            var (value, caption) = metricLabels[0];
            var card = value.Parent!;
            var inset = UiMetrics.Px(card, UiMetrics.ControlGap);
            var gap = UiMetrics.Px(card, UiMetrics.TightGap);
            var cardHeight = NativePageStyle.TextHeight(card, value.Font, verticalPadding: 2)
                + NativePageStyle.TextHeight(card, caption.Font, verticalPadding: 2)
                + gap + inset * 2;
            var height = Math.Max(UiMetrics.Px(layout, 206), 2 * (cardHeight + card.Margin.Vertical));
            if (Math.Abs(metricsRow.Height - height) >= .5f) metricsRow.Height = height;
        }
        layout.DpiChangedAfterParent += (_, _) => ResizeMetricsRow();
        metricLabels[0].Value.FontChanged += (_, _) => ResizeMetricsRow();
        metricLabels[0].Caption.FontChanged += (_, _) => ResizeMetricsRow();
        ResizeMetricsRow();

        var tabs = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Theme.Background,
            Margin = new Padding(0, 1, 0, 3),
            Padding = Padding.Empty
        };
        mapTab.Margin = new Padding(0, 0, 6, 0);
        agentTab.Margin = new Padding(0, 0, 6, 0);
        trendTab.Margin = Padding.Empty;
        tabs.Controls.Add(mapTab);
        tabs.Controls.Add(agentTab);
        tabs.Controls.Add(trendTab);
        todaySummary.Dock = DockStyle.Fill;
        todaySummary.AutoSize = false;
        todaySummary.TextChanged += (_, _) => todayRow.Height = todaySummary.Text.Length == 0 ? 0 : NativePageStyle.TextHeight(layout, todaySummary.Font, verticalPadding: UiMetrics.TightGap);

        var mapCard = BreakdownCard("MAP PERFORMANCE", mapRows, mapPrevious, mapPageLabel, mapNext);
        var agentCard = BreakdownCard("AGENT PERFORMANCE", agentRows, agentPrevious, agentPageLabel, agentNext);
        mapChartPage.Controls.Add(mapCard);
        agentChartPage.Controls.Add(agentCard);
        analysisChartHost.Controls.Add(agentChartPage);
        analysisChartHost.Controls.Add(mapChartPage);
        var trendCard = NativePageStyle.Card();
        trendCard.Dock = DockStyle.Fill;
        trendCard.Padding = new Padding(UiMetrics.Px(this, UiMetrics.ContentGap));
        var metricBar = new FlowLayoutPanel { Dock = DockStyle.Top, Height = UiMetrics.Px(this, 44), WrapContents = false, AutoScroll = true, BackColor = Theme.Surface };
        foreach (var name in new[] { "ACS", "ADR", "HS%", "K/D", "RR推移" })
        {
            var chip = new ThemedButton { Text = name, Size = new Size(UiMetrics.Px(this, 92), UiMetrics.Px(this, 34)), Compact = true, Variant = ButtonVariant.Chip, Selected = name == trendMetric };
            chip.Click += (_, _) => { trendMetric = name; foreach (Control control in metricBar.Controls) if (control is ThemedButton button) button.Selected = button == chip; UpdateTrend(); };
            metricBar.Controls.Add(chip);
        }
        trendChart.MapName = MapName;
        trendChart.RankName = RankName;
        trendCard.Controls.Add(trendChart); trendCard.Controls.Add(metricBar);
        trendPage.Controls.Add(trendCard);
        analysisChartHost.Controls.Add(trendPage);
        mapTab.Click += (_, _) => ShowAnalysisChart(map: true);
        agentTab.Click += (_, _) => ShowAnalysisChart(map: false);
        trendTab.Click += (_, _) => ShowTrend();
        layout.Controls.Add(todaySummary, 0, 1);
        layout.Controls.Add(tabs, 0, 2);
        layout.Controls.Add(analysisChartHost, 0, 3);
        return layout;
    }

    private void ShowAnalysisChart(bool map)
    {
        using var redraw = PageRedrawScope.Begin(analysisChartHost);
        mapChartPage.Visible = map;
        agentChartPage.Visible = !map;
        trendPage.Visible = false;
        trendTab.Selected = false;
        mapTab.Selected = map;
        agentTab.Selected = !map;
        analysisChartHost.PerformLayout();
        if (map) mapChartPage.PerformLayout(); else agentChartPage.PerformLayout();
        if (matches.Count == 0) return;
        if (map) RenderBreakdowns(mapRows, lastMapEntries, lastHistoryRows, map: true);
        else RenderBreakdowns(agentRows, lastAgentEntries, lastHistoryRows, map: false);
    }

    private void ShowTrend()
    {
        mapChartPage.Visible = agentChartPage.Visible = false;
        mapTab.Selected = agentTab.Selected = false;
        trendTab.Selected = true;
        trendPage.Visible = true;
        trendPage.BringToFront();
        UpdateTrend();
    }

    private void UpdateTrend()
    {
        trendChart.Metric = trendMetric;
        trendChart.SetPoints(MatchAnalyticsEngine.Trend(MatchAnalyticsEngine.CompetitiveOnly(matches), HistorySubject, trendMetric));
    }

    private Control BuildHeader()
    {
        var header = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Background, Margin = Padding.Empty };
        modeFilter.Size = new Size(250, UiMetrics.ButtonHeight);
        modeFilter.Enabled = false;
        var actions = new FlowLayoutPanel { AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.LeftToRight, Anchor = AnchorStyles.Top | AnchorStyles.Right, BackColor = Theme.Background, Margin = Padding.Empty, Padding = Padding.Empty };
        refreshButton.Margin = new Padding(0, 0, UiMetrics.Px(this, UiMetrics.ControlGap), 0);
        refreshButton.AccessibleDescription = "F5";
        loadMoreButton.Margin = Padding.Empty;
        actions.Controls.Add(refreshButton);
        actions.Controls.Add(loadMoreButton);
        pageStatus.AutoEllipsis = true;
        competitiveOnlyLabel.AutoSize = false;
        competitiveOnlyLabel.Visible = false;
        competitiveOnlyLabel.TextAlign = ContentAlignment.MiddleLeft;
        header.Controls.Add(modeFilter); header.Controls.Add(competitiveOnlyLabel); header.Controls.Add(actions); header.Controls.Add(pageStatus);
        void Place()
        {
            var s = new Func<int, int>(value => UiMetrics.Px(header, value));
            actions.PerformLayout();
            var actionsWidth = Math.Max(actions.Width, actions.GetPreferredSize(Size.Empty).Width);
            var actionsLeft = Math.Max(0, header.ClientSize.Width - actionsWidth);
            var contentHeight = Math.Max(0, header.ClientSize.Height - s(UiMetrics.ControlGap));
            modeFilter.Location = new Point(0, Math.Max(0, contentHeight - modeFilter.Height));
            competitiveOnlyLabel.Bounds = modeFilter.Bounds;
            var statusLeft = modeFilter.Right + s(UiMetrics.ContentGap);
            pageStatus.Bounds = new Rectangle(statusLeft, 0, Math.Max(0, actionsLeft - s(UiMetrics.ContentGap) - statusLeft), header.ClientSize.Height);
            actions.Location = new Point(actionsLeft, Math.Max(0, contentHeight - actions.Height));
        }
        header.Resize += (_, _) => Place();
        pageStatus.TextChanged += (_, _) => Place();
        Place();
        return header;
    }

    private Control BuildMetrics()
    {
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 2, BackColor = Theme.Background, Margin = Padding.Empty, Padding = Padding.Empty };
        for (var i = 0; i < 4; i++) grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        for (var i = 0; i < 2; i++) grid.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        for (var i = 0; i < 8; i++)
        {
            var card = NativePageStyle.Card();
            var gap = UiMetrics.Px(this, UiMetrics.TightGap);
            // The navigation row already reserves the top gap for the first row of cards.
            card.Margin = new Padding(gap, i < 4 ? 0 : gap, gap, gap);
            card.Padding = new Padding(UiMetrics.Px(this, UiMetrics.ControlGap));
            var value = NativePageStyle.Label("—", NativePageStyle.Metric);
            var caption = NativePageStyle.Label("", NativePageStyle.Small, Theme.Muted);
            card.Controls.Add(value); card.Controls.Add(caption);
            void LayoutMetric()
            {
                var inset = UiMetrics.Px(card, UiMetrics.ControlGap);
                var width = Math.Max(UiMetrics.Px(card, 20), card.ClientSize.Width - inset * 2);
                var valueHeight = NativePageStyle.TextHeight(card, value.Font, verticalPadding: 2);
                var captionHeight = NativePageStyle.TextHeight(card, caption.Font, verticalPadding: 2);
                var top = Math.Max(0, (card.ClientSize.Height - valueHeight - captionHeight - UiMetrics.Px(card, UiMetrics.TightGap)) / 2);
                value.Bounds = new Rectangle(inset, top, width, valueHeight);
                caption.Bounds = new Rectangle(inset, value.Bottom + UiMetrics.Px(card, UiMetrics.TightGap), width, captionHeight);
            }
            card.Resize += (_, _) => LayoutMetric();
            value.FontChanged += (_, _) => LayoutMetric();
            caption.FontChanged += (_, _) => LayoutMetric();
            grid.Controls.Add(card, i % 4, i / 4);
            metricLabels.Add((value, caption));
        }
        SetMetrics([("—", "勝敗"), ("—", "勝率"), ("—", "平均ACS"), ("—", "通算K/D"), ("—", "平均ADR"), ("—", "平均HS%"), ("—", "直近5戦"), ("—", "直近10戦")]);
        return grid;
    }

    private Control BuildMainContent()
    {
        var split = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Theme.Background, Margin = Padding.Empty };
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 0));
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));

        var listCard = NativePageStyle.Card();
        var splitGap = UiMetrics.Px(this, UiMetrics.ContentGap);
        listCard.Margin = new Padding(0, 0, splitGap, 0);
        listCard.Padding = new Padding(UiMetrics.Px(this, UiMetrics.ContentGap));
        var headerBottomGap = UiMetrics.Px(this, UiMetrics.TightGap);
        var listHeader = new TableLayoutPanel
        {
            Dock = DockStyle.Top, Height = UiMetrics.Px(this, UiMetrics.ButtonHeight) + headerBottomGap, ColumnCount = 3, RowCount = 1,
            BackColor = Theme.Surface, Margin = Padding.Empty, Padding = new Padding(0, 0, 0, headerBottomGap)
        };
        listHeader.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        listHeader.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        listHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, UiMetrics.Px(this, 130)));
        listHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        listTitle.AutoSize = true;
        listTitle.Dock = DockStyle.Fill; matchCount.Dock = DockStyle.Fill; matchCount.TextAlign = ContentAlignment.MiddleRight;
        favoritesFilter.Margin = new Padding(UiMetrics.Px(this, UiMetrics.TightGap), 0, UiMetrics.Px(this, UiMetrics.TightGap), 0);
        favoritesFilter.Dock = DockStyle.Fill;
        listHeader.Controls.Add(listTitle, 0, 0);
        listHeader.Controls.Add(favoritesFilter, 1, 0);
        listHeader.Controls.Add(matchCount, 2, 0);
        var listFooter = new Panel { Dock = DockStyle.Bottom, Height = UiMetrics.Px(this, 36), BackColor = Theme.Surface, Padding = new Padding(0, UiMetrics.Px(this, 2), 0, 0) };
        matchPrevious.Size = new Size(UiMetrics.Px(this, 76), UiMetrics.Px(this, 30));
        matchNext.Size = matchPrevious.Size;
        matchPrevious.Location = Point.Empty;
        matchNext.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        matchPageLabel.TextAlign = ContentAlignment.MiddleCenter;
        void PlaceMatchFooter()
        {
        matchPrevious.Size = matchNext.Size = new Size(UiMetrics.Px(this, 76), UiMetrics.Px(this, UiMetrics.CompactButtonHeight));
            matchPrevious.Location = new Point(0, 0);
            matchNext.Location = new Point(Math.Max(0, listFooter.ClientSize.Width - matchNext.Width), 0);
            var gap = UiMetrics.Px(this, UiMetrics.ControlGap);
            matchPageLabel.Bounds = new Rectangle(matchPrevious.Right + gap, 0, Math.Max(0, matchNext.Left - matchPrevious.Right - gap * 2), listFooter.ClientSize.Height);
        }
        listFooter.Resize += (_, _) => PlaceMatchFooter();
        PlaceMatchFooter();
        listFooter.Controls.Add(matchPageLabel); listFooter.Controls.Add(matchPrevious); listFooter.Controls.Add(matchNext);
        matchRows.Dock = DockStyle.Fill;
        matchList.Controls.Add(matchRows);
        noFavorites.Dock = DockStyle.Fill;
        noFavorites.TextAlign = ContentAlignment.MiddleCenter;
        noFavorites.Visible = false;
        matchList.Controls.Add(noFavorites);
        listCard.Controls.Add(matchList); listCard.Controls.Add(listFooter); listCard.Controls.Add(listHeader);
        split.Controls.Add(listCard, 0, 0);

        split.Controls.Add(BuildDetailCard(), 1, 0);
        void ArrangeSplit()
        {
            if (split.ClientSize.Width <= 0) return;
            var targetListWidth = Math.Clamp(
                (int)Math.Round(split.ClientSize.Width * .30),
                UiMetrics.Px(this, 300),
                UiMetrics.Px(this, 400));
            var firstColumnWidth = targetListWidth + splitGap;
            var firstColumn = split.ColumnStyles[0];
            if (Math.Abs(firstColumn.Width - firstColumnWidth) >= .5f)
            {
                firstColumn.SizeType = SizeType.Absolute;
                firstColumn.Width = firstColumnWidth;
            }
        }
        split.SizeChanged += (_, _) => ArrangeSplit();
        split.Layout += (_, _) => ArrangeSplit();
        ArrangeSplit();
        return split;
    }

    private Control BuildDetailCard()
    {
        detailCard.Margin = Padding.Empty;
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = Theme.Surface, Margin = Padding.Empty };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, UiMetrics.Px(this, 60)));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, UiMetrics.Px(this, 45)));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var summary = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface, Margin = Padding.Empty };
        summary.Controls.Add(detailTitle); summary.Controls.Add(detailSubtitle); summary.Controls.Add(scoreLabel);
        void PlaceSummary()
        {
            detailTitle.Bounds = new Rectangle(0, 0, Math.Max(UiMetrics.Px(summary, 120), summary.ClientSize.Width - UiMetrics.Px(summary, 115)), UiMetrics.Px(summary, 28));
            detailSubtitle.Bounds = new Rectangle(UiMetrics.Px(summary, 1), UiMetrics.Px(summary, 29), Math.Max(UiMetrics.Px(summary, 120), summary.ClientSize.Width - UiMetrics.Px(summary, 115)), UiMetrics.Px(summary, 22));
            scoreLabel.Bounds = new Rectangle(Math.Max(0, summary.ClientSize.Width - UiMetrics.Px(summary, 105)), 0, UiMetrics.Px(summary, 100), UiMetrics.Px(summary, 48));
            scoreLabel.TextAlign = ContentAlignment.MiddleRight;
        }
        summary.Resize += (_, _) => PlaceSummary();
        PlaceSummary();
        layout.Controls.Add(summary, 0, 0);

        var detailBar = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface, Margin = Padding.Empty };
        var detailTabs = new FlowLayoutPanel
        {
            AutoSize = true,
            AutoSizeMode = AutoSizeMode.GrowAndShrink,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Theme.Surface,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        scoreboardTab.Size = new Size(UiMetrics.Px(this, 156), UiMetrics.Px(this, UiMetrics.CompactButtonHeight));
        roundsTab.Size = new Size(UiMetrics.Px(this, 136), UiMetrics.Px(this, UiMetrics.CompactButtonHeight));
        matchAnalysisTab.Size = new Size(UiMetrics.Px(this, 184), UiMetrics.Px(this, UiMetrics.CompactButtonHeight));
        favoriteButton.Size = new Size(UiMetrics.Px(this, 152), UiMetrics.Px(this, UiMetrics.CompactButtonHeight));
        scoreboardTab.Margin = new Padding(0, 0, UiMetrics.Px(this, UiMetrics.TightGap), 0);
        roundsTab.Margin = new Padding(0, 0, UiMetrics.Px(this, UiMetrics.TightGap), 0);
        matchAnalysisTab.Margin = Padding.Empty;
        favoriteButton.Margin = Padding.Empty;
        collapsedStatsButton.Margin = new Padding(UiMetrics.Px(this, UiMetrics.ControlGap), 0, 0, 0);
        detailTabs.Controls.Add(scoreboardTab);
        detailTabs.Controls.Add(roundsTab);
        detailTabs.Controls.Add(matchAnalysisTab);
        detailBar.Controls.Add(detailTabs);
        detailBar.Controls.Add(favoriteButton);
        detailBar.Controls.Add(collapsedStatsButton);
        void PlaceDetailBar()
        {
            detailTabs.Location = new Point(0, Math.Max(0, (detailBar.ClientSize.Height - detailTabs.Height) / 2));
            var vertical = Math.Max(0, (detailBar.ClientSize.Height - favoriteButton.Height) / 2);
            var right = detailBar.ClientSize.Width;
            var actionsRight = right;
            if (collapsedStatsButton.Visible)
            {
                collapsedStatsButton.Location = new Point(Math.Max(0, right - collapsedStatsButton.Width), Math.Max(0, (detailBar.ClientSize.Height - collapsedStatsButton.Height) / 2));
                actionsRight = collapsedStatsButton.Left - UiMetrics.Px(detailBar, UiMetrics.ControlGap);
            }
            var minimumLeft = detailTabs.Right + UiMetrics.Px(detailBar, UiMetrics.ContentGap);
            var favoriteWidth = Math.Min(UiMetrics.Px(this, 152), Math.Max(0, actionsRight - minimumLeft));
            if (favoriteButton.Width != favoriteWidth) favoriteButton.Width = favoriteWidth;
            favoriteButton.Location = new Point(Math.Max(minimumLeft, actionsRight - favoriteWidth), vertical);
        }
        detailBar.Resize += (_, _) => PlaceDetailBar();
        detailTabs.SizeChanged += (_, _) => PlaceDetailBar();
        favoriteButton.SizeChanged += (_, _) => PlaceDetailBar();
        collapsedStatsButton.SizeChanged += (_, _) => PlaceDetailBar();
        collapsedStatsButton.VisibleChanged += (_, _) => PlaceDetailBar();
        collapsedStatsButton.Click += (_, _) => CycleCollapsedScoreboardMetric();
        PlaceDetailBar();
        layout.Controls.Add(detailBar, 0, 1);

        ConfigureScoreboardColumns();
        scoreboardPage.Controls.Add(scoreboard);

        var roundsHeading = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface, Margin = Padding.Empty };
        var roundsTitle = NativePageStyle.Label("ラウンド別", NativePageStyle.SmallBold, Theme.Muted);
        roundsTitle.Dock = DockStyle.Left; roundsTitle.Width = 150;
        roundsPageLabel.TextAlign = ContentAlignment.MiddleCenter;
        roundsPageLabel.Dock = DockStyle.Right; roundsPageLabel.Width = 60;
        roundsNext.Dock = DockStyle.Right; roundsNext.Size = new Size(64, 28);
        roundsPrevious.Dock = DockStyle.Right; roundsPrevious.Size = new Size(64, 28);
        roundsHeading.Controls.Add(roundsPageLabel); roundsHeading.Controls.Add(roundsNext); roundsHeading.Controls.Add(roundsPrevious); roundsHeading.Controls.Add(roundsTitle);
        var roundsLayout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Theme.Surface, Margin = Padding.Empty };
        roundsLayout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        roundsLayout.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        roundsLayout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        roundsLayout.Controls.Add(roundsHeading, 0, 0);
        roundsLayout.Controls.Add(rounds, 0, 1);
        roundsPage.Controls.Add(roundsLayout);

        var matchAnalysis = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Theme.Surface, Margin = Padding.Empty };
        matchAnalysis.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var openerTileHeight = NativePageStyle.TextHeight(this, firstKillValue.Font, verticalPadding: 2)
            + NativePageStyle.TextHeight(this, NativePageStyle.Reading, verticalPadding: 2)
            + UiMetrics.Px(this, 22); // Space between labels, inside the border, and around the card.
        matchAnalysis.RowStyles.Add(new RowStyle(SizeType.Absolute, Math.Max(UiMetrics.Px(this, 112), 32 + openerTileHeight)));
        matchAnalysis.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var openerPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Theme.Surface, Margin = Padding.Empty };
        openerPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        openerPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        openerPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        openerInsight.Text = "ファーストキル・デス";
        openerInsight.Dock = DockStyle.Fill;
        openerPanel.Controls.Add(openerInsight, 0, 0);
        openerPanel.Controls.Add(BuildOpenerTiles(), 0, 1);

        var weaponPanel = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Theme.Surface, Margin = Padding.Empty };
        weaponPanel.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        weaponPanel.RowStyles.Add(new RowStyle(SizeType.Absolute, 32));
        weaponPanel.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var weaponsHeading = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface, Margin = Padding.Empty };
        var weaponsTitle = NativePageStyle.Label("武器・アビリティ別キル", NativePageStyle.SmallBold, Theme.Muted);
        weaponsTitle.AutoSize = true;
        weaponsTitle.Dock = DockStyle.Left;
        weaponPageLabel.Dock = DockStyle.Right; weaponPageLabel.Width = 52; weaponPageLabel.TextAlign = ContentAlignment.MiddleCenter;
        weaponNext.Dock = DockStyle.Right; weaponNext.Size = new Size(62, 28);
        weaponPrevious.Dock = DockStyle.Right; weaponPrevious.Size = new Size(62, 28);
        weaponsHeading.Controls.Add(weaponPageLabel); weaponsHeading.Controls.Add(weaponNext); weaponsHeading.Controls.Add(weaponPrevious); weaponsHeading.Controls.Add(weaponsTitle);
        weaponPanel.Controls.Add(weaponsHeading, 0, 0);
        weaponPanel.Controls.Add(weaponRows, 0, 1);
        matchAnalysis.Controls.Add(openerPanel, 0, 0);
        matchAnalysis.Controls.Add(weaponPanel, 0, 1);
        matchAnalysisPage.Controls.Add(matchAnalysis);

        detailPageHost.Controls.Add(matchAnalysisPage);
        detailPageHost.Controls.Add(roundsPage);
        detailPageHost.Controls.Add(scoreboardPage);
        layout.Controls.Add(detailPageHost, 0, 2);
        scoreboardTab.Click += (_, _) => ShowMatchDetailPage(0);
        roundsTab.Click += (_, _) => ShowMatchDetailPage(1);
        matchAnalysisTab.Click += (_, _) => ShowMatchDetailPage(2);

        detailNote.Dock = DockStyle.Bottom;
        detailNote.Height = NativePageStyle.TextHeight(this, detailNote.Font, verticalPadding: 8);
        detailCard.Controls.Add(layout);
        detailCard.Controls.Add(detailNote);
        detailEmpty.TextAlign = ContentAlignment.MiddleCenter;
        detailEmpty.Dock = DockStyle.Fill;
        detailEmpty.Visible = false;
        detailCard.Controls.Add(detailEmpty);
        detailLayout = layout;
        return detailCard;
    }

    private void ShowMatchDetailPage(int page)
    {
        using var redraw = PageRedrawScope.Begin(detailPageHost);
        scoreboardPage.Visible = page == 0;
        roundsPage.Visible = page == 1;
        matchAnalysisPage.Visible = page == 2;
        scoreboardTab.Selected = page == 0;
        roundsTab.Selected = page == 1;
        matchAnalysisTab.Selected = page == 2;
        scoreboardPageActive = page == 0;
        detailPageHost.PerformLayout();
        if (scoreboardPageActive)
        {
            scoreboardPage.PerformLayout();
            UpdateScoreboardColumnWidths();
        }
        else collapsedStatsButton.Visible = false;
        if (page == 1 && detailMatch is not null)
        {
            roundsPage.PerformLayout();
            rounds.PerformLayout();
            RenderRoundPage();
        }
        else if (page == 2)
        {
            matchAnalysisPage.PerformLayout();
            weaponRows.PerformLayout();
            RenderWeaponBreakdown();
        }
    }

    private Control BuildOpenerTiles()
    {
        var grid = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 4, RowCount = 1, BackColor = Theme.Surface, Margin = Padding.Empty };
        for (var i = 0; i < 4; i++) grid.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 25));
        grid.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        grid.Controls.Add(InsightTile(firstKillValue, "ファーストキル"), 0, 0);
        grid.Controls.Add(InsightTile(firstDeathValue, "ファーストデス"), 1, 0);
        grid.Controls.Add(InsightTile(firstKillRateValue, "ファーストキル率"), 2, 0);
        grid.Controls.Add(InsightTile(kdDiffValue, "K − D"), 3, 0);
        return grid;
    }

    private static Control InsightTile(Label value, string caption)
    {
        var card = NativePageStyle.Card(); card.Margin = new Padding(2); card.Padding = new Padding(7, 4, 7, 4);
        var label = NativePageStyle.Label(caption, NativePageStyle.Reading, Theme.Muted);
        card.Controls.Add(value); card.Controls.Add(label);
        void PlaceTile()
        {
            var inset = UiMetrics.Px(card, 5);
            var width = Math.Max(UiMetrics.Px(card, 15), card.ClientSize.Width - inset * 2);
            var valueHeight = NativePageStyle.TextHeight(card, value.Font, verticalPadding: 2);
            var labelHeight = NativePageStyle.TextHeight(card, label.Font, verticalPadding: 2);
            var top = Math.Max(0, (card.ClientSize.Height - valueHeight - labelHeight - UiMetrics.Px(card, 2)) / 2);
            value.TextAlign = ContentAlignment.MiddleCenter;
            label.TextAlign = ContentAlignment.MiddleCenter;
            value.Bounds = new Rectangle(inset, top, width, valueHeight);
            label.Bounds = new Rectangle(inset, value.Bottom + UiMetrics.Px(card, 2), width, labelHeight);
        }
        card.Resize += (_, _) => PlaceTile();
        PlaceTile();
        return card;
    }

    private Control BreakdownCard(string title, Control rows, ThemedButton previous, Label page, ThemedButton next)
    {
        var card = NativePageStyle.Card(); card.Margin = new Padding(3); card.Padding = new Padding(10, 6, 10, 7);
        var heading = NativePageStyle.Label(title, NativePageStyle.SmallBold, Theme.Muted);
        heading.Dock = DockStyle.Top; heading.Height = NativePageStyle.TextHeight(card, heading.Font, verticalPadding: 8);
        var footer = new Panel { Dock = DockStyle.Bottom, Height = 32, BackColor = Theme.Surface };
        previous.Size = new Size(64, 28);
        next.Size = new Size(64, 28);
        previous.Dock = DockStyle.Left; next.Dock = DockStyle.Right;
        page.Dock = DockStyle.Fill; page.TextAlign = ContentAlignment.MiddleCenter;
        footer.Controls.Add(page); footer.Controls.Add(previous); footer.Controls.Add(next);
        card.Resize += (_, _) =>
        {
            heading.Height = NativePageStyle.TextHeight(card, heading.Font, verticalPadding: 8);
            footer.Height = Math.Max(32, NativePageStyle.TextHeight(card, page.Font, verticalPadding: 12));
            previous.Size = next.Size = new Size(72, footer.ClientSize.Height - 2);
        };
        card.Controls.Add(rows); card.Controls.Add(footer); card.Controls.Add(heading);
        return card;
    }

    private void ConfigureScoreboardColumns()
    {
        scoreboard.ColumnCount = ScoreboardHeaders.Length;
        scoreboard.ColumnStyles.Clear();
        for (var column = 0; column < ScoreboardHeaders.Length; column++)
            scoreboard.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 0));
        UpdateScoreboardColumnWidths();
        BuildScoreboardHeader();
        scoreboard.AccessibleName = "試合スコアボード";
        scoreboard.AccessibleDescription = "表示幅に応じて統計列を折りたたみます。各選手のセルから全指標を確認できます。";
        DarkForm.ApplyDarkScrollBars(scoreboard);
    }

    private void UpdateScoreboardColumnWidths()
    {
        if (scoreboard.ColumnStyles.Count != ScoreboardHeaders.Length || scoreboard.ClientSize.Width <= 0) return;
        var availableWidth = scoreboard.ClientSize.Width;

        var columnCount = ScoreboardColumnCount(availableWidth);
        int[] columns;
        int[] widths;
        while (true)
        {
            columns = VisibleScoreboardColumns(columnCount);
            widths = columns.Select(ScoreboardColumnMinimumWidth).ToArray();
            if (widths.Sum() <= availableWidth || columnCount <= 3) break;
            columnCount--;
        }

        scoreboardAvailableWidth = availableWidth;
        scoreboardMetricCapacity = columns.Length - 1;
        visibleScoreboardColumns = columns;
        var remaining = Math.Max(0, availableWidth - widths.Sum());
        var playerIndex = Array.IndexOf(columns, 0);
        if (playerIndex >= 0)
        {
            var playerMax = UiMetrics.Px(this, 420);
            var playerExtra = Math.Min(remaining, Math.Max(0, playerMax - widths[playerIndex]));
            widths[playerIndex] += playerExtra;
            remaining -= playerExtra;
        }
        var metricPositions = Enumerable.Range(0, columns.Length).Where(index => columns[index] != 0).ToArray();
        if (metricPositions.Length > 0 && remaining > 0)
        {
            var equalShare = remaining / metricPositions.Length;
            foreach (var position in metricPositions) widths[position] += equalShare;
            widths[metricPositions[^1]] += remaining - equalShare * metricPositions.Length;
        }

        var widthByColumn = new int[ScoreboardHeaders.Length];
        for (var index = 0; index < columns.Length; index++) widthByColumn[columns[index]] = widths[index];
        for (var column = 0; column < ScoreboardHeaders.Length; column++)
        {
            var style = scoreboard.ColumnStyles[column];
            style.SizeType = SizeType.Absolute;
            style.Width = widthByColumn[column];
        }
        UpdateCollapsedStatsButton();
    }

    private int ScoreboardColumnCount(int width)
    {
        if (width >= UiMetrics.Px(this, 800)) return 8;
        if (width >= UiMetrics.Px(this, 745)) return 7;
        if (width >= UiMetrics.Px(this, 675)) return 6;
        if (width >= UiMetrics.Px(this, 600)) return 5;
        if (width >= UiMetrics.Px(this, 530)) return 4;
        return 3;
    }

    private int[] VisibleScoreboardColumns(int columnCount)
    {
        var metricCount = Math.Max(2, columnCount - 1);
        if (collapsedStatsIndex >= 0 && collapsedStatsIndex < metricCount) collapsedStatsIndex = -1;
        var metrics = ScoreboardMetricPriority.Take(metricCount).ToArray();
        if (collapsedStatsIndex >= metricCount && collapsedStatsIndex < ScoreboardMetricPriority.Length && metricCount >= 3)
            metrics[^1] = ScoreboardMetricPriority[collapsedStatsIndex];
        var visible = new HashSet<int>(metrics) { 0 };
        return Enumerable.Range(0, ScoreboardHeaders.Length).Where(visible.Contains).ToArray();
    }

    private int ScoreboardColumnMinimumWidth(int column)
    {
        ReadOnlySpan<int> minimumLogicalWidths = [220, 65, 120, 55, 65, 70, 70, 75];
        var minimum = UiMetrics.Px(this, minimumLogicalWidths[column]);
        if (column == 0) return minimum;

        var headerWidth = TextRenderer.MeasureText(
            ScoreboardHeaders[column] + " ▼", NativePageStyle.SmallBold, Size.Empty,
            TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width;
        var cellWidth = detailMatch?.Players
            .Select(player => TextRenderer.MeasureText(ScoreboardValue(player, column), NativePageStyle.Numeric, Size.Empty,
                TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width)
            .DefaultIfEmpty(0).Max() ?? 0;
        return Math.Max(minimum, Math.Max(headerWidth, cellWidth) + UiMetrics.Px(this, UiMetrics.TightGap + UiMetrics.ControlGap));
    }

    private int GetVerticalScrollBarWidthForDpi()
    {
        try
        {
            if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 14393))
                return Math.Max(0, GetSystemMetricsForDpi(2, (uint)DeviceDpi));
        }
        catch (EntryPointNotFoundException) { /* Older Windows versions use the fallback below. */ }
        catch (DllNotFoundException) { /* Use the standard system metric when the API is unavailable. */ }
        return Math.Max(0, (int)Math.Round(SystemInformation.VerticalScrollBarWidth * DeviceDpi / 96f));
    }

    [DllImport("user32.dll", EntryPoint = "GetSystemMetricsForDpi")]
    private static extern int GetSystemMetricsForDpi(int index, uint dpi);

    private void UpdateCollapsedStatsButton()
    {
        var hiddenColumns = Enumerable.Range(1, ScoreboardHeaders.Length - 1).Except(visibleScoreboardColumns).ToArray();
        var hiddenMetricCount = hiddenColumns.Length;
        var sortIsHidden = scoreboardSortColumn > 0 && !visibleScoreboardColumns.Contains(scoreboardSortColumn);
        var sortIndicator = scoreboardSortDescending ? "▼" : "▲";
        collapsedStatsButton.Text = sortIsHidden
            ? $"{ScoreboardHeaders[scoreboardSortColumn]} {sortIndicator}"
            : $"+{hiddenMetricCount} 指標";
        var textWidth = TextRenderer.MeasureText(collapsedStatsButton.Text, collapsedStatsButton.Font, Size.Empty,
            TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width;
        var inset = UiMetrics.Px(this, UiMetrics.ControlGap);
        collapsedStatsButton.Size = new Size(Math.Max(UiMetrics.Px(this, 70), textWidth + inset * 2), UiMetrics.Px(this, UiMetrics.CompactButtonHeight));
        var hiddenNames = hiddenColumns.Select(column => ScoreboardHeaders[column]).ToArray();
        collapsedStatsButton.AccessibleDescription = hiddenNames.Length == 0
            ? "すべての統計列を表示しています。"
            : $"現在非表示の指標: {string.Join("、", hiddenNames)}。クリックすると表示列を切り替えます。";
        scoreboardToolTips.SetToolTip(collapsedStatsButton, collapsedStatsButton.AccessibleDescription);
        collapsedStatsButton.Visible = scoreboardPageActive && hiddenMetricCount > 0 && scoreboardMetricCapacity >= 3;
    }

    private void CycleCollapsedScoreboardMetric()
    {
        if (scoreboardMetricCapacity < 3) return;
        using var redraw = PageRedrawScope.Begin(detailCard);
        var firstHiddenPriority = scoreboardMetricCapacity;
        if (collapsedStatsIndex < firstHiddenPriority)
        {
            var sortedPriority = Array.IndexOf(ScoreboardMetricPriority, scoreboardSortColumn);
            collapsedStatsIndex = sortedPriority >= firstHiddenPriority ? sortedPriority : firstHiddenPriority;
        }
        else if (collapsedStatsIndex + 1 < ScoreboardMetricPriority.Length)
            collapsedStatsIndex++;
        else
            collapsedStatsIndex = -1;

        scoreboardContent = null;
        UpdateScoreboardColumnWidths();
        if (detailMatch is not null) RenderScoreboard(detailMatch);
    }

    private static readonly string[] ScoreboardHeaders = ["選手 / エージェント", "ACS", "K/D/A", "K/D", "ADR", "DDΔ", "HS%", "FK/FD"];
    private static Color FriendlyTeamColor => Theme.Friendly;
    private static Color OpposingTeamColor => Theme.Opposing;
    private const int AcsColumn = 1, KdaColumn = 2, KdColumn = 3, AdrColumn = 4, DamageDeltaColumn = 5, HeadshotColumn = 6, OpeningColumn = 7;
    private static readonly int[] ScoreboardMetricPriority = [AcsColumn, KdaColumn, AdrColumn, HeadshotColumn, OpeningColumn, DamageDeltaColumn, KdColumn];
    private static bool IsSortableScoreboardColumn(int column) => column is >= AcsColumn and <= OpeningColumn;

    private static string ScoreboardValue(PlayerMatchStats player, int column) => column switch
    {
        0 => player.DisplayName.Length > 0 ? player.DisplayName : "名前非公開",
        AcsColumn => Number(player.AverageCombatScore, 0),
        KdaColumn => $"{player.Kills?.ToString() ?? "—"} / {player.Deaths?.ToString() ?? "—"} / {player.Assists?.ToString() ?? "—"}",
        KdColumn => Number(player.KillDeathRatio, 2),
        AdrColumn => Number(player.AverageDamagePerRound, 1),
        DamageDeltaColumn => Number(player.DamageDeltaPerRound, 1),
        HeadshotColumn => Percent(player.HeadshotPercent),
        OpeningColumn => $"{player.FirstKills?.ToString() ?? "—"} / {player.FirstDeaths?.ToString() ?? "—"}",
        _ => "—"
    };

    private void BuildScoreboardHeader()
    {
        scoreboard.SuspendLayout();
        scoreboard.RowCount = 1;
        scoreboard.RowStyles.Add(new RowStyle(SizeType.Absolute, NativePageStyle.TextHeight(this, NativePageStyle.SmallBold, verticalPadding: 8)));
        for (var column = 0; column < ScoreboardHeaders.Length; column++)
        {
            var sortable = IsSortableScoreboardColumn(column);
            var header = NativePageStyle.Label(ScoreboardHeaders[column], NativePageStyle.SmallBold, Theme.Muted);
            header.AutoSize = false;
            header.Dock = DockStyle.Fill;
            header.BackColor = Theme.Elevated;
            header.Padding = new Padding(UiMetrics.Px(this, UiMetrics.TightGap), 0, UiMetrics.Px(this, UiMetrics.ControlGap), 0);
            header.AutoEllipsis = true;
            // Number columns are right-aligned with their header so digits line up for comparison.
            if (column > 0) header.TextAlign = ContentAlignment.MiddleRight;
            if (sortable)
            {
                var sortColumn = column;
                header.Cursor = Cursors.Hand;
                header.AccessibleName = $"{ScoreboardHeaders[column]} で並べ替え";
                header.Click += (_, _) => ToggleScoreboardSort(sortColumn);
            }
            scoreboardHeaders.Add(header);
            scoreboard.Controls.Add(header, column, 0);
        }
        scoreboard.ResumeLayout();
    }

    private void ToggleScoreboardSort(int column)
    {
        if (!IsSortableScoreboardColumn(column) || detailMatch is not { } match) return;
        using var redraw = PageRedrawScope.Begin(detailCard);
        if (scoreboardSortColumn == column)
        {
            if (scoreboardSortDescending) scoreboardSortDescending = false;
            else { scoreboardSortColumn = -1; scoreboardSortDescending = true; }
        }
        else
        {
            scoreboardSortColumn = column;
            scoreboardSortDescending = true;
        }
        scoreboard.SuspendLayout();
        try
        {
            var row = 1;
            foreach (var player in OrderScoreboardPlayers(match.Players))
            {
                var background = ScoreboardRowColor(player, row);
                foreach (var cell in scoreboardRows[player])
                {
                    if (scoreboard.GetRow(cell) != row) scoreboard.SetRow(cell, row);
                    cell.BackColor = background;
                    foreach (Control child in cell.Controls) child.BackColor = background;
                }
                row++;
            }
            for (var i = 0; i < scoreboardHeaders.Count; i++)
            {
                var selected = i == scoreboardSortColumn;
                var indicator = scoreboardSortDescending ? "▼" : "▲";
                scoreboardHeaders[i].Text = selected ? $"{ScoreboardHeaders[i]} {indicator}" : ScoreboardHeaders[i];
                scoreboardHeaders[i].ForeColor = selected ? Theme.Text : Theme.Muted;
            }
        }
        finally { scoreboard.ResumeLayout(); }
        UpdateCollapsedStatsButton();
    }

    private Color ScoreboardRowColor(PlayerMatchStats player, int row)
    {
        if (player.Subject.Equals(HistorySubject, StringComparison.OrdinalIgnoreCase)) return Theme.Highlight;
        return row % 2 == 1 ? Theme.Surface : Theme.Elevated;
    }

    private IEnumerable<PlayerMatchStats> OrderScoreboardPlayers(IEnumerable<PlayerMatchStats> players)
    {
        // Default order: team, then ACS (highest first). A selected column sorts on top of it; OrderBy is
        // stable, so ties keep the default order.
        var playerList = players.ToArray();
        var deathmatch = playerList.Length > 1 && playerList.All(p => Guid.TryParse(p.TeamId, out _) && p.AverageCombatScore is null) &&
            playerList.Select(p => p.TeamId).Distinct(StringComparer.OrdinalIgnoreCase).Count() == playerList.Length;
        var ordered = deathmatch
            ? playerList.OrderByDescending(p => p.Score ?? -1).ThenByDescending(p => p.Kills ?? -1).ToArray()
            : playerList.OrderBy(p => p.TeamId, StringComparer.OrdinalIgnoreCase).ThenByDescending(p => p.AverageCombatScore ?? -1).ToArray();
        if (!IsSortableScoreboardColumn(scoreboardSortColumn)) return ordered;
        var column = scoreboardSortColumn;
        var descending = scoreboardSortDescending;
        return ordered.OrderBy(p => p, Comparer<PlayerMatchStats>.Create((left, right) => CompareScoreboardRows(left, right, column, descending)));
    }

    private static int CompareScoreboardRows(PlayerMatchStats left, PlayerMatchStats right, int column, bool descending)
    {
        var a = ScoreboardSortKey(left, column);
        var b = ScoreboardSortKey(right, column);
        // Rows without a sort value always stay at the bottom, whichever direction is selected.
        if (a is null) return b is null ? 0 : 1;
        if (b is null) return -1;
        var result = 0;
        for (var i = 0; i < a.Length && result == 0; i++) result = a[i].CompareTo(b[i]);
        return descending ? -result : result;
    }

    /// <summary>Ascending key for a sortable column, or null when the value is not available.</summary>
    private static double[]? ScoreboardSortKey(PlayerMatchStats player, int column)
    {
        static double? Finite(double? value) => value is { } number && double.IsFinite(number) ? number : null;
        return column switch
        {
            AcsColumn => Finite(player.AverageCombatScore) is { } acs ? new[] { acs } : null,
            KdColumn => Finite(player.KillDeathRatio) is { } kd ? new[] { kd } : null,
            AdrColumn => Finite(player.AverageDamagePerRound) is { } adr ? new[] { adr } : null,
            DamageDeltaColumn => Finite(player.DamageDeltaPerRound) is { } delta ? new[] { delta } : null,
            HeadshotColumn => Finite(player.HeadshotPercent) is { } hs ? new[] { hs } : null,
            OpeningColumn => player.FirstKills is { } firstKills
                ? new double[] { firstKills, -(double)(player.FirstDeaths ?? int.MaxValue) }
                : null,
            // K/D/A: more kills first, then fewer deaths, then more assists.
            KdaColumn => player.Kills is { } kills
                ? new double[] { kills, -(double)(player.Deaths ?? int.MaxValue), player.Assists ?? -1 }
                : null,
            _ => null
        };
    }

    private async Task RefreshHistoryAsync()
    {
        await LoadNextPageAsync(refresh: true);
    }

    internal async Task SyncAfterMatchAsync(string subject)
    {
        if (IsDisposed || shuttingDown || !string.Equals(api.Subject, subject, StringComparison.OrdinalIgnoreCase)) return;
        var started = DateTimeOffset.UtcNow;
        if (!archiveSubject.Equals(subject, StringComparison.OrdinalIgnoreCase) || !archiveLoaded)
            await ActivatePageAsync(silent: true);
        if (activeLoadTask is { } pending) await pending;
        if (latestSync < started && !IsDisposed && !shuttingDown && CanSync && api.Subject.Equals(subject, StringComparison.OrdinalIgnoreCase))
            await LoadNextPageAsync(refresh: true, silent: true);
    }

    private Task LoadNextPageAsync(bool refresh = false, bool silent = false)
    {
        if (IsDisposed || shuttingDown || loading || (!refresh && !hasMore)) return Task.CompletedTask;
        if (!CanSync)
        {
            if (!silent) SetStatus("試合分析を見るには、上部のRiotログインから接続してください。", error: false);
            return Task.CompletedTask;
        }

        var task = LoadNextPageCoreAsync(refresh, silent);
        activeLoadTask = task;
        _ = task.ContinueWith(completed => Interlocked.CompareExchange(ref activeLoadTask, null, completed),
            CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        return task;
    }

    private async Task LoadNextPageCoreAsync(bool refresh, bool silent)
    {
        if (IsDisposed || shuttingDown) return;

        var generation = loadGeneration;
        var subject = archiveSubject;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        operation.CancelAfter(TimeSpan.FromSeconds(35));
        activeLoad = operation;
        var token = operation.Token;
        loading = true;
        // Progress messages are kept out of the status line, so the pressed button itself shows the work.
        if (refresh) refreshButton.Text = "更新中…";
        else loadMoreButton.Text = "読み込み中…";
        UpdateHistoryButtons();
        var startIndex = refresh ? 0 : nextIndex;
        if (!silent) SetStatus(startIndex == 0 ? "最近の試合を読み込んでいます…" : "さらに試合を読み込んでいます…", error: false);
        if (matches.Count == 0)
        {
            historyEmptyText.Visible = false;
            historyContent.Visible = false;
            historySkeleton.BringToFront();
            historySkeleton.Loading = true;
        }
        try
        {
            var weaponNamesTask = LoadWeaponNamesAsync(token);
            var competitiveTask = LoadCompetitiveUpdatesAsync(token);
            var page = await api.GetMatchHistory(startIndex, PageSize, token).WaitAsync(token);
            if (generation != loadGeneration || IsDisposed) return;
            var entriesToLoad = page.Entries.Where(entry => !matchPositions.TryGetValue(entry.MatchId, out var position) ||
                    NeedsKillTimeRefresh(matches[position]) || NeedsRankRefresh(matches[position]))
                .DistinctBy(entry => entry.MatchId, StringComparer.OrdinalIgnoreCase).ToArray();

            using var concurrency = new SemaphoreSlim(3, 3);
            var parsedDetails = await Task.WhenAll(entriesToLoad.Select(async entry =>
            {
                await concurrency.WaitAsync(token);
                try
                {
                    using var detailTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                    detailTimeout.CancelAfter(TimeSpan.FromSeconds(6));
                    var detailTask = api.GetMatchDetails(entry.MatchId, detailTimeout.Token);
                    JsonDocument? response;
                    try { response = await detailTask.WaitAsync(detailTimeout.Token); }
                    catch (OperationCanceledException)
                    {
                        _ = DisposeLateMatchDetailsAsync(detailTask);
                        throw;
                    }
                    using var document = response;
                    if (document is null) return null;
                    var root = document.RootElement.Clone();
                    var result = await Task.Run(() => MatchDetailsParser.Parse(root), detailTimeout.Token).WaitAsync(detailTimeout.Token);
                    if (result.Match is not { } match) return null;
                    if (match.Info.MatchId.Length > 0 && !match.Info.MatchId.Equals(entry.MatchId, StringComparison.OrdinalIgnoreCase)) return null;
                    return match with { Info = match.Info with { MatchId = entry.MatchId } };
                }
                catch (ApiException ex) when (ex.Status == 404) { return null; }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { return null; }
                catch (HttpRequestException) { return null; }
                finally { concurrency.Release(); }
            }));

            var weaponNames = await weaponNamesTask.WaitAsync(token);

            if (generation != loadGeneration || IsDisposed) return;
            var unresolvedSubjects = parsedDetails.Where(match => match is not null)
                .SelectMany(match => match!.Players.Where(player => player.GameName.Length == 0 &&
                    !(matchPositions.TryGetValue(match.Info.MatchId, out var position) &&
                      matches[position].FindPlayer(player.Subject) is { GameName.Length: > 0 })).Select(player => player.Subject))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToArray();
            IReadOnlyDictionary<string, (string GameName, string TagLine)> resolvedNames = new Dictionary<string, (string, string)>(StringComparer.OrdinalIgnoreCase);
            if (unresolvedSubjects.Length > 0)
            {
                using var namesTimeout = CancellationTokenSource.CreateLinkedTokenSource(token);
                namesTimeout.CancelAfter(TimeSpan.FromSeconds(5));
                try { resolvedNames = await api.GetPlayerNames(unresolvedSubjects, namesTimeout.Token).WaitAsync(namesTimeout.Token); }
                catch (Exception ex) when (ex is ApiException or HttpRequestException or JsonException)
                { /* Names are optional; display the available match details. */ }
                catch (OperationCanceledException) when (!token.IsCancellationRequested)
                { /* The name lookup timed out; keep the match details. */ }
            }
            if (generation != loadGeneration || IsDisposed) return;
            var competitiveUpdates = await competitiveTask;
            if (generation != loadGeneration || IsDisposed) return;
            foreach (var entry in competitiveUpdates)
            {
                if (!matchPositions.TryGetValue(entry.Key, out var index)) continue;
                var existing = matches[index];
                var updated = CompetitiveUpdate.Attach(existing.Match, competitiveUpdates);
                if (updated.CompetitiveUpdate == existing.Match.CompetitiveUpdate) continue;
                matches[index] = MatchAnalyticsEngine.Analyze(updated);
                saveQueue.Enqueue(matches[index]);
            }
            var prepared = parsedDetails.Where(match => match is not null).Select(match =>
            {
                var details = match!;
                var previous = matchPositions.TryGetValue(details.Info.MatchId, out var position) ? matches[position] : null;
                var players = details.Players.Select(player =>
                {
                    if (player.GameName.Length > 0) return player;
                    if (resolvedNames.TryGetValue(player.Subject, out var name))
                        return player with { GameName = name.GameName, TagLine = name.TagLine };
                    return previous?.FindPlayer(player.Subject) is { GameName.Length: > 0 } old
                        ? player with { GameName = old.GameName, TagLine = old.TagLine } : player;
                }).ToArray();
                return CompetitiveUpdate.Attach(details with { Players = players, CompetitiveUpdate = previous?.Match.CompetitiveUpdate }, competitiveUpdates);
            }).ToArray();
            var parsed = await Task.Run(() => prepared.Select(MatchAnalyticsEngine.Analyze).ToArray(), token).WaitAsync(token);
            if (generation != loadGeneration || IsDisposed) return;
            if (refresh) unavailableDetailsCount = 0;
            if (!ReferenceEquals(weapons, weaponNames)) weaponRenderKey = null;
            weapons = weaponNames;
            (nextIndex, hasMore) = MatchHistoryPaging.Advance(nextIndex, hasMore, refresh, startIndex, PageSize, page.RawCount, page.Total);
            unavailableDetailsCount += parsedDetails.Count(x => x is null);
            foreach (var analysis in parsed)
            {
                if (AddMatch(analysis) || UpgradeSavedMatch(analysis)) saveQueue.Enqueue(analysis);
            }

            // Show the fetched matches before touching the disk: persistence runs outside the fetch timeout,
            // so a slow save can neither hide merged results nor be abandoned mid-write by the timeout.
            if (refresh) latestSync = DateTimeOffset.UtcNow;
            if (matches.Count == 0 && !hasMore)
                RenderEmptyState("試合詳細を取得できませんでした。試合終了後、少し待ってから更新してください。");
            else RenderHistory();
            SetStatus(archiveSaveFailed ? ArchiveSaveFailedMessage : FetchStatusMessage(), error: archiveSaveFailed);
            if (matches.Count > 0 && (saveQueue.PendingCount > 0 || refresh))
                _ = PersistHistoryAsync(subject, weaponNames, generation);
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            if (!silent && !lifetime.IsCancellationRequested && !shuttingDown && generation == loadGeneration && !IsDisposed)
                ShowHistoryLoadError(new TimeoutException(), "試合履歴の取得がタイムアウトしました。「最新に更新」で再試行してください。");
        }
        catch (ApiException ex) when (ex.Status is 401 or 403)
        {
            if (!silent && generation == loadGeneration && !IsDisposed && !shuttingDown) ShowHistoryLoadError(ex, "Riotのログイン期限が切れました。再接続してください。");
        }
        catch (ApiException ex) when (ex.Status == 429)
        {
            if (!silent && generation == loadGeneration && !IsDisposed && !shuttingDown) ShowHistoryLoadError(ex, "Riot側の応答が混み合っています。少し待ってから再読み込みしてください。");
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException or InvalidDataException or JsonException or InvalidOperationException or TaskCanceledException)
        {
            if (!silent && generation == loadGeneration && !IsDisposed && !shuttingDown) ShowHistoryLoadError(ex, "試合履歴を取得できませんでした。ネット接続とRiotアカウントを確認してください。");
        }
        catch (Exception ex)
        {
            if (!silent && generation == loadGeneration && !IsDisposed && !shuttingDown) ShowHistoryLoadError(ex, "試合履歴を表示できませんでした。最新に更新してください。");
        }
        finally
        {
            operation.Cancel();
            if (ReferenceEquals(activeLoad, operation)) activeLoad = null;
            if (generation == loadGeneration && !IsDisposed)
            {
                loading = false;
                loadMoreButton.Text = "さらに10試合";
                refreshButton.Text = "最新に更新";
                UpdateHistoryButtons();
            }
        }
    }

    private async Task<IReadOnlyDictionary<string, CompetitiveUpdate>> LoadCompetitiveUpdatesAsync(CancellationToken token)
    {
        try { return await api.GetCompetitiveUpdates(token).WaitAsync(token); }
        catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
        catch (Exception ex) when (ex is ApiException or HttpRequestException or JsonException or InvalidDataException or InvalidOperationException) { }
        return new Dictionary<string, CompetitiveUpdate>(StringComparer.OrdinalIgnoreCase);
    }

    // After a successful fetch the status line confirms when the list was last brought up to date.
    private string FetchStatusMessage()
    {
        if (unavailableDetailsCount > 0) return $"{unavailableDetailsCount} 件の試合詳細は取得できませんでした。保存した履歴は表示できます。";
        return latestSync != default ? $"最終更新 {latestSync.ToLocalTime():HH:mm}" : "";
    }

    /// <summary>
    /// Saves the queued matches for <paramref name="subject"/> after they are already on screen. Never throws:
    /// failures and timeouts keep the entries queued for the next refresh, and results from a previous
    /// account session (or after the page is closed) are ignored.
    /// </summary>
    private Task PersistHistoryAsync(string subject, IReadOnlyDictionary<string, string> weaponNames, int generation)
    {
        var task = PersistHistoryCoreAsync(subject, weaponNames, generation);
        TrackArchiveOperation(task);
        return task;
    }

    private async Task PersistHistoryCoreAsync(string subject, IReadOnlyDictionary<string, string> weaponNames, int generation)
    {
        MatchHistorySaveResult result;
        try { result = await saveQueue.SaveAsync(subject, weaponNames, ArchiveSaveTimeout, lifetime.Token); }
        catch (Exception ex) { result = new(MatchHistorySaveOutcome.Failed, ex); }
        if (generation != loadGeneration || IsDisposed || shuttingDown || result.Outcome == MatchHistorySaveOutcome.Superseded) return;
        if (result.Outcome == MatchHistorySaveOutcome.Saved)
        {
            if (!archiveSaveFailed) return;
            archiveSaveFailed = false;
            // Only replace our own failure notice; leave any newer fetch/login error visible.
            if (!loading && pageStatus.Text == ArchiveSaveFailedMessage) SetStatus(FetchStatusMessage(), error: false);
            return;
        }
        NativePageStyle.LogPageError("MatchHistoryArchive", result.Error ?? new TimeoutException("Saving match history timed out."));
        archiveSaveFailed = true;
        // A running load reports it when it finishes; an existing error (e.g. expired login) is not hidden.
        if (!loading && pageStatus.ForeColor != Theme.Red) SetStatus(ArchiveSaveFailedMessage, error: true);
    }

    private static async Task DisposeLateMatchDetailsAsync(Task<JsonDocument?> task)
    {
        try { (await task.ConfigureAwait(false))?.Dispose(); }
        catch (Exception) { /* The timed-out request is no longer needed. */ }
    }

    private bool AddMatch(MatchAnalytics match)
    {
        var id = match.Info.MatchId;
        if (id.Length == 0 || !matchPositions.TryAdd(id, matches.Count)) return false;
        matches.Add(match);
        return true;
    }

    private static bool NeedsKillTimeRefresh(MatchAnalytics match) => !match.HasKillTimeline &&
        match.Match.Rounds.Any(round => round.Kills.Any(kill => kill.TimeSinceRoundStartMillis is null && kill.TimeSinceGameStartMillis is null));

    private static bool NeedsRankRefresh(MatchAnalytics match) =>
        match.Info.QueueId.Equals("competitive", StringComparison.OrdinalIgnoreCase) &&
        match.Match.Players.Count > 0 && match.Match.Players.All(player => player.CompetitiveTier is null);

    private bool UpgradeSavedMatch(MatchAnalytics updated)
    {
        if (!matchPositions.TryGetValue(updated.Info.MatchId, out var position)) return false;
        var previous = matches[position];
        var rankImproved = NeedsRankRefresh(previous) && updated.Match.Players.Any(player => player.CompetitiveTier is not null);
        var killsImproved = NeedsKillTimeRefresh(previous) && updated.HasKillTimeline;
        if ((!rankImproved && !killsImproved) || updated.Rounds.Count < previous.Rounds.Count ||
            (previous.HasKillTimeline && !updated.HasKillTimeline) ||
            (previous.HasDamageData && !updated.HasDamageData)) return false;
        matches[position] = updated;
        return true;
    }

    private void RenderHistory()
    {
        using var redraw = PageRedrawScope.Begin(historyPage);
        UpdateModeFilterItems();
        // The mode filter applies to history; career analytics always use competitive matches.
        var ordered = OrderedVisibleMatches();
        historyContent.Visible = ordered.Length > 0;
        historyEmptyText.Visible = ordered.Length == 0;
        if (ordered.Length == 0) RenderEmptyState("表示できる試合がありません。");
        var competitive = MatchAnalyticsEngine.CompetitiveOnly(matches).ToArray();
        var history = MatchAnalyticsEngine.AnalyzeHistory(competitive, HistorySubject);
        var today = MatchAnalyticsEngine.Today(competitive, HistorySubject, DateTimeOffset.Now, TimeZoneInfo.Local);
        todaySummary.Text = today.MatchCount == 0 ? "" : $"今日: {today.Wins}勝 {today.Losses}敗 · 平均ACS {Number(today.AverageCombatScore, 0)}";
        UpdateTrend();
        matchCount.Text = $"{ordered.Length} 試合";
        // Draws are listed only when there are any, so the common W/L case stays compact.
        var record = history.Draws > 0 ? $"{history.Wins}W {history.Losses}L {history.Draws}D" : $"{history.Wins}W {history.Losses}L";
        var recordCaption = history.Draws > 0 ? $"勝敗 · {history.MatchCount} 試合 · 引き分け {history.Draws}" : $"勝敗 · {history.MatchCount} 試合";
        SetMetrics([
            (record, recordCaption),
            (Percent(history.WinRatePercent), "勝率"),
            (Number(history.AverageCombatScore, 0), "平均ACS"),
            (Number(history.OverallKillDeathRatio, 2), "通算K/D"),
            (Number(history.AverageDamagePerRound, 1), "平均ADR"),
            (Percent(history.AverageHeadshotPercent), "平均HS%"),
            ($"{history.Recent5.Wins}W · {Percent(history.Recent5.WinRatePercent)}", $"直近5戦 · {history.Recent5.MatchesConsidered}戦"),
            ($"{history.Recent10.Wins}W · {Percent(history.Recent10.WinRatePercent)}", $"直近10戦 · {history.Recent10.MatchesConsidered}戦")
        ]);
        metricLabels[1].Value.ForeColor = PerformanceColor(history.WinRatePercent, 50);
        metricLabels[3].Value.ForeColor = PerformanceColor(history.OverallKillDeathRatio, 1);
        lastMapEntries = history.Maps;
        lastAgentEntries = history.Agents;
        lastHistoryRows = history.MatchesNewestFirst;
        RenderBreakdowns(mapRows, lastMapEntries, lastHistoryRows, map: true);
        RenderBreakdowns(agentRows, lastAgentEntries, lastHistoryRows, map: false);
        if (ordered.Length > 0) RenderHistorySelection(ordered);
        UpdateHistoryButtons();
        historySkeleton.Loading = false;
    }

    private MatchAnalytics[] OrderedVisibleMatches() =>
        matches.Where(MatchesMode).OrderByDescending(match => match.Info.GameStartMillis ?? 0).ToArray();

    private void RenderHistorySelection() => RenderHistorySelection(OrderedVisibleMatches());

    private void RenderHistorySelection(IReadOnlyList<MatchAnalytics> ordered)
    {
        using var redraw = PageRedrawScope.Begin(historyContent);
        var visible = favoritesOnly ? ordered.Where(match => favorites.Contains(match.Info.MatchId)).ToArray() : ordered;
        var empty = visible.Count == 0;
        noFavorites.Text = selectedMode is null ? "お気に入り登録した試合がここに表示されます。" : "このモードのお気に入りはまだありません。";
        noFavorites.Visible = empty && favoritesOnly;
        if (noFavorites.Visible) noFavorites.BringToFront();
        matchRows.Visible = !empty;
        var selected = visible.FirstOrDefault(match => match.Info.MatchId.Equals(selectedMatchId, StringComparison.OrdinalIgnoreCase));
        if (selected is null)
        {
            selected = visible.FirstOrDefault();
            selectedMatchId = selected?.Info.MatchId ?? "";
            matchPageIndex = 0;
        }
        RenderMatchList(visible);
        if (selected is null)
        {
            if (detailLayout is not null) detailLayout.Visible = false;
            detailMatch = null;
            detailNote.Visible = false;
            detailEmpty.Text = "お気に入りの試合がありません。";
            detailEmpty.Visible = true;
        }
        else if (!ReferenceEquals(detailMatch, selected) || detailEmpty.Visible)
            RenderMatchDetail(selected);
        else RenderWeaponBreakdown();
        UpdateFavoriteButton();
    }

    private void ToggleFavorite()
    {
        if (shuttingDown || IsDisposed || favoriteSaving || detailMatch is not { } match || !Guid.TryParse(archiveSubject, out _)) return;
        var matchId = match.Info.MatchId;
        var favorite = !favorites.Contains(matchId);
        var subject = archiveSubject;
        var generation = loadGeneration;
        favoriteSaving = true;
        UpdateFavoriteButton();
        var task = ToggleFavoriteAsync(subject, match, matchId, favorite, generation);
        TrackArchiveOperation(task);
    }

    private async Task ToggleFavoriteAsync(string subject, MatchAnalytics match, string matchId, bool favorite, int generation)
    {
        try
        {
            var result = await saveQueue.SetFavoriteAsync(subject, match.Match, favorite, ArchiveSaveTimeout, lifetime.Token);
            if (generation != loadGeneration || IsDisposed || shuttingDown || result.Outcome == MatchHistorySaveOutcome.Superseded) return;
            if (result.Outcome != MatchHistorySaveOutcome.Saved)
            {
                NativePageStyle.LogPageError("MatchHistoryFavorite", result.Error ?? new TimeoutException("Saving favorite timed out."));
                SetStatus("お気に入りを保存できませんでした。", error: true);
                return;
            }
            if (favorite) favorites.Add(matchId);
            else favorites.Remove(matchId);
            RenderHistorySelection();
            SetStatus("", error: false);
        }
        catch (Exception ex)
        {
            if (generation == loadGeneration && !IsDisposed && !shuttingDown)
            {
                NativePageStyle.LogPageError("MatchHistoryFavorite", ex);
                SetStatus("お気に入りを保存できませんでした。", error: true);
            }
        }
        finally
        {
            if (generation == loadGeneration)
            {
                favoriteSaving = false;
                if (!IsDisposed && !shuttingDown) UpdateFavoriteButton();
            }
        }
    }

    private void UpdateFavoriteButton()
    {
        var favorite = detailMatch is { } match && favorites.Contains(match.Info.MatchId);
        favoriteButton.Enabled = !favoriteSaving && !shuttingDown && detailMatch is not null && archiveSubject.Length > 0;
        favoriteButton.Text = favorite ? "★ 登録済み" : "☆ お気に入り";
        favoriteButton.AccessibleName = favorite ? "選択した試合のお気に入り登録を解除" : "選択した試合をお気に入りに登録";
        favoriteButton.Selected = favorite;
    }

    private void UpdateHistoryButtons()
    {
        refreshButton.Enabled = CanSync && !loading;
        loadMoreButton.Enabled = CanSync && hasMore && !loading;
        favoritesFilter.Enabled = matches.Count > 0;
    }

    private void RenderMatchList(IReadOnlyList<MatchAnalytics> ordered)
    {
        using var redraw = PageRedrawScope.Begin(matchRows);
        orderedMatches = ordered;
        var minimumRowHeight = MatchListMinimumRowHeight();
        var pageSize = Math.Max(1, Math.Min(PageSize, matchRows.ClientSize.Height / minimumRowHeight));
        var pageCount = Math.Max(1, (ordered.Count + pageSize - 1) / pageSize);
        matchPageIndex = Math.Clamp(matchPageIndex, 0, pageCount - 1);
        var start = matchPageIndex * pageSize;
        var visibleMatches = ordered.Skip(start).Take(pageSize).ToArray();
        var rowHeight = Math.Max(minimumRowHeight,
            Math.Min(UiMetrics.Px(this, 150), matchRows.ClientSize.Height / Math.Max(1, visibleMatches.Length)));
        matchRows.SuspendLayout();
        try
        {
            foreach (var match in visibleMatchRows.Keys.Where(match => !visibleMatches.Contains(match, ReferenceEqualityComparer.Instance)).ToArray())
            {
                visibleMatchRows[match].Dispose();
                visibleMatchRows.Remove(match);
            }
            for (var i = 0; i < visibleMatches.Length; i++)
            {
                var match = visibleMatches[i];
                if (!visibleMatchRows.TryGetValue(match, out var row))
                {
                    row = CreateMatchRow(match, rowHeight);
                    row.Bounds = new Rectangle(0, i * rowHeight, matchRows.ClientSize.Width, rowHeight - UiMetrics.Px(this, 3));
                    visibleMatchRows.Add(match, row);
                    matchRows.Controls.Add(row);
                }
                row.Bounds = new Rectangle(0, i * rowHeight, matchRows.ClientSize.Width, rowHeight - UiMetrics.Px(this, 3));
                row.BackColor = match.Info.MatchId.Equals(selectedMatchId, StringComparison.OrdinalIgnoreCase)
                    ? Theme.Selected : Theme.Surface;
                row.Controls["matchTitle"]!.Text = MatchListTitle(match);
            }
        }
        finally { matchRows.ResumeLayout(); }
        matchCount.Text = $"{ordered.Count} 試合";
        // Shows which part of the list is on screen, so paging by wheel or keys gives visible feedback.
        matchPageLabel.Text = visibleMatches.Length == 0
            ? $"{matchPageIndex + 1} / {pageCount}"
            : $"{start + 1}–{start + visibleMatches.Length} 試合目  ·  {matchPageIndex + 1} / {pageCount}";
        matchPrevious.Enabled = matchPageIndex > 0;
        matchNext.Enabled = matchPageIndex + 1 < pageCount;
        matchPrevious.Visible = matchNext.Visible = pageCount > 1;
    }

    private int MatchListLineHeight() => Math.Max(
        NativePageStyle.TextHeight(this, NativePageStyle.ReadingBold, verticalPadding: 2),
        NativePageStyle.TextHeight(this, NativePageStyle.Reading, verticalPadding: 2));

    private int MatchListMinimumRowHeight() => MatchListLineHeight() * 3 + UiMetrics.Px(this, 14) + 2;

    private Control CreateMatchRow(MatchAnalytics match, int height)
    {
        var me = match.FindPlayer(HistorySubject);
        var accent = OutcomeColor(me?.Outcome);
        var selected = match.Info.MatchId.Equals(selectedMatchId, StringComparison.OrdinalIgnoreCase);
        var row = new Panel { Tag = match.Info.MatchId, Height = height - UiMetrics.Px(this, 3), BackColor = selected ? Theme.Selected : Theme.Surface, BorderStyle = BorderStyle.FixedSingle, Cursor = Cursors.Hand };
        var marker = new Panel { BackColor = accent };
        var title = NativePageStyle.Label(MatchListTitle(match), NativePageStyle.ReadingBold, Theme.Text);
        title.Name = "matchTitle";
        // The result is spelled out next to the score so it never depends on colour alone (draws included).
        var outcome = OutcomeName(me?.Outcome);
        var rr = match.Match.CompetitiveUpdate?.RankedRatingEarned is { } earned ? $"  {earned:+0;-0;0} RR" : "";
        var score = NativePageStyle.Label(outcome.Length > 0 ? $"{outcome}  {TeamScore(match, me)}" : TeamScore(match, me), NativePageStyle.ReadingBold, accent);
        var rrBadge = NativePageStyle.Label(rr.Trim(), NativePageStyle.SmallBold, Theme.Text);
        rrBadge.AutoSize = false; rrBadge.Visible = rr.Length > 0; rrBadge.BackColor = Theme.Highlight; rrBadge.TextAlign = ContentAlignment.MiddleCenter;
        var agent = NativePageStyle.Label(AgentName(me?.CharacterId ?? ""), NativePageStyle.Reading, Theme.Muted);
        var date = NativePageStyle.Label(match.Info.GameStartUtc?.ToLocalTime().ToString("M/d HH:mm") ?? "日時不明", NativePageStyle.Reading, Theme.Muted);
        var kda = NativePageStyle.Label($"K/D/A  {me?.Kills?.ToString() ?? "—"} / {me?.Deaths?.ToString() ?? "—"} / {me?.Assists?.ToString() ?? "—"}", NativePageStyle.Reading, Theme.Text);
        var acs = NativePageStyle.Label($"ACS  {Number(me?.AverageCombatScore, 0)}", NativePageStyle.Reading, Theme.Muted);
        score.TextAlign = date.TextAlign = acs.TextAlign = ContentAlignment.MiddleRight;
        row.Controls.AddRange([marker, title, score, rrBadge, agent, date, kda, acs]);

        void PlaceLine(Label left, Label right, int y, int lineHeight)
        {
            var leftInset = UiMetrics.Px(row, 16);
            var gap = UiMetrics.Px(row, 10);
            var rightEdge = Math.Max(leftInset, row.ClientSize.Width - UiMetrics.Px(row, 12));
            var rightWidth = Math.Min(right.PreferredWidth, rightEdge - leftInset);
            right.Bounds = new Rectangle(rightEdge - rightWidth, y, rightWidth, lineHeight);
            left.Bounds = new Rectangle(leftInset, y, Math.Max(0, right.Left - leftInset - gap), lineHeight);
        }

        void Place()
        {
            var lineHeight = MatchListLineHeight();
            var top = Math.Max(0, (row.ClientSize.Height - lineHeight * 3) / 2);
            var markerInset = UiMetrics.Px(row, 8);
            marker.Bounds = new Rectangle(UiMetrics.Px(row, 5), markerInset, UiMetrics.Px(row, 3), Math.Max(0, row.ClientSize.Height - markerInset * 2));
            PlaceLine(title, score, top, lineHeight);
            if (rrBadge.Visible)
            {
                var gap = UiMetrics.Px(row, UiMetrics.TightGap);
                var width = Math.Min(rrBadge.PreferredWidth + gap * 2, Math.Max(0, title.Width - gap));
                rrBadge.Bounds = new Rectangle(Math.Max(title.Left, score.Left - gap - width), top, width, lineHeight);
                title.Width = Math.Max(0, rrBadge.Left - title.Left - gap);
            }
            PlaceLine(agent, date, top + lineHeight, lineHeight);
            PlaceLine(kda, acs, top + lineHeight * 2, lineHeight);
        }
        row.Resize += (_, _) => Place();
        Place();
        void Select(object? _, EventArgs __)
        {
            if (selectedMatchId.Equals(match.Info.MatchId, StringComparison.OrdinalIgnoreCase)) return;
            selectedMatchId = match.Info.MatchId;
            foreach (Control item in matchRows.Controls)
                item.BackColor = item.Tag is string id && id.Equals(selectedMatchId, StringComparison.OrdinalIgnoreCase)
                    ? Theme.Selected : Theme.Surface;
            RenderMatchDetail(match);
        }
        BindClick(row, Select);
        return row;
    }

    private string MatchListTitle(MatchAnalytics match)
    {
        var star = favorites.Contains(match.Info.MatchId) ? "★ " : "";
        return $"{star}{MapName(match.Info.MapId)}  ·  {QueueName(MatchAnalyticsEngine.DisplayQueueId(match.Match))}";
    }

    private static void BindClick(Control root, EventHandler click)
    {
        root.Click += click;
        foreach (Control child in root.Controls) BindClick(child, click);
    }

    private void RenderMatchDetail(MatchAnalytics match)
    {
        using var redraw = PageRedrawScope.Begin(detailLayout ?? historyContent);
        if (detailLayout is not null) detailLayout.Visible = true;
        detailEmpty.Visible = false;
        detailMatch = match;
        UpdateFavoriteButton();
        var me = match.FindPlayer(HistorySubject);
        var team = match.Teams.FirstOrDefault(t => t.TeamId.Equals(me?.TeamId, StringComparison.OrdinalIgnoreCase));
        detailTitle.Text = DetailTitle(match);
        var outcomeName = OutcomeName(me?.Outcome);
        detailSubtitle.Text = (outcomeName.Length > 0 ? outcomeName + "  ·  " : "") +
            $"{match.Info.GameStartUtc?.ToLocalTime().ToString("yyyy/MM/dd HH:mm") ?? "日時不明"}  ·  {Math.Round(match.Info.GameLength?.TotalMinutes ?? 0)} 分  ·  {match.Rounds.Count} ラウンド";
        scoreLabel.Text = team?.RoundsWon is { } won ? $"{won} : {team.RoundsLost?.ToString() ?? "—"}" : "—";
        scoreLabel.ForeColor = me?.Outcome is MatchOutcome.Unknown or null ? Theme.Text : OutcomeColor(me?.Outcome);
        scoreLabel.AccessibleName = outcomeName.Length > 0 ? $"{outcomeName} {scoreLabel.Text}" : scoreLabel.Text;
        firstKillValue.Text = me?.FirstKills?.ToString() ?? "—";
        firstDeathValue.Text = me?.FirstDeaths?.ToString() ?? "—";
        var fk = me?.FirstKills ?? 0; var fd = me?.FirstDeaths ?? 0;
        firstKillRateValue.Text = fk + fd > 0 ? $"{100d * fk / (fk + fd):0}%" : "—";
        kdDiffValue.Text = me?.KillDeathDifference?.ToString() ?? "—";

        RenderScoreboard(match);
        roundsPageIndex = 0;
        RenderRoundPage();
        detailNote.Text = NeedsKillTimeRefresh(match) ? "ファーストキル・デスはオンライン更新で取得" : "";
        detailNote.Visible = detailNote.Text.Length > 0;
        RenderWeaponBreakdown(match);
    }

    private string DetailTitle(MatchAnalytics match) =>
        $"{MapName(match.Info.MapId)}  ·  {QueueName(MatchAnalyticsEngine.DisplayQueueId(match.Match))}  ·  {AgentName(match.FindPlayer(HistorySubject)?.CharacterId ?? "")}";

    private void RenderScoreboard(MatchAnalytics match)
    {
        using var redraw = PageRedrawScope.Begin(scoreboard);
        var content = (match, DeviceDpi);
        var height = scoreboard.ClientSize.Height;
        var headerHeight = NativePageStyle.TextHeight(this, NativePageStyle.SmallBold, verticalPadding: UiMetrics.TightGap);
        var playerCount = Math.Max(1, match.Players.Count);
        var availableRowsHeight = Math.Max(0, height - headerHeight);
        var naturalRowHeight = availableRowsHeight / playerCount;
        var twoLineMinimum = NativePageStyle.TextHeight(this, NativePageStyle.Reading, lines: 2, verticalPadding: UiMetrics.TightGap);
        var oneLineMinimum = Math.Max(
            NativePageStyle.TextHeight(this, NativePageStyle.Reading, verticalPadding: UiMetrics.ControlGap),
            UiMetrics.Px(this, 35));
        var lineMode = 0;
        if (naturalRowHeight >= twoLineMinimum) lineMode = 2;
        else if (naturalRowHeight >= oneLineMinimum) lineMode = 1;
        UpdateScoreboardColumnWidths();
        var columnsKey = string.Join(",", visibleScoreboardColumns);
        var replaceRows = scoreboardContent != content || scoreboardLineMode != lineMode || scoreboardRenderedColumns != columnsKey;
        if (!replaceRows && scoreboardHeight == height) return;
        scoreboardContent = content;
        scoreboardHeight = height;
        scoreboardLineMode = lineMode;
        scoreboardRenderedColumns = columnsKey;
        UpdateScoreboardColumnWidths();
        scoreboard.SuspendLayout();
        try
        {
            if (replaceRows) BuildScoreboardRows(match, lineMode);

            // Rows always share the visible height exactly. Forcing a minimum row height here made ten-player
            // modes overflow on scaled displays, which showed a vertical scrollbar and, because that scrollbar
            // narrows the client area, a horizontal one as well. Compact mode (lineMode 0) lays the cell out
            // for whatever height it gets, so fitting is preferable to scrolling.
            scoreboard.RowStyles[0].Height = headerHeight;
            var remainder = availableRowsHeight - naturalRowHeight * playerCount;
            for (var i = 1; i < scoreboard.RowStyles.Count; i++)
                scoreboard.RowStyles[i].Height = naturalRowHeight + (i <= remainder ? 1 : 0);
        }
        finally { scoreboard.ResumeLayout(); }
        UpdateScoreboardColumnWidths();
    }

    private void ClearScoreboardRows()
    {
        foreach (var row in scoreboardRows.Values)
            foreach (var cell in row)
            {
                scoreboardToolTips.SetToolTip(cell, null);
                foreach (Control child in cell.Controls) scoreboardToolTips.SetToolTip(child, null);
                cell.Dispose();
            }
        scoreboardRows.Clear();
        while (scoreboard.RowStyles.Count > 1)
            scoreboard.RowStyles.RemoveAt(scoreboard.RowStyles.Count - 1);
        scoreboard.RowCount = 1;
    }

    private void BuildScoreboardRows(MatchAnalytics match, int lineMode)
    {
        ClearScoreboardRows();
        var ordered = OrderScoreboardPlayers(match.Players).ToArray();
        var teamTags = ScoreboardTeamTags(match);
        // Shared cached images (owned by RankImages, never disposed here). Reserve the rank slot for every row
        // when any player has an image so the text stays aligned; skip it entirely when none do.
        var rankImages = ordered.Select(player => RankImages.Get(player.CompetitiveTier)).ToArray();
        var reserveRankSlot = Array.Exists(rankImages, image => image is not null);
        scoreboard.RowCount = ordered.Length + 1;
        for (var i = 0; i < ordered.Length; i++)
        {
            var player = ordered[i];
            var rankName = RankName(player.CompetitiveTier);
            var values = Enumerable.Range(0, ScoreboardHeaders.Length).Select(column => ScoreboardValue(player, column)).ToArray();
            scoreboard.RowStyles.Add(new RowStyle(SizeType.Absolute));
            var background = ScoreboardRowColor(player, i + 1);
            var cells = new Control[values.Length];
            teamTags.TryGetValue(player.TeamId, out var teamTag);
            var accessibleStats = string.Join(" · ", Enumerable.Range(1, values.Length - 1)
                .Select(column => $"{ScoreboardHeaders[column]} {values[column]}"));
            var hiddenStats = ScoreboardMetricPriority.Where(column => !visibleScoreboardColumns.Contains(column)).ToArray();
            var rowDetail = lineMode == 2 && hiddenStats.Length > 0
                ? string.Join(" · ", hiddenStats.Take(2).Select(column => $"{ScoreboardHeaders[column]} {values[column]}"))
                : $"{AgentName(player.CharacterId)}  ·  {rankName}";
            var cellText = new PlayerCellText(values[0], $"{AgentName(player.CharacterId)}  ·  {rankName}", rowDetail, accessibleStats);
            var cellImages = new PlayerCellImages(player.CharacterId, rankImages[i], rankName, reserveRankSlot);
            cells[0] = CreatePlayerCell(cellText, cellImages, background, teamTag, lineMode);
            for (var column = 1; column < values.Length; column++)
            {
                var cell = NativePageStyle.Label(values[column], NativePageStyle.Numeric, values[column] == "—" ? Theme.Muted : Theme.Text);
                cell.AutoSize = false;
                cell.Dock = DockStyle.Fill;
                cell.BackColor = background;
                cell.Padding = new Padding(UiMetrics.Px(this, UiMetrics.TightGap), 0, UiMetrics.Px(this, UiMetrics.ControlGap), 0);
                cell.TextAlign = ContentAlignment.MiddleRight;
                cell.AutoEllipsis = true;
                cells[column] = cell;
            }
            for (var column = 0; column < cells.Length; column++)
                scoreboard.Controls.Add(cells[column], column, i + 1);
            scoreboardRows.Add(player, cells);
        }
    }

    private Dictionary<string, (Color Color, string? Label)> ScoreboardTeamTags(MatchAnalytics match)
    {
        var tags = new Dictionary<string, (Color Color, string? Label)>(StringComparer.OrdinalIgnoreCase);
        var teams = match.Teams.Where(team => team.PlayerCount > 0).ToArray();
        if (teams.Length != 2 || match.Info.QueueId.Equals("deathmatch", StringComparison.OrdinalIgnoreCase))
            return tags;

        var myTeam = match.FindPlayer(HistorySubject)?.TeamId;
        var friendly = Array.FindIndex(teams, team => team.TeamId.Equals(myTeam, StringComparison.OrdinalIgnoreCase));
        if (friendly >= 0)
        {
            tags.Add(teams[friendly].TeamId, (FriendlyTeamColor, "味方チーム"));
            tags.Add(teams[1 - friendly].TeamId, (OpposingTeamColor, "相手チーム"));
        }
        else
        {
            // Without the current player, keep the teams distinct without guessing which is friendly.
            var blue = Array.FindIndex(teams, team => team.TeamId.Equals("Blue", StringComparison.OrdinalIgnoreCase));
            if (blue < 0) blue = 0;
            tags.Add(teams[blue].TeamId, (FriendlyTeamColor, "青チーム"));
            tags.Add(teams[1 - blue].TeamId, (OpposingTeamColor, "赤チーム"));
        }
        return tags;
    }

    /// <summary>
    /// First scoreboard column: team tag, agent icon, rank icon, then two text lines (player name / agent · rank).
    /// Nothing overlaps: each element gets its own horizontal slot. Icon slots keep a fixed width even when no
    /// image exists so the text stays aligned across rows, and they collapse (rank first) when the column is too
    /// narrow so the text always remains visible. Each text line ellipsizes on its own and shows the full text
    /// as a tooltip when cut. Child controls only get a BackColor; the team tag is painted by the cell itself
    /// in an area no child covers, so re-colouring rows on sort never paints over it.
    /// </summary>
    private sealed record PlayerCellText(string Name, string Detail, string RowDetail, string AccessibleStats);
    private sealed record PlayerCellImages(string CharacterId, Image? RankImage, string RankName, bool ReserveRankSlot);

    private Control CreatePlayerCell(PlayerCellText text, PlayerCellImages images, Color background,
        (Color Color, string? Label) teamTag, int lineMode)
    {
        var (name, detail, rowDetail, accessibleStats) = text;
        var (characterId, rankImage, rankName, reserveRankSlot) = images;
        var cell = new Panel
        {
            Dock = DockStyle.Fill, BackColor = background, Margin = Padding.Empty, Padding = Padding.Empty,
            AccessibleRole = AccessibleRole.ListItem, AccessibleName = name,
            AccessibleDescription = string.Join(" · ", new[] { teamTag.Label, detail, accessibleStats }.Where(value => !string.IsNullOrWhiteSpace(value)))
        };
        scoreboardToolTips.SetToolTip(cell, cell.AccessibleDescription);
        var icon = new SharedImageBox { Image = AgentIcon(characterId), BackColor = background, AccessibleName = AgentName(characterId) };
        var rankIcon = new SharedImageBox { Image = rankImage, BackColor = background, AccessibleName = rankName };
        Label TextLine(string text)
        {
            var line = NativePageStyle.Label(text, NativePageStyle.Reading, Theme.Text);
            line.AutoSize = false;
            line.AutoEllipsis = true;
            line.BackColor = background;
            line.Padding = new Padding(UiMetrics.Px(this, UiMetrics.TightGap), 0, UiMetrics.Px(this, UiMetrics.ControlGap), 0);
            return line;
        }
        var nameLabel = TextLine(name);
        nameLabel.AccessibleDescription = teamTag.Label;
        var detailLabel = TextLine(rowDetail);
        foreach (var child in new Control[] { icon, rankIcon, nameLabel, detailLabel }) scoreboardToolTips.SetToolTip(child, cell.AccessibleDescription);
        detailLabel.Visible = lineMode == 2;
        cell.Controls.Add(nameLabel);
        cell.Controls.Add(detailLabel);
        cell.Controls.Add(icon);
        cell.Controls.Add(rankIcon);
        var tagBounds = Rectangle.Empty;
        cell.Paint += (_, e) =>
        {
            if (tagBounds.IsEmpty) return;
            var color = teamTag.Label is "味方チーム" or "青チーム" ? Theme.Friendly : Theme.Opposing;
            using var brush = new SolidBrush(color);
            e.Graphics.FillRectangle(brush, tagBounds);
        };
        void Place()
        {
            var inset = UiMetrics.Px(cell, UiMetrics.TightGap);
            var width = cell.ClientSize.Width;
            var height = cell.ClientSize.Height;
            var tagWidth = UiMetrics.Px(cell, UiMetrics.TightGap);
            var tagHeight = Math.Min(UiMetrics.Px(cell, UiMetrics.ButtonHeight), height - inset * 2);
            tagBounds = !teamTag.Color.IsEmpty && tagHeight > 0 && width > tagWidth + inset * 2
                ? new Rectangle(inset, (height - tagHeight) / 2, tagWidth, tagHeight)
                : Rectangle.Empty;
            var iconLeft = tagBounds.IsEmpty ? inset : tagBounds.Right + inset;
            var iconLimit = height - inset * 2;
            var minimumIcon = UiMetrics.Px(cell, 20);
            var size = Math.Min(Math.Min(UiMetrics.Px(cell, 40), iconLimit), (width - iconLeft) / 3);
            var showIcon = size >= minimumIcon;
            icon.Visible = showIcon;
            var textLeft = tagBounds.IsEmpty ? 0 : iconLeft;
            var rankLeft = iconLeft;
            if (showIcon)
            {
                icon.Bounds = new Rectangle(iconLeft, Math.Max(0, (height - size) / 2), size, size);
                textLeft = icon.Right;
                rankLeft = icon.Right + UiMetrics.Px(cell, UiMetrics.TightGap);
            }
            // The rank badge is a little smaller than the agent portrait and may take at most a quarter of the
            // remaining width, so the name and rank text keep most of the column.
            var rankSize = reserveRankSlot ? Math.Min(Math.Min(UiMetrics.Px(cell, 30), iconLimit), (width - rankLeft) / 4) : 0;
            var showRank = rankSize >= minimumIcon;
            rankIcon.Visible = showRank;
            if (showRank)
            {
                rankIcon.Bounds = new Rectangle(rankLeft, Math.Max(0, (height - rankSize) / 2), rankSize, rankSize);
                textLeft = rankIcon.Right;
            }
            var textWidth = Math.Max(0, width - textLeft);
            var lineHeight = Math.Min(NativePageStyle.TextHeight(cell, nameLabel.Font), Math.Max(0, height / (lineMode == 2 ? 2 : 1)));
            var lines = lineMode == 2 ? 2 : 1;
            var top = Math.Max(0, (height - lineHeight * lines) / 2);
            nameLabel.Bounds = new Rectangle(textLeft, top, textWidth, lineHeight);
            detailLabel.Bounds = lineMode == 2
                ? new Rectangle(textLeft, top + lineHeight, textWidth, lineHeight)
                : Rectangle.Empty;
            cell.Invalidate();
        }
        cell.Resize += (_, _) => Place();
        Place();
        return cell;
    }

    private Image? AgentIcon(string characterId)
    {
        // Embedded resource names are lower-case UUIDs; normalise ids that arrive in another casing/format.
        var id = agents.TryGetValue(characterId, out var agent) ? agent.Id : characterId;
        return Guid.TryParse(id, out var guid) ? AgentImages.Get(guid.ToString("D"), "icon") : null;
    }

    /// <summary>
    /// Draws a shared, cached image (agent or rank icon) with high-quality scaling. The image belongs to its
    /// cache (AgentImages / RankImages), so this control never disposes it; a null image leaves the slot empty.
    /// </summary>
    private sealed class SharedImageBox : Control
    {
        private Image? image;

        public SharedImageBox()
        {
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            SetStyle(ControlStyles.Selectable, false);
            Margin = Padding.Empty;
        }

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public Image? Image
        {
            get => image;
            set { image = value; Invalidate(); }
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            if (image is null || image.Width <= 0 || image.Height <= 0 || ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
            var scale = Math.Min((double)ClientSize.Width / image.Width, (double)ClientSize.Height / image.Height);
            var width = Math.Max(1, (int)Math.Round(image.Width * scale));
            var height = Math.Max(1, (int)Math.Round(image.Height * scale));
            var destination = new Rectangle((ClientSize.Width - width) / 2, (ClientSize.Height - height) / 2, width, height);
            e.Graphics.InterpolationMode = System.Drawing.Drawing2D.InterpolationMode.HighQualityBicubic;
            e.Graphics.PixelOffsetMode = System.Drawing.Drawing2D.PixelOffsetMode.HighQuality;
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.HighQuality;
            try { e.Graphics.DrawImage(image, destination); }
            catch (Exception ex) when (ex is ArgumentException or System.Runtime.InteropServices.ExternalException)
            { /* Ignore invalid image data and keep the rest of the row visible. */ }
        }
    }

    private void RenderRoundPage()
    {
        var cardWidth = UiMetrics.Px(this, 220);
        var textHeight = NativePageStyle.TextHeight(this, NativePageStyle.Reading, lines: 3, verticalPadding: 8);
        var cardHeight = Math.Max(UiMetrics.Px(this, 96), textHeight + UiMetrics.Px(this, 12));
        var columns = Math.Max(1, rounds.ClientSize.Width / (cardWidth + UiMetrics.Px(this, 6)));
        var rows = Math.Max(1, rounds.ClientSize.Height / (cardHeight + UiMetrics.Px(this, 6)));
        var pageSize = Math.Max(1, columns * rows);
        var pageCount = Math.Max(1, ((detailMatch?.Rounds.Count ?? 0) + pageSize - 1) / pageSize);
        roundsPageIndex = Math.Clamp(roundsPageIndex, 0, pageCount - 1);
        var renderKey = (detailMatch, rounds.ClientSize, roundsPageIndex, DeviceDpi);
        if (roundsRenderKey == renderKey) return;
        roundsRenderKey = renderKey;
        using var redraw = PageRedrawScope.Begin(rounds);
        rounds.SuspendLayout();
        DisposeChildren(rounds);
        if (detailMatch is not { } match || match.Rounds.Count == 0)
        {
            roundsPageLabel.Text = "1 / 1";
            roundsPrevious.Enabled = roundsNext.Enabled = false;
            rounds.ResumeLayout();
            return;
        }
        var start = roundsPageIndex * pageSize;
        foreach (var (round, index) in match.Rounds.Select((value, i) => (value, i)).Skip(start).Take(pageSize))
        {
            var card = NativePageStyle.Card();
            card.Dock = DockStyle.None;
            card.Size = new Size(Math.Max(UiMetrics.Px(this, 96), Math.Min(cardWidth, rounds.ClientSize.Width / columns - UiMetrics.Px(this, 6))), cardHeight);
            card.Margin = new Padding(UiMetrics.Px(this, 3));
            card.Padding = new Padding(UiMetrics.Px(this, 6), UiMetrics.Px(this, 3), UiMetrics.Px(this, 6), UiMetrics.Px(this, 3));
            var first = round.FirstKiller is { Length: > 0 } ? $"ファーストキル {PlayerName(round.FirstKiller, match)}" : "ファーストキル情報なし";
            var text = NativePageStyle.Label($"ラウンド {(round.RoundNum ?? index) + 1}\n{RoundResultName(round.RoundResult)}  ·  {round.KillCount} キル\n{first}", NativePageStyle.Reading, Theme.Text);
            text.AutoSize = false; text.AutoEllipsis = false; text.TextAlign = ContentAlignment.MiddleLeft;
            text.Dock = DockStyle.Fill; card.Controls.Add(text); rounds.Controls.Add(card);
        }
        roundsPageLabel.Text = $"{roundsPageIndex + 1} / {pageCount}";
        roundsPrevious.Enabled = roundsPageIndex > 0;
        roundsNext.Enabled = roundsPageIndex + 1 < pageCount;
        roundsPrevious.Visible = roundsNext.Visible = pageCount > 1;
        rounds.ResumeLayout();
    }

    private void RenderBreakdowns(Control host, IReadOnlyList<HistoryBreakdownEntry> entries, IReadOnlyList<PlayerHistoryEntry> rows, bool map)
    {
        var textHeight = NativePageStyle.TextHeight(this, NativePageStyle.Reading, verticalPadding: 3);
        var trackHeight = Math.Max(4, UiMetrics.Px(this, 5));
        var contentHeight = textHeight + UiMetrics.Px(this, 5) + trackHeight;
        var minimumRowHeight = Math.Max(UiMetrics.Px(this, 50), contentHeight + UiMetrics.Px(this, 8));
        var pageSize = Math.Max(1, host.ClientSize.Height / minimumRowHeight);
        var pageCount = Math.Max(1, (entries.Count + pageSize - 1) / pageSize);
        var pageIndex = map ? mapPageIndex : agentPageIndex;
        pageIndex = Math.Clamp(pageIndex, 0, pageCount - 1);
        if (map) mapPageIndex = pageIndex; else agentPageIndex = pageIndex;
        var renderKey = (entries, rows, host.ClientSize, pageIndex, DeviceDpi);
        if (map ? mapRenderKey == renderKey : agentRenderKey == renderKey) return;
        if (map) mapRenderKey = renderKey; else agentRenderKey = renderKey;
        using var redraw = PageRedrawScope.Begin(host);
        SuspendFlow(host);
        DisposeChildren(host);
        var visibleEntries = entries.Skip(pageIndex * pageSize).Take(pageSize).ToArray();
        var rowHeight = Math.Max(minimumRowHeight, host.ClientSize.Height / Math.Max(1, visibleEntries.Length));
        foreach (var item in visibleEntries)
        {
            var grouped = rows.Where(row => (map ? row.MapId : row.Stats.CharacterId).Equals(item.Key, StringComparison.OrdinalIgnoreCase)).ToArray();
            var averageAcs = Average(grouped.Select(row => row.Stats.AverageCombatScore));
            var name = map ? MapName(item.Key) : AgentName(item.Key);
            var rowControl = new Panel { Height = rowHeight - UiMetrics.Px(this, 3), Width = Math.Max(UiMetrics.Px(this, 200), host.ClientSize.Width - UiMetrics.Px(this, 24)), BackColor = Theme.Surface, Margin = new Padding(0, 0, 0, UiMetrics.Px(this, 3)) };
            var record = item.Draws > 0 ? $"{item.Wins}勝 {item.Losses}敗 {item.Draws}分" : $"{item.Wins}勝 {item.Losses}敗";
            var label = NativePageStyle.Label($"{name}  ·  {item.Matches}戦 ({record})  ·  ACS {Number(averageAcs, 0)}", NativePageStyle.Reading, Theme.Text);
            label.AutoEllipsis = true;
            var performanceColor = PerformanceColor(item.WinRatePercent, 50);
            var percent = NativePageStyle.Label(Percent(item.WinRatePercent), NativePageStyle.ReadingBold, performanceColor);
            percent.TextAlign = ContentAlignment.MiddleRight;
            var track = new Panel { BackColor = Theme.Elevated, Height = trackHeight, Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom };
            var fill = new Panel { BackColor = performanceColor, Height = trackHeight, Width = 0, Dock = DockStyle.Left };
            track.Controls.Add(fill);
            rowControl.Controls.Add(label); rowControl.Controls.Add(percent); rowControl.Controls.Add(track);
            // Agent rows lead with the agent icon. The image comes from the shared AgentImages cache, so the
            // SharedImageBox never disposes it when the row is rebuilt. Map rows keep their text-only layout.
            var agentIcon = map ? null : new SharedImageBox { Image = AgentIcon(item.Key), BackColor = Theme.Surface, AccessibleName = name };
            if (agentIcon is not null) rowControl.Controls.Add(agentIcon);
            void LayoutRow()
            {
                var contentTop = Math.Max(0, (rowControl.ClientSize.Height - contentHeight) / 2);
                var contentLeft = UiMetrics.Px(this, 2);
                if (agentIcon is not null)
                {
                    var iconSize = Math.Min(UiMetrics.Px(this, 36), Math.Max(0, rowControl.ClientSize.Height - UiMetrics.Px(this, 4)));
                    agentIcon.Visible = iconSize >= UiMetrics.Px(this, 14);
                    if (agentIcon.Visible)
                    {
                        agentIcon.Bounds = new Rectangle(contentLeft, Math.Max(0, (rowControl.ClientSize.Height - iconSize) / 2), iconSize, iconSize);
                        contentLeft = agentIcon.Right + UiMetrics.Px(this, 8);
                    }
                }
                label.Bounds = new Rectangle(contentLeft, contentTop, Math.Max(UiMetrics.Px(this, 80), rowControl.ClientSize.Width - contentLeft - UiMetrics.Px(this, 94)), textHeight);
                percent.Bounds = new Rectangle(Math.Max(0, rowControl.ClientSize.Width - UiMetrics.Px(this, 92)), contentTop, UiMetrics.Px(this, 88), textHeight);
                track.Bounds = new Rectangle(contentLeft, contentTop + textHeight + UiMetrics.Px(this, 5), Math.Max(UiMetrics.Px(this, 40), rowControl.ClientSize.Width - contentLeft - UiMetrics.Px(this, 2)), trackHeight);
                fill.Width = (int)Math.Round(track.ClientSize.Width * Math.Clamp((item.WinRatePercent ?? 0) / 100d, 0, 1));
            }
            rowControl.Resize += (_, _) => LayoutRow();
            LayoutRow();
            host.Controls.Add(rowControl);
        }
        if (entries.Count == 0)
        {
            var empty = NativePageStyle.Label("データがありません。", NativePageStyle.Reading, Theme.Muted);
            empty.AutoSize = false;
            empty.TextAlign = ContentAlignment.MiddleCenter;
            empty.Size = new Size(Math.Max(UiMetrics.Px(this, 200), host.ClientSize.Width - UiMetrics.Px(this, 24)), Math.Max(UiMetrics.Px(this, 40), host.ClientSize.Height - UiMetrics.Px(this, 4)));
            host.Controls.Add(empty);
        }
        var previous = map ? mapPrevious : agentPrevious;
        var next = map ? mapNext : agentNext;
        var page = map ? mapPageLabel : agentPageLabel;
        page.Text = $"{pageIndex + 1} / {pageCount}";
        previous.Enabled = pageIndex > 0;
        next.Enabled = pageIndex + 1 < pageCount;
        previous.Visible = next.Visible = pageCount > 1;
        host.ResumeLayout();
    }

    private void RenderWeaponBreakdown(MatchAnalytics match)
    {
        weaponMatch = match;
        weaponPageIndex = 0;
        RenderWeaponBreakdown();
    }

    private void RenderWeaponBreakdown()
    {
        var items = weaponMatch?.FindPlayer(HistorySubject)?.WeaponKills.Where(x => x.Kills > 0).OrderByDescending(x => x.Kills).ToArray() ?? [];
        var maxKills = Math.Max(6, items.FirstOrDefault()?.Kills ?? 0);
        var textHeight = NativePageStyle.TextHeight(this, NativePageStyle.Reading, verticalPadding: 3);
        var trackHeight = Math.Max(4, UiMetrics.Px(this, 5));
        var contentHeight = textHeight + UiMetrics.Px(this, 5) + trackHeight;
        var minimumRowHeight = Math.Max(UiMetrics.Px(this, 50), contentHeight + UiMetrics.Px(this, 8));
        var pageSize = Math.Max(1, weaponRows.ClientSize.Height / minimumRowHeight);
        var pageCount = Math.Max(1, (items.Length + pageSize - 1) / pageSize);
        weaponPageIndex = Math.Clamp(weaponPageIndex, 0, pageCount - 1);
        var renderKey = (weaponMatch, weaponRows.ClientSize, weaponPageIndex, DeviceDpi);
        if (weaponRenderKey == renderKey) return;
        weaponRenderKey = renderKey;
        using var redraw = PageRedrawScope.Begin(weaponRows);
        SuspendFlow(weaponRows); DisposeChildren(weaponRows);
        var visibleItems = items.Skip(weaponPageIndex * pageSize).Take(pageSize).ToArray();
        var rowHeight = Math.Max(minimumRowHeight, weaponRows.ClientSize.Height / Math.Max(1, visibleItems.Length));
        foreach (var item in visibleItems)
        {
            var rowControl = new Panel { Height = rowHeight - UiMetrics.Px(this, 3), Width = Math.Max(UiMetrics.Px(this, 140), weaponRows.ClientSize.Width - UiMetrics.Px(this, 20)), BackColor = Theme.Surface, Margin = new Padding(0, 0, 0, UiMetrics.Px(this, 3)) };
            var label = NativePageStyle.Label(WeaponName(item.DamageItem), NativePageStyle.Reading, Theme.Text);
            var count = NativePageStyle.Label($"{item.Kills} キル", NativePageStyle.ReadingBold, Theme.Text);
            count.TextAlign = ContentAlignment.MiddleRight;
            var track = new Panel { BackColor = Theme.Elevated, Height = trackHeight, Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom };
            var fill = new Panel { BackColor = Theme.Red, Height = trackHeight, Width = 0, Dock = DockStyle.Left };
            track.Controls.Add(fill); rowControl.Controls.Add(label); rowControl.Controls.Add(count); rowControl.Controls.Add(track);
            void LayoutRow()
            {
                var contentTop = Math.Max(0, (rowControl.ClientSize.Height - contentHeight) / 2);
                label.Bounds = new Rectangle(0, contentTop, Math.Max(UiMetrics.Px(this, 30), rowControl.ClientSize.Width - UiMetrics.Px(this, 88)), textHeight);
                count.Bounds = new Rectangle(Math.Max(0, rowControl.ClientSize.Width - UiMetrics.Px(this, 84)), contentTop, UiMetrics.Px(this, 82), textHeight);
                track.Bounds = new Rectangle(0, contentTop + textHeight + UiMetrics.Px(this, 5), Math.Max(UiMetrics.Px(this, 35), rowControl.ClientSize.Width - UiMetrics.Px(this, 2)), trackHeight);
                fill.Width = (int)Math.Round(track.ClientSize.Width * Math.Clamp(item.Kills / (double)maxKills, 0, 1));
            }
            rowControl.Resize += (_, _) => LayoutRow();
            LayoutRow();
            weaponRows.Controls.Add(rowControl);
        }
        if (items.Length == 0)
        {
            var empty = NativePageStyle.Label("キル情報はありません。", NativePageStyle.Reading, Theme.Muted);
            empty.AutoSize = false;
            empty.TextAlign = ContentAlignment.MiddleCenter;
            empty.Size = new Size(Math.Max(UiMetrics.Px(this, 140), weaponRows.ClientSize.Width - UiMetrics.Px(this, 20)), Math.Max(UiMetrics.Px(this, 40), weaponRows.ClientSize.Height - UiMetrics.Px(this, 4)));
            weaponRows.Controls.Add(empty);
        }
        weaponPageLabel.Text = $"{weaponPageIndex + 1} / {pageCount}";
        weaponPrevious.Enabled = weaponPageIndex > 0;
        weaponNext.Enabled = weaponPageIndex + 1 < pageCount;
        weaponPrevious.Visible = weaponNext.Visible = pageCount > 1;
        weaponRows.ResumeLayout();
    }

    private void ResizeAnalysisRows(FlowLayoutPanel host, int horizontalPadding, int minimumWidth)
    {
        var width = Math.Max(UiMetrics.Px(this, minimumWidth), host.ClientSize.Width - UiMetrics.Px(this, horizontalPadding));
        foreach (Control child in host.Controls)
            if (child is Panel) child.Width = width;
            else if (child is Label label) label.Size = new Size(width, Math.Max(UiMetrics.Px(this, 40), host.ClientSize.Height - UiMetrics.Px(this, 4)));
    }

    private void RenderEmptyState(string message)
    {
        using var redraw = PageRedrawScope.Begin(this);
        historySkeleton.Loading = false;
        historyContent.Visible = false;
        historyEmptyText.Text = loginRequested is not null ? SignedOutEmptyText : SignedOutEmptyText + SignedOutHeaderHint;
        if (api.HasAccountSession) historyEmptyText.Text = message;
        historyEmptyText.Visible = true;
        orderedMatches = [];
        matchPageIndex = roundsPageIndex = mapPageIndex = agentPageIndex = weaponPageIndex = 0;
        detailMatch = weaponMatch = null;
        scoreboardContent = null;
        roundsRenderKey = weaponRenderKey = null;
        mapRenderKey = agentRenderKey = null;
        if (detailLayout is not null) detailLayout.Visible = false;
        detailEmpty.Text = message;
        detailEmpty.Visible = true;
        detailNote.Visible = false;
        matchCount.Text = "0 試合";
        detailTitle.Text = message;
        detailSubtitle.Text = "";
        scoreLabel.Text = "—";
        detailNote.Text = "";
        RenderMatchList(orderedMatches);
        scoreboard.SuspendLayout();
        try { ClearScoreboardRows(); }
        finally { scoreboard.ResumeLayout(); }
        DisposeChildren(rounds); DisposeChildren(mapRows); DisposeChildren(agentRows); DisposeChildren(weaponRows);
        var breakdownMessage = api.HasAccountSession ? "試合データがありません。" : "ログイン後に表示されます。";
        AddBreakdownPlaceholder(mapRows, breakdownMessage);
        AddBreakdownPlaceholder(agentRows, breakdownMessage);
        roundsPageLabel.Text = mapPageLabel.Text = agentPageLabel.Text = weaponPageLabel.Text = "1 / 1";
        roundsPrevious.Enabled = roundsNext.Enabled = mapPrevious.Enabled = mapNext.Enabled = agentPrevious.Enabled = agentNext.Enabled = weaponPrevious.Enabled = weaponNext.Enabled = false;
        roundsPrevious.Visible = roundsNext.Visible = mapPrevious.Visible = mapNext.Visible = agentPrevious.Visible = agentNext.Visible = weaponPrevious.Visible = weaponNext.Visible = false;
        firstKillValue.Text = firstDeathValue.Text = firstKillRateValue.Text = kdDiffValue.Text = "—";
        noFavorites.Visible = false;
        UpdateFavoriteButton();
        UpdateHistoryButtons();
        if (!api.HasAccountSession) pageStatus.Text = "";
        SetMetrics([("—", "勝敗"), ("—", "勝率"), ("—", "平均ACS"), ("—", "通算K/D"), ("—", "平均ADR"), ("—", "平均HS%"), ("—", "直近5戦"), ("—", "直近10戦")]);
        UpdateEmptyLogin();
    }

    private void ShowHistoryLoadError(Exception exception, string message)
    {
        NativePageStyle.LogPageError("MatchHistory", exception);
        SetStatus(message, error: true);
        if (matches.Count == 0)
        {
            historySkeleton.Loading = false;
            historyContent.Visible = false;
            historyEmptyText.Text = message;
            historyEmptyText.Visible = true;
        }
    }

    private static void AddBreakdownPlaceholder(FlowLayoutPanel host, string text)
    {
        var message = NativePageStyle.Label(text, NativePageStyle.Reading, Theme.Muted);
        message.AutoSize = false;
        message.TextAlign = ContentAlignment.MiddleCenter;
        message.BackColor = Theme.Surface;
        message.Size = new Size(Math.Max(120, host.ClientSize.Width - UiMetrics.Px(host, 8)), Math.Max(UiMetrics.Px(host, 40), host.ClientSize.Height - UiMetrics.Px(host, 4)));
        host.Controls.Add(message);
    }

    private void SetMetrics((string Value, string Caption)[] values)
    {
        for (var i = 0; i < metricLabels.Count && i < values.Length; i++)
        {
            metricLabels[i].Value.Text = values[i].Value;
            metricLabels[i].Caption.Text = values[i].Caption;
            metricLabels[i].Value.ForeColor = Theme.Text;
        }
    }

    private static Color PerformanceColor(double? value, double threshold)
    {
        if (value is not { } number || !double.IsFinite(number)) return Theme.Muted;
        return number >= threshold ? Theme.Green : Theme.Negative;
    }

    private void SetStatus(string message, bool error)
    {
        pageStatus.Text = IsProgressMessage(message) ? "" : message;
        pageStatus.ForeColor = error ? Theme.Red : Theme.Muted;
    }

    private static bool IsProgressMessage(string message) =>
        message.Contains("読み込み中", StringComparison.Ordinal) ||
        message.Contains("読み込んでいます", StringComparison.Ordinal) ||
        message.Contains("取得しています", StringComparison.Ordinal) ||
        message.Contains("更新しています", StringComparison.Ordinal);

    private async Task<IReadOnlyDictionary<string, string>> LoadWeaponNamesAsync(CancellationToken ct)
    {
        if (weapons.Count > 0) return weapons;
        try
        {
            using var response = await assets.GetAsync(WeaponCatalogUri, ct);
            response.EnsureSuccessStatusCode();
            using var document = JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
            if (!document.RootElement.TryGetProperty("data", out var data) || data.ValueKind != JsonValueKind.Array) return weapons;
            var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var item in data.EnumerateArray())
            {
                if (!item.TryGetProperty("uuid", out var id) || id.ValueKind != JsonValueKind.String ||
                    !item.TryGetProperty("displayName", out var name) || name.ValueKind != JsonValueKind.String) continue;
                var key = id.GetString() ?? ""; var value = name.GetString() ?? "";
                if (Guid.TryParse(key, out _) && value.Length > 0) result[key] = value;
            }
            if (result.Count > 0) return result;
        }
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException or InvalidOperationException)
        { /* Continue with previously cached weapon names. */ }
        return weapons;
    }

    private string PlayerName(string? subject, MatchAnalytics match)
    {
        if (string.IsNullOrWhiteSpace(subject)) return "";
        var player = match.Players.FirstOrDefault(p => p.Subject.Equals(subject, StringComparison.OrdinalIgnoreCase));
        return player?.DisplayName.Length > 0 ? player.DisplayName : "名前非公開";
    }

    private static string OutcomeName(MatchOutcome? outcome) => outcome switch
    {
        MatchOutcome.Win => "勝利", MatchOutcome.Loss => "敗北", MatchOutcome.Draw => "引き分け", _ => ""
    };

    private static Color OutcomeColor(MatchOutcome? outcome) => outcome switch
    {
        MatchOutcome.Win => Theme.Green, MatchOutcome.Loss => Theme.Red, MatchOutcome.Draw => DrawColor, _ => Theme.Muted
    };

    private static string TeamScore(MatchAnalytics match, PlayerMatchStats? me)
    {
        var team = match.Teams.FirstOrDefault(t => t.TeamId.Equals(me?.TeamId, StringComparison.OrdinalIgnoreCase));
        return team?.RoundsWon is { } won ? $"{won}:{team.RoundsLost?.ToString() ?? "—"}" : "—";
    }

    private string AgentName(string id) => agents.TryGetValue(id, out var agent) ? agent.Name : "不明なエージェント";
    private static string RankName(int? tier) => tier switch
    {
        null => "ランク不明",
        >= 0 and <= 2 => "ランクなし",
        >= 3 and <= 26 => $"{new[] { "アイアン", "ブロンズ", "シルバー", "ゴールド", "プラチナ", "ダイヤモンド", "アセンダント", "イモータル" }[(tier.Value - 3) / 3]} {(tier.Value - 3) % 3 + 1}",
        27 => "レディアント",
        _ => "ランク不明"
    };
    private string WeaponName(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return "不明";
        if (weapons.TryGetValue(id, out var name)) return name;
        return id.Trim().ToLowerInvariant() switch
        {
            "ultimate" => "アルティメット",
            "ability1" => "アビリティ 1",
            "ability2" => "アビリティ 2",
            "grenadeability" => "グレネードアビリティ",
            "primary" => "固有アビリティ",
            _ when Guid.TryParse(id, out _) => "武器",
            _ => id
        };
    }

    private static string RoundResultName(string result) => result.Trim().ToLowerInvariant() switch
    {
        "eliminated" or "elimination" => "チーム全滅",
        "bomb defused" or "defuse" => "スパイク解除",
        "bomb detonated" or "detonate" => "スパイク爆発",
        "round timer expired" or "timerexpired" => "時間切れ",
        "surrendered" or "surrender" => "降参",
        "" => "結果不明",
        _ => result
    };
    private static string MapName(string id) => string.IsNullOrWhiteSpace(id) ? "不明なマップ" : MapCatalog.Name(id);
    private static string QueueName(string id) => id.ToLowerInvariant() switch
    {
        "competitive" => "コンペティティブ", "unrated" => "アンレート", "swiftplay" => "スイフトプレイ",
        "spikerush" => "スパイクラッシュ", "deathmatch" => "デスマッチ", "teamdeathmatch" or "hurm" => "チームデスマッチ",
        "skirmish" => "スカーミッシュ",
        "escalation" or "ggteam" => "エスカレーション", "replication" => "レプリケーション", "premier" => "プレミア",
        "custom" => "カスタム", "onefa" => "アビスドラフトアリーナ", _ => id.Length == 0 ? "モード不明" : id
    };
    private static string Number(double? value, int digits) => value is { } number && double.IsFinite(number) ? number.ToString($"F{digits}") : "—";
    private static string Percent(double? value) => value is { } number && double.IsFinite(number) ? $"{number:0.0}%" : "—";
    private static double? Average(IEnumerable<double?> values)
    {
        var present = values.Where(v => v.HasValue && double.IsFinite(v.Value)).Select(v => v!.Value).ToArray();
        return present.Length == 0 ? null : present.Average();
    }
    private static void DisposeChildren(Control parent)
    {
        foreach (Control child in parent.Controls.Cast<Control>().ToArray()) { parent.Controls.Remove(child); child.Dispose(); }
    }
    private static void SuspendFlow(Control control) => control.SuspendLayout();
}
