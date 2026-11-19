using System.Net.Http.Json;
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
    private readonly List<MatchAnalytics> matches = [];
    private readonly Dictionary<string, int> matchPositions = new(StringComparer.OrdinalIgnoreCase);
    private readonly List<(Label Value, Label Caption)> metricLabels = [];
    private readonly Panel matchList = new() { Dock = DockStyle.Fill, BackColor = Theme.Surface, Margin = Padding.Empty };
    private readonly Panel matchRows = new() { Dock = DockStyle.Fill, BackColor = Theme.Surface, Margin = Padding.Empty };
    private readonly Dictionary<MatchAnalytics, Control> visibleMatchRows = new(ReferenceEqualityComparer.Instance);
    private readonly TableLayoutPanel scoreboard = new() { Dock = DockStyle.Fill, BackColor = Theme.Surface, Margin = Padding.Empty, CellBorderStyle = TableLayoutPanelCellBorderStyle.None };
    private readonly FlowLayoutPanel rounds = new() { Dock = DockStyle.Fill, AutoScroll = false, WrapContents = true, FlowDirection = FlowDirection.LeftToRight, BackColor = Theme.Surface, Margin = Padding.Empty };
    private readonly FlowLayoutPanel mapRows = new() { Dock = DockStyle.Fill, AutoScroll = false, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Theme.Surface, Margin = Padding.Empty };
    private readonly FlowLayoutPanel agentRows = new() { Dock = DockStyle.Fill, AutoScroll = false, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Theme.Surface, Margin = Padding.Empty };
    private readonly FlowLayoutPanel weaponRows = new() { Dock = DockStyle.Fill, AutoScroll = false, FlowDirection = FlowDirection.TopDown, WrapContents = false, BackColor = Theme.Surface, Margin = Padding.Empty };
    private readonly ThemedButton matchPrevious = new() { Text = "前へ", Size = new(76, 30), Compact = true };
    private readonly ThemedButton matchNext = new() { Text = "次へ", Size = new(76, 30), Compact = true };
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
    private readonly ThemedButton historyTab = new() { Text = "試合履歴", Size = new(144, 36), Compact = true, Primary = true };
    private readonly ThemedButton careerTab = new() { Text = "成績分析", Size = new(144, 36), Compact = true };
    private readonly ThemedButton mapTab = new() { Text = "マップ別", Size = new(128, 34), Compact = true, Primary = true };
    private readonly ThemedButton agentTab = new() { Text = "エージェント別", Size = new(164, 34), Compact = true };
    private readonly ThemedButton scoreboardTab = new() { Text = "スコアボード", Size = new(156, 34), Compact = true, Primary = true };
    private readonly ThemedButton roundsTab = new() { Text = "ラウンド別", Size = new(136, 34), Compact = true };
    private readonly ThemedButton matchAnalysisTab = new() { Text = "ファースト・武器", Size = new(184, 34), Compact = true };
    private readonly Label weaponPageLabel = NativePageStyle.Label("1 / 1", NativePageStyle.Small, Theme.Muted);
    private readonly Label pageStatus = NativePageStyle.Label("Riotアカウントにログインすると、試合履歴を表示します。", NativePageStyle.Small, Theme.Muted);
    private readonly Label listTitle = NativePageStyle.Label("試合履歴", NativePageStyle.SmallBold);
    private readonly ThemedButton favoritesFilter = new() { Text = "お気に入り", Size = new(130, 32), Compact = true };
    private readonly ThemedButton favoriteButton = new() { Text = "☆ お気に入り", Size = new(152, 34), Compact = true };
    private readonly Label noFavorites = NativePageStyle.Label("お気に入り登録した試合がここに表示されます。", NativePageStyle.Reading, Theme.Muted);
    private readonly Label matchCount = NativePageStyle.Label("0 試合", NativePageStyle.Small, Theme.Muted);
    private readonly Label detailTitle = NativePageStyle.Label("試合を選ぶと、スコアボードとラウンド分析を表示します。", NativePageStyle.Subheading);
    private readonly Label detailSubtitle = NativePageStyle.Label("", NativePageStyle.Small, Theme.Muted);
    private readonly Label scoreLabel = NativePageStyle.Label("—", NativePageStyle.Heading, Theme.Green);
    private readonly Label detailNote = NativePageStyle.Label("", NativePageStyle.Small, Theme.Muted);
    private readonly Label mapInsight = NativePageStyle.Label("マップ別の成績", NativePageStyle.Small, Theme.Muted);
    private readonly Label agentInsight = NativePageStyle.Label("エージェント別の成績", NativePageStyle.Small, Theme.Muted);
    private readonly Label openerInsight = NativePageStyle.Label("ファーストキル / ファーストデス", NativePageStyle.Small, Theme.Muted);
    private readonly Label firstKillValue = NativePageStyle.Label("—", NativePageStyle.Metric, Theme.Text);
    private readonly Label firstDeathValue = NativePageStyle.Label("—", NativePageStyle.Metric, Theme.Text);
    private readonly Label firstKillRateValue = NativePageStyle.Label("—", NativePageStyle.Metric, Theme.Text);
    private readonly Label kdDiffValue = NativePageStyle.Label("—", NativePageStyle.Metric, Theme.Text);
    private readonly ThemedButton refreshButton = new() { Text = "最新を更新", Size = new(140, 40), Compact = true };
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
    private bool hasMore = true;
    private int nextIndex;
    private int unavailableDetailsCount;
    private int loadGeneration;
    private CancellationTokenSource? activeLoad;
    private readonly MatchHistoryArchive archive = new(Path.Combine(Storage.Folder, "MatchHistory"));
    private readonly HashSet<string> favorites = new(StringComparer.OrdinalIgnoreCase);
    private static readonly TimeSpan ArchiveSaveTimeout = TimeSpan.FromSeconds(30);
    private const string ArchiveSaveFailedMessage = "このPCへの保存に失敗しました。「最新を更新」で再試行できます。";
    private readonly MatchHistorySaveQueue saveQueue;
    private bool archiveSaveFailed;
    private string archiveSubject = "";
    private Task? archiveLoad;
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
        refreshButton.Click += async (_, _) => await RefreshHistoryAsync();
        loadMoreButton.Click += async (_, _) => await LoadNextPageAsync();
        favoritesFilter.Click += (_, _) =>
        {
            favoritesOnly = !favoritesOnly;
            favoritesFilter.Primary = favoritesOnly;
            matchPageIndex = 0;
            RenderHistorySelection();
        };
        favoriteButton.Click += (_, _) => ToggleFavorite();
        matchRows.SizeChanged += (_, _) => RenderMatchList(orderedMatches);
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
        Disposed += (_, _) =>
        {
            lifetime.Cancel();
            activeLoad?.Cancel();
            lifetime.Dispose();
            assets.Dispose();
        };
        RenderEmptyState("Riotアカウントにログインすると、試合履歴を表示します。");
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
        if (IsDisposed) return;
        if (InvokeRequired) { BeginInvoke(() => UpdateAgents(catalogue)); return; }
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

    public async Task ActivatePageAsync()
    {
        if (IsDisposed) return;
        var subject = api.HasAccountSession ? api.Subject : archiveSubject.Length > 0 ? archiveSubject : archive.LastSubject();
        if (!archiveSubject.Equals(subject, StringComparison.OrdinalIgnoreCase))
        {
            ResetSession();
            archiveSubject = subject;
        }
        if (subject.Length == 0) return;
        var generation = loadGeneration;
        archiveLoad ??= LoadArchiveAsync(subject, generation, lifetime.Token);
        await archiveLoad;
        if (generation != loadGeneration || IsDisposed || !CanSync || loading ||
            DateTimeOffset.UtcNow - latestSync < TimeSpan.FromSeconds(30)) return;
        await LoadNextPageAsync(refresh: true);
    }

    private async Task LoadArchiveAsync(string subject, int generation, CancellationToken token)
    {
        archiveLoading = true;
        UpdateHistoryButtons();
        SetStatus("このPCに保存した試合履歴を読み込んでいます…", error: false);
        if (matches.Count == 0)
        {
            historyContent.Visible = false;
            historyEmptyText.Visible = false;
            historySkeleton.BringToFront();
            historySkeleton.Loading = true;
        }
        try
        {
            var saved = await Task.Run(() => archive.Load(subject, token), token);
            if (generation != loadGeneration || IsDisposed) return;
            foreach (var match in saved.Matches) AddMatch(match);
            favorites.UnionWith(saved.Favorites);
            weapons = saved.Weapons;
            if (matches.Count > 0) RenderHistory();
            SetStatus(saved.UnreadableCount > 0
                ? "一部の保存データを読み込めませんでした。"
                : "",
                error: saved.UnreadableCount > 0);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { }
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

    public void ResetSession()
    {
        loadGeneration++;
        activeLoad?.Cancel();
        activeLoad = null;
        loading = false;
        loadMoreButton.Text = "さらに10試合";
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
        favoritesFilter.Primary = false;
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
            Padding = new Padding(8, 6, 8, 4),
            ColumnCount = 1,
            RowCount = 3,
            AutoScroll = false,
            Margin = Padding.Empty
        };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var headerRow = new RowStyle(SizeType.Absolute, 48);
        root.RowStyles.Add(headerRow);
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));

        root.Controls.Add(BuildHeader(), 0, 0);
        void ResizeHeader()
        {
            var titleHeight = NativePageStyle.TextHeight(this, NativePageStyle.Heading, verticalPadding: 6);
            var statusHeight = NativePageStyle.TextHeight(this, pageStatus.Font, verticalPadding: 4);
            var height = Math.Max(Math.Max(refreshButton.Height, loadMoreButton.Height), titleHeight + statusHeight);
            if (headerRow.Height != height) headerRow.Height = height;
        }
        root.DpiChangedAfterParent += (_, _) => ResizeHeader();
        ResizeHeader();
        root.Controls.Add(BuildMainNavigation(), 0, 1);
        historyContent.Controls.Add(BuildMainContent());
        historyEmptyText.AutoSize = false;
        historyEmptyText.AutoEllipsis = false;
        historyEmptyText.TextAlign = ContentAlignment.MiddleCenter;
        historyEmptyText.Dock = DockStyle.Fill;
        historyPage.Controls.Add(historyContent);
        historyPage.Controls.Add(historyEmptyText);
        historyPage.Controls.Add(historySkeleton);
        historySkeleton.BringToFront();
        careerPage.Controls.Add(BuildCareerPage());
        var pageHost = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Background, Margin = Padding.Empty };
        pageHost.Controls.Add(careerPage);
        pageHost.Controls.Add(historyPage);
        root.Controls.Add(pageHost, 0, 2);
        pageStatus.Font = NativePageStyle.Reading;
        ResizeHeader();
        Controls.Add(root);
        scoreboard.SizeChanged += (_, _) => { if (detailMatch is not null) RenderScoreboard(detailMatch); };
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
        historyTab.Margin = new Padding(0, 0, 6, 0);
        careerTab.Margin = Padding.Empty;
        tabs.Controls.Add(historyTab);
        tabs.Controls.Add(careerTab);
        historyTab.Click += (_, _) => ShowMainPage(history: true);
        careerTab.Click += (_, _) => ShowMainPage(history: false);
        return tabs;
    }

    private void ShowMainPage(bool history)
    {
        historyPage.Visible = history;
        careerPage.Visible = !history;
        historyTab.Primary = history;
        careerTab.Primary = !history;
    }

    private Control BuildCareerPage()
    {
        var layout = new TableLayoutPanel
        {
            Dock = DockStyle.Fill,
            ColumnCount = 1,
            RowCount = 3,
            BackColor = Theme.Background,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 144));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 38));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.Controls.Add(BuildMetrics(), 0, 0);

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
        agentTab.Margin = Padding.Empty;
        tabs.Controls.Add(mapTab);
        tabs.Controls.Add(agentTab);

        var mapCard = BreakdownCard("MAP PERFORMANCE", mapRows, mapPrevious, mapPageLabel, mapNext);
        var agentCard = BreakdownCard("AGENT PERFORMANCE", agentRows, agentPrevious, agentPageLabel, agentNext);
        mapChartPage.Controls.Add(mapCard);
        agentChartPage.Controls.Add(agentCard);
        analysisChartHost.Controls.Add(agentChartPage);
        analysisChartHost.Controls.Add(mapChartPage);
        mapTab.Click += (_, _) => ShowAnalysisChart(map: true);
        agentTab.Click += (_, _) => ShowAnalysisChart(map: false);
        layout.Controls.Add(tabs, 0, 1);
        layout.Controls.Add(analysisChartHost, 0, 2);
        return layout;
    }

    private void ShowAnalysisChart(bool map)
    {
        mapChartPage.Visible = map;
        agentChartPage.Visible = !map;
        mapTab.Primary = map;
        agentTab.Primary = !map;
        analysisChartHost.PerformLayout();
        if (map) mapChartPage.PerformLayout(); else agentChartPage.PerformLayout();
        if (map) RenderBreakdowns(mapRows, lastMapEntries, lastHistoryRows, map: true);
        else RenderBreakdowns(agentRows, lastAgentEntries, lastHistoryRows, map: false);
    }

    private Control BuildHeader()
    {
        var header = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Background, Margin = Padding.Empty };
        var title = NativePageStyle.Label("試合分析", NativePageStyle.Heading);
        title.AutoSize = false; title.AutoEllipsis = false;
        var actions = new FlowLayoutPanel { AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.LeftToRight, Anchor = AnchorStyles.Top | AnchorStyles.Right, BackColor = Theme.Background, Margin = Padding.Empty, Padding = Padding.Empty };
        actions.Controls.Add(refreshButton);
        actions.Controls.Add(loadMoreButton);
        header.Controls.Add(title); header.Controls.Add(actions); header.Controls.Add(pageStatus);
        void Place()
        {
            var s = new Func<int, int>(value => UiMetrics.Px(header, value));
            actions.PerformLayout();
            var actionsWidth = Math.Max(actions.Width, actions.GetPreferredSize(Size.Empty).Width);
            var actionsLeft = Math.Max(0, header.ClientSize.Width - actionsWidth);
            var titleAndStatusWidth = Math.Max(s(300), actionsLeft - s(20));
            var titleHeight = NativePageStyle.TextHeight(header, title.Font, verticalPadding: 6);
            title.Bounds = new Rectangle(0, 0, titleAndStatusWidth, titleHeight);
            pageStatus.Bounds = new Rectangle(0, titleHeight, titleAndStatusWidth, Math.Max(0, header.ClientSize.Height - titleHeight));
            actions.Location = new Point(actionsLeft, Math.Max(0, (header.ClientSize.Height - actions.Height) / 2));
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
            card.Margin = new Padding(3, 2, 3, 2);
            card.Padding = new Padding(6, 3, 6, 3);
            var value = NativePageStyle.Label("—", NativePageStyle.Metric);
            var caption = NativePageStyle.Label("", NativePageStyle.Small, Theme.Muted);
            card.Controls.Add(value); card.Controls.Add(caption);
            card.Resize += (_, _) =>
            {
                var inset = UiMetrics.Px(card, 6);
                var width = Math.Max(UiMetrics.Px(card, 20), card.ClientSize.Width - inset * 2);
                var valueHeight = NativePageStyle.TextHeight(card, value.Font, verticalPadding: 2);
                var captionHeight = NativePageStyle.TextHeight(card, caption.Font, verticalPadding: 2);
                var top = Math.Max(0, (card.ClientSize.Height - valueHeight - captionHeight - UiMetrics.Px(card, 2)) / 2);
                value.Bounds = new Rectangle(inset, top, width, valueHeight);
                caption.Bounds = new Rectangle(inset, value.Bottom + UiMetrics.Px(card, 2), width, captionHeight);
            };
            grid.Controls.Add(card, i % 4, i / 4);
            metricLabels.Add((value, caption));
        }
        SetMetrics([("—", "勝敗"), ("—", "勝率"), ("—", "平均ACS"), ("—", "通算K/D"), ("—", "平均ADR"), ("—", "平均HS%"), ("—", "直近5戦"), ("—", "直近10戦")]);
        return grid;
    }

    private Control BuildMainContent()
    {
        var split = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Theme.Background, Margin = Padding.Empty };
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 36));
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 64));

        var listCard = NativePageStyle.Card();
        listCard.Margin = new Padding(3);
        var listHeader = new TableLayoutPanel { Dock = DockStyle.Top, Height = 38, ColumnCount = 3, RowCount = 1, BackColor = Theme.Surface, Margin = Padding.Empty };
        listHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        listHeader.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, favoritesFilter.Width + 10));
        listHeader.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
        listTitle.Dock = DockStyle.Fill; matchCount.Dock = DockStyle.Fill; matchCount.TextAlign = ContentAlignment.MiddleRight;
        favoritesFilter.Margin = new Padding(4, 2, 6, 4);
        favoritesFilter.Dock = DockStyle.Fill;
        listHeader.Controls.Add(listTitle, 0, 0);
        listHeader.Controls.Add(favoritesFilter, 1, 0);
        listHeader.Controls.Add(matchCount, 2, 0);
        var listFooter = new Panel { Dock = DockStyle.Bottom, Height = 36, BackColor = Theme.Surface, Padding = new Padding(0, 2, 0, 0) };
        matchPrevious.Size = new Size(76, 30);
        matchNext.Size = new Size(76, 30);
        matchPrevious.Location = Point.Empty;
        matchNext.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        matchPageLabel.TextAlign = ContentAlignment.MiddleCenter;
        void PlaceMatchFooter()
        {
            matchPrevious.Size = matchNext.Size = new Size(UiMetrics.Px(this, 76), UiMetrics.Px(this, 30));
            matchPrevious.Location = new Point(0, 0);
            matchNext.Location = new Point(Math.Max(0, listFooter.ClientSize.Width - matchNext.Width), 0);
            matchPageLabel.Bounds = new Rectangle(matchPrevious.Right + UiMetrics.Px(this, 2), 0, Math.Max(0, matchNext.Left - matchPrevious.Right - UiMetrics.Px(this, 4)), listFooter.ClientSize.Height);
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
        return split;
    }

    private Control BuildDetailCard()
    {
        detailCard.Margin = new Padding(3);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = Theme.Surface, Margin = Padding.Empty };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 56));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 40));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var summary = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface, Margin = Padding.Empty };
        summary.Controls.Add(detailTitle); summary.Controls.Add(detailSubtitle); summary.Controls.Add(scoreLabel);
        summary.Resize += (_, _) =>
        {
            detailTitle.Bounds = new Rectangle(0, 0, Math.Max(UiMetrics.Px(summary, 120), summary.ClientSize.Width - UiMetrics.Px(summary, 115)), UiMetrics.Px(summary, 28));
            detailSubtitle.Bounds = new Rectangle(UiMetrics.Px(summary, 1), UiMetrics.Px(summary, 29), Math.Max(UiMetrics.Px(summary, 120), summary.ClientSize.Width - UiMetrics.Px(summary, 115)), UiMetrics.Px(summary, 22));
            scoreLabel.Bounds = new Rectangle(Math.Max(0, summary.ClientSize.Width - UiMetrics.Px(summary, 105)), 0, UiMetrics.Px(summary, 100), UiMetrics.Px(summary, 48));
            scoreLabel.TextAlign = ContentAlignment.MiddleRight;
        };
        layout.Controls.Add(summary, 0, 0);

        var detailTabs = new FlowLayoutPanel
        {
            Dock = DockStyle.Fill,
            FlowDirection = FlowDirection.LeftToRight,
            WrapContents = false,
            BackColor = Theme.Surface,
            Margin = Padding.Empty,
            Padding = Padding.Empty
        };
        scoreboardTab.Margin = new Padding(0, 0, 6, 0);
        roundsTab.Margin = new Padding(0, 0, 6, 0);
        matchAnalysisTab.Margin = new Padding(0, 0, 6, 0);
        favoriteButton.Margin = Padding.Empty;
        detailTabs.Controls.Add(scoreboardTab);
        detailTabs.Controls.Add(roundsTab);
        detailTabs.Controls.Add(matchAnalysisTab);
        detailTabs.Controls.Add(favoriteButton);
        layout.Controls.Add(detailTabs, 0, 1);

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
        matchAnalysis.RowStyles.Add(new RowStyle(SizeType.Absolute, 112));
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
        scoreboardPage.Visible = page == 0;
        roundsPage.Visible = page == 1;
        matchAnalysisPage.Visible = page == 2;
        scoreboardTab.Primary = page == 0;
        roundsTab.Primary = page == 1;
        matchAnalysisTab.Primary = page == 2;
        detailPageHost.PerformLayout();
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
        card.Resize += (_, _) =>
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
        };
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
        foreach (var width in new[] { 33f, 9f, 14f, 8f, 9f, 8f, 8f, 11f })
            scoreboard.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, width));
        BuildScoreboardHeader();
    }

    private static readonly string[] ScoreboardHeaders = ["選手 / エージェント", "ACS", "K/D/A", "K/D", "ADR", "DDΔ", "HS%", "FK/FD"];
    private static readonly Color FriendlyTeamColor = Color.FromArgb(91, 185, 255);
    private static readonly Color OpposingTeamColor = Color.FromArgb(255, 105, 123);
    private const int AcsColumn = 1, KdaColumn = 2, KdColumn = 3, AdrColumn = 4, DamageDeltaColumn = 5, HeadshotColumn = 6, OpeningColumn = 7;
    private static bool IsSortableScoreboardColumn(int column) => column is >= AcsColumn and <= OpeningColumn;

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
            header.Padding = new Padding(UiMetrics.Px(this, 3), 0, UiMetrics.Px(this, 2), 0);
            header.AutoEllipsis = true;
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
        if (scoreboardSortColumn == column) scoreboardSortDescending = !scoreboardSortDescending;
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
                scoreboardHeaders[i].Text = selected ? $"{ScoreboardHeaders[i]} {(scoreboardSortDescending ? "▼" : "▲")}" : ScoreboardHeaders[i];
                scoreboardHeaders[i].ForeColor = selected ? Theme.Text : Theme.Muted;
            }
        }
        finally { scoreboard.ResumeLayout(); }
    }

    private Color ScoreboardRowColor(PlayerMatchStats player, int row) =>
        player.Subject.Equals(HistorySubject, StringComparison.OrdinalIgnoreCase)
            ? Color.FromArgb(57, 38, 46)
            : row % 2 == 1 ? Theme.Surface : Color.FromArgb(27, 35, 46);

    private IEnumerable<PlayerMatchStats> OrderScoreboardPlayers(IEnumerable<PlayerMatchStats> players)
    {
        // Default order: team, then ACS (highest first). A selected column sorts on top of it; OrderBy is
        // stable, so ties keep the default order.
        var ordered = players
            .OrderBy(p => p.TeamId, StringComparer.OrdinalIgnoreCase)
            .ThenByDescending(p => p.AverageCombatScore ?? -1)
            .ToArray();
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
        if (a is null || b is null) return a is null ? (b is null ? 0 : 1) : -1;
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

    private async Task LoadNextPageAsync(bool refresh = false)
    {
        if (IsDisposed || loading || (!refresh && !hasMore)) return;
        if (!CanSync)
        {
            SetStatus("試合分析を見るには、上部のRiotログインから接続してください。", error: false);
            return;
        }

        var generation = loadGeneration;
        var subject = archiveSubject;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        operation.CancelAfter(TimeSpan.FromSeconds(35));
        activeLoad = operation;
        var token = operation.Token;
        loading = true;
        loadMoreButton.Text = "読み込み中…";
        UpdateHistoryButtons();
        var startIndex = refresh ? 0 : nextIndex;
        SetStatus(startIndex == 0 ? "最近の試合を読み込んでいます…" : "さらに試合を読み込んでいます…", error: false);
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
            var page = await api.GetMatchHistory(startIndex, PageSize, token).WaitAsync(token);
            if (generation != loadGeneration || IsDisposed) return;
            var entriesToLoad = page.Where(entry => !matchPositions.TryGetValue(entry.MatchId, out var position) ||
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
                catch (ApiException) { }
                catch (HttpRequestException) { }
                catch (JsonException) { }
                catch (OperationCanceledException) when (!token.IsCancellationRequested) { }
            }
            if (generation != loadGeneration || IsDisposed) return;
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
                return details with { Players = players };
            }).ToArray();
            var parsed = await Task.Run(() => prepared.Select(MatchAnalyticsEngine.Analyze).ToArray(), token).WaitAsync(token);
            if (generation != loadGeneration || IsDisposed) return;
            if (refresh) unavailableDetailsCount = 0;
            if (!ReferenceEquals(weapons, weaponNames)) weaponRenderKey = null;
            weapons = weaponNames;
            nextIndex = startIndex + PageSize;
            hasMore = page.Count == PageSize;
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
            if (!lifetime.IsCancellationRequested && generation == loadGeneration && !IsDisposed)
                ShowHistoryLoadError(new TimeoutException(), "試合履歴の取得がタイムアウトしました。「最新を更新」で再試行してください。");
        }
        catch (ApiException ex) when (ex.Status is 401 or 403)
        {
            if (generation == loadGeneration) ShowHistoryLoadError(ex, "Riotのログイン期限が切れました。再接続してください。");
        }
        catch (ApiException ex) when (ex.Status == 429)
        {
            if (generation == loadGeneration) ShowHistoryLoadError(ex, "Riot側の応答が混み合っています。少し待ってから再読み込みしてください。");
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException or InvalidDataException or JsonException or InvalidOperationException or TaskCanceledException)
        {
            if (generation == loadGeneration) ShowHistoryLoadError(ex, "試合履歴を取得できませんでした。ネット接続とRiotアカウントを確認してください。");
        }
        catch (Exception ex) when (generation == loadGeneration)
        {
            ShowHistoryLoadError(ex, "試合履歴を表示できませんでした。最新を更新してください。");
        }
        finally
        {
            if (ReferenceEquals(activeLoad, operation)) activeLoad = null;
            if (generation == loadGeneration && !IsDisposed)
            {
                loading = false;
                loadMoreButton.Text = "さらに10試合";
                UpdateHistoryButtons();
            }
        }
    }

    private string FetchStatusMessage() => unavailableDetailsCount > 0
        ? $"{unavailableDetailsCount} 件の試合詳細は取得できませんでした。保存した履歴は表示できます。"
        : "";

    /// <summary>
    /// Saves the queued matches for <paramref name="subject"/> after they are already on screen. Never throws:
    /// failures and timeouts keep the entries queued for the next refresh, and results from a previous
    /// account session (or after the page is closed) are ignored.
    /// </summary>
    private async Task PersistHistoryAsync(string subject, IReadOnlyDictionary<string, string> weaponNames, int generation)
    {
        MatchHistorySaveResult result;
        try { result = await saveQueue.SaveAsync(subject, weaponNames, ArchiveSaveTimeout, lifetime.Token); }
        catch (Exception ex) { result = new(MatchHistorySaveOutcome.Failed, ex); }
        if (generation != loadGeneration || IsDisposed || result.Outcome == MatchHistorySaveOutcome.Superseded) return;
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
        historySkeleton.Loading = false;
        var ordered = matches.OrderByDescending(m => m.Info.GameStartMillis ?? 0).ToArray();
        historyContent.Visible = ordered.Length > 0;
        historyEmptyText.Visible = ordered.Length == 0;
        var history = MatchAnalyticsEngine.AnalyzeHistory(ordered, HistorySubject);
        matchCount.Text = $"{ordered.Length} 試合";
        SetMetrics([
            ($"{history.Wins}W {history.Losses}L", $"勝敗 · {history.MatchCount} 試合"),
            (Percent(history.WinRatePercent), "勝率"),
            (Number(history.AverageCombatScore, 0), "平均ACS"),
            (Number(history.OverallKillDeathRatio, 2), "通算K/D"),
            (Number(history.AverageDamagePerRound, 1), "平均ADR"),
            (Percent(history.AverageHeadshotPercent), "平均HS%"),
            ($"{history.Recent5.Wins}W · {Percent(history.Recent5.WinRatePercent)}", $"直近5戦 · {history.Recent5.MatchesConsidered}戦"),
            ($"{history.Recent10.Wins}W · {Percent(history.Recent10.WinRatePercent)}", $"直近10戦 · {history.Recent10.MatchesConsidered}戦")
        ]);
        lastMapEntries = history.Maps;
        lastAgentEntries = history.Agents;
        lastHistoryRows = history.MatchesNewestFirst;
        RenderBreakdowns(mapRows, lastMapEntries, lastHistoryRows, map: true);
        RenderBreakdowns(agentRows, lastAgentEntries, lastHistoryRows, map: false);
        if (ordered.Length == 0) RenderEmptyState("表示できる試合がありません。");
        else RenderHistorySelection(ordered);
        UpdateHistoryButtons();
    }

    private void RenderHistorySelection() =>
        RenderHistorySelection(matches.OrderByDescending(match => match.Info.GameStartMillis ?? 0).ToArray());

    private void RenderHistorySelection(IReadOnlyList<MatchAnalytics> ordered)
    {
        var visible = favoritesOnly ? ordered.Where(match => favorites.Contains(match.Info.MatchId)).ToArray() : ordered;
        var empty = visible.Count == 0;
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
        if (detailMatch is not { } match || !Guid.TryParse(archiveSubject, out _)) return;
        var matchId = match.Info.MatchId;
        var favorite = !favorites.Contains(matchId);
        try { archive.SetFavorite(archiveSubject, match.Match, favorite); }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
        {
            NativePageStyle.LogPageError("MatchHistoryFavorite", ex);
            SetStatus("お気に入りを保存できませんでした。", error: true);
            return;
        }
        if (favorite) favorites.Add(matchId);
        else favorites.Remove(matchId);
        RenderHistorySelection();
        SetStatus("", error: false);
    }

    private void UpdateFavoriteButton()
    {
        var favorite = detailMatch is { } match && favorites.Contains(match.Info.MatchId);
        favoriteButton.Enabled = detailMatch is not null && archiveSubject.Length > 0;
        favoriteButton.Text = favorite ? "★ 登録済み" : "☆ お気に入り";
        favoriteButton.AccessibleName = favorite ? "選択した試合のお気に入り登録を解除" : "選択した試合をお気に入りに登録";
        favoriteButton.Primary = favorite;
    }

    private void UpdateHistoryButtons()
    {
        refreshButton.Enabled = CanSync && !loading;
        loadMoreButton.Enabled = CanSync && hasMore && !loading;
        favoritesFilter.Enabled = matches.Count > 0;
    }

    private void RenderMatchList(IReadOnlyList<MatchAnalytics> ordered)
    {
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
                    visibleMatchRows.Add(match, row);
                    matchRows.Controls.Add(row);
                }
                row.Bounds = new Rectangle(0, i * rowHeight, matchRows.ClientSize.Width, rowHeight - UiMetrics.Px(this, 3));
                row.BackColor = match.Info.MatchId.Equals(selectedMatchId, StringComparison.OrdinalIgnoreCase)
                    ? Color.FromArgb(45, 58, 74) : Theme.Surface;
                row.Controls["matchTitle"]!.Text = MatchListTitle(match);
            }
        }
        finally { matchRows.ResumeLayout(); }
        matchCount.Text = $"{ordered.Count} 試合";
        matchPageLabel.Text = $"{matchPageIndex + 1} / {pageCount}";
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
        var accent = me?.Outcome switch { MatchOutcome.Win => Theme.Green, MatchOutcome.Loss => Theme.Red, _ => Theme.Muted };
        var selected = match.Info.MatchId.Equals(selectedMatchId, StringComparison.OrdinalIgnoreCase);
        var row = new Panel { Tag = match.Info.MatchId, Height = height - UiMetrics.Px(this, 3), BackColor = selected ? Color.FromArgb(45, 58, 74) : Theme.Surface, BorderStyle = BorderStyle.FixedSingle, Cursor = Cursors.Hand };
        var marker = new Panel { BackColor = accent };
        var title = NativePageStyle.Label(MatchListTitle(match), NativePageStyle.ReadingBold, Theme.Text);
        title.Name = "matchTitle";
        var score = NativePageStyle.Label(TeamScore(match, me), NativePageStyle.ReadingBold, accent);
        var agent = NativePageStyle.Label(AgentName(me?.CharacterId ?? ""), NativePageStyle.Reading, Theme.Muted);
        var date = NativePageStyle.Label(match.Info.GameStartUtc?.ToLocalTime().ToString("M/d HH:mm") ?? "日時不明", NativePageStyle.Reading, Theme.Muted);
        var kda = NativePageStyle.Label($"K/D/A  {me?.Kills?.ToString() ?? "—"} / {me?.Deaths?.ToString() ?? "—"} / {me?.Assists?.ToString() ?? "—"}", NativePageStyle.Reading, Theme.Text);
        var acs = NativePageStyle.Label($"ACS  {Number(me?.AverageCombatScore, 0)}", NativePageStyle.Reading, Theme.Muted);
        score.TextAlign = date.TextAlign = acs.TextAlign = ContentAlignment.MiddleRight;
        row.Controls.AddRange([marker, title, score, agent, date, kda, acs]);

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
                    ? Color.FromArgb(45, 58, 74) : Theme.Surface;
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
        if (detailLayout is not null) detailLayout.Visible = true;
        detailEmpty.Visible = false;
        detailMatch = match;
        UpdateFavoriteButton();
        var me = match.FindPlayer(HistorySubject);
        var team = match.Teams.FirstOrDefault(t => t.TeamId.Equals(me?.TeamId, StringComparison.OrdinalIgnoreCase));
        detailTitle.Text = DetailTitle(match);
        detailSubtitle.Text = $"{match.Info.GameStartUtc?.ToLocalTime().ToString("yyyy/MM/dd HH:mm") ?? "日時不明"}  ·  {Math.Round(match.Info.GameLength?.TotalMinutes ?? 0)} 分  ·  {match.Rounds.Count} ラウンド";
        scoreLabel.Text = team?.RoundsWon is { } won ? $"{won} : {team.RoundsLost?.ToString() ?? "—"}" : "—";
        scoreLabel.ForeColor = me?.Outcome switch { MatchOutcome.Win => Theme.Green, MatchOutcome.Loss => Theme.Red, _ => Theme.Text };
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
        RenderWeaponBreakdown(match, HistorySubject);
    }

    private string DetailTitle(MatchAnalytics match) =>
        $"{MapName(match.Info.MapId)}  ·  {QueueName(MatchAnalyticsEngine.DisplayQueueId(match.Match))}  ·  {AgentName(match.FindPlayer(HistorySubject)?.CharacterId ?? "")}";

    private void RenderScoreboard(MatchAnalytics match)
    {
        var content = (match, DeviceDpi);
        var height = scoreboard.ClientSize.Height;
        var replaceRows = scoreboardContent != content;
        if (!replaceRows && scoreboardHeight == height) return;
        scoreboardContent = content;
        scoreboardHeight = height;
        scoreboard.SuspendLayout();
        try
        {
            if (replaceRows) BuildScoreboardRows(match);

            var headerHeight = NativePageStyle.TextHeight(this, NativePageStyle.SmallBold, verticalPadding: 8);
            var minimumRowHeight = NativePageStyle.TextHeight(this, NativePageStyle.Reading, lines: 2, verticalPadding: 6);
            var rowHeight = Math.Max(minimumRowHeight, (height - headerHeight) / Math.Max(1, scoreboardRows.Count));
            scoreboard.RowStyles[0].Height = headerHeight;
            for (var i = 1; i < scoreboard.RowStyles.Count; i++)
                scoreboard.RowStyles[i].Height = rowHeight;
        }
        finally { scoreboard.ResumeLayout(); }
    }

    private void ClearScoreboardRows()
    {
        foreach (var row in scoreboardRows.Values)
            foreach (var cell in row)
                cell.Dispose();
        scoreboardRows.Clear();
        while (scoreboard.RowStyles.Count > 1)
            scoreboard.RowStyles.RemoveAt(scoreboard.RowStyles.Count - 1);
        scoreboard.RowCount = 1;
    }

    private void BuildScoreboardRows(MatchAnalytics match)
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
            var values = new[]
            {
                player.DisplayName.Length > 0 ? player.DisplayName : "名前非公開",
                Number(player.AverageCombatScore, 0),
                $"{player.Kills?.ToString() ?? "—"} / {player.Deaths?.ToString() ?? "—"} / {player.Assists?.ToString() ?? "—"}",
                Number(player.KillDeathRatio, 2), Number(player.AverageDamagePerRound, 1), Number(player.DamageDeltaPerRound, 1),
                Percent(player.HeadshotPercent), $"{player.FirstKills?.ToString() ?? "—"} / {player.FirstDeaths?.ToString() ?? "—"}"
            };
            scoreboard.RowStyles.Add(new RowStyle(SizeType.Absolute));
            var background = ScoreboardRowColor(player, i + 1);
            var cells = new Control[values.Length];
            teamTags.TryGetValue(player.TeamId, out var teamTag);
            cells[0] = CreatePlayerCell(values[0], $"{AgentName(player.CharacterId)}  ·  {rankName}", player.CharacterId,
                rankImages[i], rankName, reserveRankSlot, background, teamTag);
            for (var column = 1; column < values.Length; column++)
            {
                var cell = NativePageStyle.Label(values[column], NativePageStyle.Small, Theme.Text);
                cell.AutoSize = false;
                cell.Dock = DockStyle.Fill;
                cell.BackColor = background;
                cell.Padding = new Padding(UiMetrics.Px(this, 3), 0, UiMetrics.Px(this, 2), 0);
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
    private Control CreatePlayerCell(string name, string detail, string characterId, Image? rankImage, string rankName,
        bool reserveRankSlot, Color background, (Color Color, string? Label) teamTag)
    {
        var cell = new Panel { Dock = DockStyle.Fill, BackColor = background, Margin = Padding.Empty, Padding = Padding.Empty };
        var icon = new SharedImageBox { Image = AgentIcon(characterId), BackColor = background, AccessibleName = AgentName(characterId) };
        var rankIcon = new SharedImageBox { Image = rankImage, BackColor = background, AccessibleName = rankName };
        Label TextLine(string text)
        {
            var line = NativePageStyle.Label(text, NativePageStyle.Reading, Theme.Text);
            line.AutoSize = false;
            line.AutoEllipsis = true;
            line.BackColor = background;
            line.Padding = new Padding(UiMetrics.Px(this, 3), 0, UiMetrics.Px(this, 2), 0);
            return line;
        }
        var nameLabel = TextLine(name);
        nameLabel.AccessibleDescription = teamTag.Label;
        var detailLabel = TextLine(detail);
        cell.Controls.Add(nameLabel);
        cell.Controls.Add(detailLabel);
        cell.Controls.Add(icon);
        cell.Controls.Add(rankIcon);
        var tagBounds = Rectangle.Empty;
        cell.Paint += (_, e) =>
        {
            if (tagBounds.IsEmpty) return;
            using var brush = new SolidBrush(teamTag.Color);
            e.Graphics.FillRectangle(brush, tagBounds);
        };
        void Place()
        {
            var inset = UiMetrics.Px(cell, 4);
            var width = cell.ClientSize.Width;
            var height = cell.ClientSize.Height;
            var tagWidth = UiMetrics.Px(cell, 6);
            var tagHeight = Math.Min(UiMetrics.Px(cell, 36), height - inset * 2);
            tagBounds = !teamTag.Color.IsEmpty && tagHeight > 0 && width > tagWidth + inset * 2
                ? new Rectangle(inset, (height - tagHeight) / 2, tagWidth, tagHeight)
                : Rectangle.Empty;
            var iconLeft = tagBounds.IsEmpty ? inset : tagBounds.Right + inset;
            var iconLimit = height - inset * 2;
            var minimumIcon = UiMetrics.Px(cell, 14);
            var size = Math.Min(Math.Min(UiMetrics.Px(cell, 40), iconLimit), (width - iconLeft) / 3);
            var showIcon = size >= minimumIcon;
            icon.Visible = showIcon;
            var textLeft = tagBounds.IsEmpty ? 0 : iconLeft;
            var rankLeft = iconLeft;
            if (showIcon)
            {
                icon.Bounds = new Rectangle(iconLeft, Math.Max(0, (height - size) / 2), size, size);
                textLeft = icon.Right;
                rankLeft = icon.Right + UiMetrics.Px(cell, 3);
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
            var lineHeight = Math.Min(NativePageStyle.TextHeight(cell, nameLabel.Font, verticalPadding: 2), Math.Max(0, height / 2));
            var top = Math.Max(0, (height - lineHeight * 2) / 2);
            nameLabel.Bounds = new Rectangle(textLeft, top, textWidth, lineHeight);
            detailLabel.Bounds = new Rectangle(textLeft, top + lineHeight, textWidth, lineHeight);
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
            catch (Exception ex) when (ex is ArgumentException or System.Runtime.InteropServices.ExternalException) { }
        }
    }

    private void RenderRoundPage()
    {
        var renderKey = (detailMatch, rounds.ClientSize, roundsPageIndex, DeviceDpi);
        if (roundsRenderKey == renderKey) return;
        roundsRenderKey = renderKey;
        rounds.SuspendLayout();
        DisposeChildren(rounds);
        if (detailMatch is not { } match || match.Rounds.Count == 0)
        {
            roundsPageIndex = 0;
            roundsPageLabel.Text = "1 / 1";
            roundsPrevious.Enabled = roundsNext.Enabled = false;
            rounds.ResumeLayout();
            return;
        }
        var cardWidth = UiMetrics.Px(this, 220);
        var textHeight = NativePageStyle.TextHeight(this, NativePageStyle.Reading, lines: 3, verticalPadding: 8);
        var cardHeight = Math.Max(UiMetrics.Px(this, 96), textHeight + UiMetrics.Px(this, 12));
        var columns = Math.Max(1, rounds.ClientSize.Width / (cardWidth + UiMetrics.Px(this, 6)));
        var rows = Math.Max(1, rounds.ClientSize.Height / (cardHeight + UiMetrics.Px(this, 6)));
        var pageSize = Math.Max(1, columns * rows);
        var pageCount = Math.Max(1, (match.Rounds.Count + pageSize - 1) / pageSize);
        roundsPageIndex = Math.Clamp(roundsPageIndex, 0, pageCount - 1);
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
        var renderKey = (entries, rows, host.ClientSize, map ? mapPageIndex : agentPageIndex, DeviceDpi);
        if (map ? mapRenderKey == renderKey : agentRenderKey == renderKey) return;
        if (map) mapRenderKey = renderKey; else agentRenderKey = renderKey;
        SuspendFlow(host);
        DisposeChildren(host);
        var textHeight = NativePageStyle.TextHeight(this, NativePageStyle.Reading, verticalPadding: 3);
        var trackHeight = Math.Max(4, UiMetrics.Px(this, 5));
        var contentHeight = textHeight + UiMetrics.Px(this, 5) + trackHeight;
        var minimumRowHeight = Math.Max(UiMetrics.Px(this, 50), contentHeight + UiMetrics.Px(this, 8));
        var pageSize = Math.Max(1, host.ClientSize.Height / minimumRowHeight);
        var pageCount = Math.Max(1, (entries.Count + pageSize - 1) / pageSize);
        var pageIndex = map ? mapPageIndex : agentPageIndex;
        pageIndex = Math.Clamp(pageIndex, 0, pageCount - 1);
        if (map) mapPageIndex = pageIndex; else agentPageIndex = pageIndex;
        var visibleEntries = entries.Skip(pageIndex * pageSize).Take(pageSize).ToArray();
        var rowHeight = Math.Max(minimumRowHeight, host.ClientSize.Height / Math.Max(1, visibleEntries.Length));
        foreach (var item in visibleEntries)
        {
            var grouped = rows.Where(row => (map ? row.MapId : row.Stats.CharacterId).Equals(item.Key, StringComparison.OrdinalIgnoreCase)).ToArray();
            var averageAcs = Average(grouped.Select(row => row.Stats.AverageCombatScore));
            var name = map ? MapName(item.Key) : AgentName(item.Key);
            var rowControl = new Panel { Height = rowHeight - UiMetrics.Px(this, 3), Width = Math.Max(UiMetrics.Px(this, 200), host.ClientSize.Width - UiMetrics.Px(this, 24)), BackColor = Theme.Surface, Margin = new Padding(0, 0, 0, UiMetrics.Px(this, 3)) };
            var label = NativePageStyle.Label($"{name}  ·  {item.Matches}戦  ·  ACS {Number(averageAcs, 0)}", NativePageStyle.Reading, Theme.Text);
            var percent = NativePageStyle.Label(Percent(item.WinRatePercent), NativePageStyle.ReadingBold, Theme.Text);
            percent.TextAlign = ContentAlignment.MiddleRight;
            var track = new Panel { BackColor = Theme.Elevated, Height = trackHeight, Anchor = AnchorStyles.Left | AnchorStyles.Right | AnchorStyles.Bottom };
            var fill = new Panel { BackColor = Theme.Red, Height = trackHeight, Width = 0, Dock = DockStyle.Left };
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

    private void RenderWeaponBreakdown(MatchAnalytics match, string subject)
    {
        weaponMatch = match;
        weaponPageIndex = 0;
        RenderWeaponBreakdown();
    }

    private void RenderWeaponBreakdown()
    {
        var renderKey = (weaponMatch, weaponRows.ClientSize, weaponPageIndex, DeviceDpi);
        if (weaponRenderKey == renderKey) return;
        weaponRenderKey = renderKey;
        SuspendFlow(weaponRows); DisposeChildren(weaponRows);
        var items = weaponMatch?.FindPlayer(HistorySubject)?.WeaponKills.Where(x => x.Kills > 0).OrderByDescending(x => x.Kills).ToArray() ?? [];
        var textHeight = NativePageStyle.TextHeight(this, NativePageStyle.Reading, verticalPadding: 3);
        var trackHeight = Math.Max(4, UiMetrics.Px(this, 5));
        var contentHeight = textHeight + UiMetrics.Px(this, 5) + trackHeight;
        var minimumRowHeight = Math.Max(UiMetrics.Px(this, 50), contentHeight + UiMetrics.Px(this, 8));
        var pageSize = Math.Max(1, weaponRows.ClientSize.Height / minimumRowHeight);
        var pageCount = Math.Max(1, (items.Length + pageSize - 1) / pageSize);
        weaponPageIndex = Math.Clamp(weaponPageIndex, 0, pageCount - 1);
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
                fill.Width = (int)Math.Round(track.ClientSize.Width * Math.Clamp(item.Kills / 12d, 0, 1));
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
        historySkeleton.Loading = false;
        historyContent.Visible = false;
        historyEmptyText.Text = api.HasAccountSession
            ? message
            : "Riotアカウントにログインすると、試合履歴と分析を表示します。\n上部の「Riotにログイン」から接続してください。";
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
        }
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
        catch (Exception ex) when (ex is HttpRequestException or JsonException or TaskCanceledException or InvalidOperationException) { }
        return weapons;
    }

    private string PlayerName(string? subject, MatchAnalytics match)
    {
        if (string.IsNullOrWhiteSpace(subject)) return "";
        var player = match.Players.FirstOrDefault(p => p.Subject.Equals(subject, StringComparison.OrdinalIgnoreCase));
        return player?.DisplayName.Length > 0 ? player.DisplayName : "名前非公開";
    }

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
        "escalation" => "エスカレーション", "replication" => "レプリケーション", "ggteam" => "プレミア",
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
