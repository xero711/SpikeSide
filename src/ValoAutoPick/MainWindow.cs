using System.Drawing.Drawing2D;
using System.Net.Sockets;
using System.Reflection;
using System.Text.Json;
using System.Threading.Channels;

namespace ValoAutoPick;

public sealed class MainWindow : DarkForm
{
    // Keep the idle poll short enough to catch the start of character select quickly,
    // while still leaving the server a reasonable amount of breathing room. The
    // worker is serial, so this never creates overlapping requests.
    private const int FastPollIntervalMilliseconds = 250;
    private const int ClientUnavailableIntervalMilliseconds = 1500;
    private const int TransientFailureIntervalMilliseconds = 2500;

    private static readonly string[] RegionValues = ["auto", "ap", "na", "eu", "kr", "br", "latam", "pbe"];
    private readonly ThemedSelect region = RegionCombo();
    private readonly SwitchToggle autoStart = new("自動で監視開始", false);
    private readonly ThemedButton login = new() { Text = "Riotにログイン", Size = new(176, 43), Compact = true };
    // Leave enough horizontal room for Japanese glyphs plus the button's text padding.
    private readonly ThemedButton logout = new() { Text = "ログアウト", Size = new(119, 43), Compact = true };
    private readonly ThemedButton dashboardTab = new() { Text = "ピック", Size = new(92, 43), Compact = true, Primary = true };
    private readonly ThemedButton matchHistoryButton = new() { Text = "試合分析", Size = new(132, 43), Compact = true };
    private readonly ThemedButton storeTab = new() { Text = "STORE", Size = new(104, 43), Compact = true };
    // MainWindow positions this page area below the header with ArrangeRoot.
    // Keeping Dock=Fill here makes WinForms cover the manually positioned header.
    private readonly Panel pageHost = new() { Dock = DockStyle.None, BackColor = Theme.Background };
    private readonly ThemedButton start = new() { Text = "監視を開始", Primary = true, Font = new Font("Yu Gothic UI", 15.5f, FontStyle.Bold) };
    private readonly ThemedButton autoQueue = new() { Text = "自動インキュー開始", Font = new Font("Yu Gothic UI", 13.5f, FontStyle.Bold), AccessibleDescription = "ゲーム内で選択中のモードに自動で参加します" };
    private readonly HeroPanel hero = new();
    private readonly RoundedCard settingsPanel = new() { Dock = DockStyle.Fill };
    private readonly RoundedCard mapSettingsCard = new() { Dock = DockStyle.Fill };
    private readonly MatchMapPanel matchMap = new() { Dock = DockStyle.Fill };
    private readonly Label mapSummary = new() { Dock = DockStyle.Fill, ForeColor = Theme.Muted, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
    private readonly Label trialRemaining = new() { Dock = DockStyle.Fill, ForeColor = Theme.Text, Font = new Font("Yu Gothic UI", 11, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft, Margin = Padding.Empty };
    private readonly Label trialDescription = new() { Dock = DockStyle.Fill, ForeColor = Theme.Text, Font = new Font("Yu Gothic UI", 9.5f), TextAlign = ContentAlignment.TopLeft, Margin = Padding.Empty, Text = "1試合のピック試行で1回消費します。\nマップ別設定・自動インキューは利用不可" };
    private readonly Label trialCorner = new() { ForeColor = Theme.Text, BackColor = Theme.Background, Font = new Font("Yu Gothic UI", 9, FontStyle.Bold), TextAlign = ContentAlignment.MiddleRight, AutoEllipsis = true, AccessibleName = "体験版の残り回数" };
    private readonly TrialUsage trialUsage = TrialUsage.CreateDefault();
    private string displayedMap = "";
    private readonly RoundedCard startPanel = new() { Dock = DockStyle.Fill };
    private readonly Label status = new() { Text = "停止中", ForeColor = Theme.Muted, Font = new Font("Yu Gothic UI", 8.2f, FontStyle.Bold), AutoSize = false, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill };
    private readonly Label detail = new() { Text = "", ForeColor = Theme.Muted, Font = new Font("Yu Gothic UI", 8.2f), AutoSize = false, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill };
    private readonly Label queueDetail = new() { Text = "自動インキューは停止中", ForeColor = Theme.Muted, Font = new Font("Yu Gothic UI", 8.2f), AutoSize = false, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill };
    private readonly Label accountInfo = new() { Text = "Riotアカウント: 未接続", ForeColor = Theme.Muted, Font = new Font("Yu Gothic UI", 9.5f), AutoSize = false, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill };
    private readonly NotifyIcon tray = new() { Icon = AppIcon.Create(), Text = "SpikeSide", Visible = true };
    private readonly RiotApi api = new();
    private MatchHistoryControl? matchHistory;
    private StorePreviewControl? storePreview;
    private Control? dashboardPage;
    private MainPage selectedPage;
    private readonly CancellationTokenSource lifetime = new();
    private LoginWindow? loginWindow;
    private CancellationTokenSource? loginAttempt;
    private CancellationTokenSource? refreshStop;
    private Task<bool>? accountRefresh;
    private Task? ownedLoad;
    private CancellationTokenSource? monitor;
    private Task? monitorTask;
    private Task? stopTask;
    private Task? queueLeaveTask;
    private PickEngine? engine;
    private AutoQueueEngine? queueEngine;
    private RelayOptions? relayOptions;
    private string engineSubject = "", lastStatus = "", lastQueueStatus = "";
    private bool closing, busy, stopping, pickMonitoring, autoQueueActive, systemBrowserLogin, inputEnabled = true;
    private Panel? statusLed;
    private static readonly Uri AgentCatalogUri = new("https://valorant-api.com/v1/agents?isPlayableCharacter=true&language=ja-JP");
    private static string AgentCachePath => Path.Combine(Storage.Folder, "agents.json");
    // Replaced (never mutated) on refresh: open dialogs keep indexing the list they were given.
    private List<Agent> agents = [];
    private readonly List<Agent> embeddedAgents;
    private List<Agent> cachedAgents = [];
    private Task? agentCatalogRefresh;
    private Agent? mainAgent;
    // Saved agent ID that is not in the catalogue yet; kept so saving settings never erases it.
    private string pendingAgentId = "";
    private HashSet<string>? ownedAgents;
    private string ownedSubject = "";
    private int? trialRemainingCount;
    private const string TrialFinishedMessage = "体験版の3回を使い切りました。製品版をご利用ください";
    private Dictionary<string, string> mapAgents = new(StringComparer.OrdinalIgnoreCase);
    private readonly ThemedButton mapSettings = new() { Text = "マップ別エージェント設定", Compact = true, Dock = DockStyle.Fill };
    private readonly string? smokePath;
    private readonly string? previewAgentId;

    public MainWindow(string? smokePath = null, bool loginSmokeTest = false, string? previewAgentId = null, bool pickerSmokeTest = false, bool mapSmokeTest = false)
        : base(resizable: true)
    {
        this.smokePath = smokePath;
        this.previewAgentId = previewAgentId;
        Text = Edition.IsTrial ? "SpikeSide 体験版" : "SpikeSide";
        ClientSize = new Size(1600, 860); MinimumSize = new Size(1200, 760); StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Background; ForeColor = Theme.Text; Font = new Font("Yu Gothic UI", 10.5f);

        using (var resource = Assembly.GetExecutingAssembly().GetManifestResourceStream("ValoAutoPick.agents.json"))
        {
            if (resource == null) throw new InvalidOperationException("エージェント一覧を読み込めません。");
            using var reader = new StreamReader(resource);
            embeddedAgents = AgentCatalog.ParseCatalogue(reader.ReadToEnd(), trustBaseContent: true);
        }
        if (embeddedAgents.Count == 0) throw new InvalidOperationException("エージェント一覧を読み込めません。");
        // The cache only overlays the embedded list, so a stale cache can never hide newly shipped agents.
        try { cachedAgents = AgentCatalog.ReadCache(AgentCachePath) ?? []; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException) { }
        agents = AgentCatalog.Merge(embeddedAgents, cachedAgents);

        var config = Storage.Load(); RestoreAgent(config);
        mapAgents = new(config.MapAgents, StringComparer.OrdinalIgnoreCase);
        if (Edition.IsTrial) mapAgents.Clear();
        systemBrowserLogin = config.SystemBrowserLogin;
        if (previewAgentId != null) mainAgent = agents.FirstOrDefault(a => a.Id.Equals(previewAgentId, StringComparison.OrdinalIgnoreCase));
        autoStart.Checked = config.StartOnLaunch;
        region.SelectedIndex = Math.Max(0, Array.IndexOf(RegionValues, config.Region));

        var root = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Background, AutoScroll = false };
        var header = BuildHeader(); header.Dock = DockStyle.None; header.TabIndex = 0;
        var dashboard = BuildDashboard(); dashboard.Dock = DockStyle.Fill; dashboard.TabIndex = 1;
        dashboardPage = dashboard;
        pageHost.Controls.Add(dashboard);
        matchHistory = new MatchHistoryControl(api, agents) { Visible = false, Dock = DockStyle.Fill, TabIndex = 2 };
        storePreview = new StorePreviewControl(api) { Visible = false, Dock = DockStyle.Fill, TabIndex = 3 };
        pageHost.Controls.Add(matchHistory);
        pageHost.Controls.Add(storePreview);
        var version = new Label { Text = "v" + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? ""), ForeColor = Theme.Muted, BackColor = Theme.Background, Font = new Font("Segoe UI", 8.5f), TextAlign = ContentAlignment.MiddleRight, AutoSize = false, TabStop = false, AccessibleName = "バージョン" };
        root.Controls.Add(pageHost); root.Controls.Add(header); root.Controls.Add(version);
        if (Edition.IsTrial) root.Controls.Add(trialCorner);
        Controls.Add(root);
        void ArrangeRoot()
        {
            var width = Math.Max(UiMetrics.Px(this, 850), root.ClientSize.Width - UiMetrics.Px(this, 39));
            var height = Math.Max(UiMetrics.Px(this, Edition.IsTrial ? 760 : 640), root.ClientSize.Height - UiMetrics.Px(this, 87));
            var offset = root.AutoScrollPosition;
            header.Bounds = new Rectangle(offset.X + UiMetrics.Px(this, 20), offset.Y + UiMetrics.Px(this, 13), width, UiMetrics.Px(this, 49));
            pageHost.Bounds = new Rectangle(offset.X + UiMetrics.Px(this, 20), offset.Y + UiMetrics.Px(this, 68), width, height);
            var footerY = offset.Y + UiMetrics.Px(this, 68) + height;
            version.Bounds = new Rectangle(offset.X + UiMetrics.Px(this, 20), footerY, width, UiMetrics.Px(this, 19));
            if (Edition.IsTrial)
            {
                version.Bounds = new Rectangle(offset.X + UiMetrics.Px(this, 20) + width - UiMetrics.Px(this, 65), footerY, UiMetrics.Px(this, 65), UiMetrics.Px(this, 19));
                trialCorner.Bounds = new Rectangle(offset.X + UiMetrics.Px(this, 20) + width - UiMetrics.Px(this, 265), footerY, UiMetrics.Px(this, 195), UiMetrics.Px(this, 19));
            }
        }
        root.Layout += (_, _) => ArrangeRoot();
        ArrangeRoot();

        SetHero(); UpdateMapSummary();
        if (Edition.IsTrial) UpdateTrialStatus();
        hero.ChooseClicked += async (_, _) => await PickAgent();
        mapSettings.Click += (_, _) => ConfigureMaps();
        login.Click += async (_, _) => await SignIn();
        logout.Click += async (_, _) => await SignOut();
        dashboardTab.Click += (_, _) => ShowMainPage(MainPage.Dashboard);
        matchHistoryButton.Click += (_, _) => ShowMainPage(MainPage.MatchHistory);
        storeTab.Click += (_, _) => ShowMainPage(MainPage.Store);
        storePreview.LoginRequested += async (_, _) => await SignIn();
        storePreview.LogoutRequested += async (_, _) => await SignOut();
        start.Click += async (_, _) => await TogglePickMonitoring();
        autoQueue.Click += async (_, _) => await ToggleAutoQueue();
        region.SelectedIndexChanged += async (_, _) => { SaveSettings(); if (api.HasAccountSession) { await SignOut(clearBrowserSession: false); await RestoreAccountSession(ReadSettings()); } };
        autoStart.CheckedChanged += (_, _) => SaveSettings();
        Resize += (_, _) => { if (WindowState == FormWindowState.Minimized) { Hide(); tray.ShowBalloonTip(1500, "SpikeSide", monitor != null ? autoQueueActive ? "自動インキューを監視中です" : "自動ピックを監視中です" : "監視は停止中です", ToolTipIcon.Info); } };
        tray.DoubleClick += (_, _) => Restore();
        var menu = new ContextMenuStrip();
        menu.Items.Add("開く", null, (_, _) => Restore());
        menu.Items.Add("監視を停止", null, async (_, _) => await StopMonitor());
        menu.Items.Add("終了", null, (_, _) => Close());
        tray.ContextMenuStrip = menu;
        UpdateConnectionUi();
        Load += (_, _) => { ActiveControl = login.Visible ? login : region; root.AutoScrollPosition = Point.Empty; };

        Shown += async (_, _) =>
        {
            if (smokePath != null)
            {
                if (loginSmokeTest)
                {
                    try
                    {
                        loginWindow = new LoginWindow();
                        var loginTask = loginWindow.Authenticate(this, false, lifetime.Token);
                        await Task.Delay(12000);
                        await loginWindow.CaptureDiagnostic(smokePath);
                        lifetime.Cancel(); await loginTask;
                    }
                    catch (Exception e) { File.WriteAllText(Path.ChangeExtension(smokePath, ".error.txt"), e.GetType().Name); }
                    Close(); return;
                }
                if (mapSmokeTest)
                {
                    using var dialog = new MapSettingsDialog(agents, mapAgents);
                    dialog.Shown += async (_, _) =>
                    {
                        await Task.Delay(900); dialog.TopMost = true; dialog.Activate();
                        if (Environment.GetCommandLineArgs().Contains("--preview-bottom")) { dialog.PreviewBottom(); await Task.Delay(300); }
                        using var shot = new Bitmap(dialog.Width, dialog.Height);
                        using (var graphics = Graphics.FromImage(shot)) graphics.CopyFromScreen(dialog.Location, Point.Empty, dialog.Size);
                        shot.Save(smokePath); dialog.DialogResult = DialogResult.Cancel; dialog.Close();
                    };
                    dialog.ShowDialog(this); Close(); return;
                }
                if (pickerSmokeTest)
                {
                    var previewOwned = Environment.GetCommandLineArgs().Contains("--preview-partial-ownership")
                        ? agents.Where((_, index) => index % 2 == 0).Select(a => a.Id).ToHashSet(StringComparer.OrdinalIgnoreCase) : null;
                    using var picker = new AgentPickerDialog(agents, mainAgent?.Id, previewOwned);
                    picker.Shown += async (_, _) =>
                    {
                        await Task.Delay(900);
                        picker.TopMost = true;
                        picker.Activate();
                        using var shot = new Bitmap(picker.Width, picker.Height);
                        using (var graphics = Graphics.FromImage(shot)) graphics.CopyFromScreen(picker.Location, Point.Empty, picker.Size);
                        shot.Save(smokePath);
                        picker.DialogResult = DialogResult.Cancel;
                        picker.Close();
                    };
                    picker.ShowDialog(this); Close(); return;
                }
                var previewMap = Environment.GetCommandLineArgs().SkipWhile(x => x != "--preview-map").Skip(1).FirstOrDefault();
                if (previewMap != null) UpdateMap(previewMap);
                if (Environment.GetCommandLineArgs().Contains("--preview-logged-out")) Report("未ログイン");
                if (Environment.GetCommandLineArgs().Contains("--preview-minimum")) Size = MinimumSize;
                if (Edition.IsTrial && Environment.GetCommandLineArgs().Contains("--preview-trial-exhausted")) { SetTrialRemaining(0); ReportPick("この試合の処理は完了 · 次の試合も自動で検知します"); }
                Hide(); Show(); TopMost = true; Activate();
                if (Environment.GetCommandLineArgs().Contains("--preview-connected")) { ApplyAccountLabels(true, "ExamplePlayer#1234"); login.Enabled = false; logout.Visible = true; }
                if (Environment.GetCommandLineArgs().Contains("--preview-match-history"))
                {
                    ShowMainPage(MainPage.MatchHistory);
                    await matchHistory!.ActivatePageAsync();
                    if (Environment.GetCommandLineArgs().Contains("--preview-career-tab"))
                        matchHistory!.ShowCareerForPreview();
                }
                else if (Environment.GetCommandLineArgs().Contains("--preview-store"))
                {
                    ShowMainPage(MainPage.Store);
                    await storePreview!.ActivatePageAsync();
                    if (Environment.GetCommandLineArgs().Contains("--preview-catalog"))
                        storePreview.ShowCatalogForPreview();
                    if (Environment.GetCommandLineArgs().Contains("--preview-video"))
                    {
                        await Task.Delay(700);
                        await storePreview.PlayVideoForPreviewAsync(TimeSpan.FromSeconds(20));
                        File.WriteAllText(Path.ChangeExtension(smokePath, ".video-result.txt"), storePreview.PreviewVideoResult);
                    }
                }
                await Task.Delay(700);
                using var bitmap = new Bitmap(Width, Height);
                using (var graphics = Graphics.FromImage(bitmap)) graphics.CopyFromScreen(Location, Point.Empty, Size);
                bitmap.Save(smokePath); Close();
            }
            else
            {
                // Runs in the background; login restoration never waits for the catalogue.
                StartAgentCatalogRefresh();
                await RestoreAccountSession(config);
                _ = MaintainAccountSession();
            }
        };
        FormClosing += async (_, e) =>
        {
            if (closing) return;
            e.Cancel = true; closing = true; lifetime.Cancel(); await StopMonitor();
            if (accountRefresh != null) { try { await accountRefresh; } catch (OperationCanceledException) { } }
            if (agentCatalogRefresh != null) { try { await agentCatalogRefresh; } catch (Exception) { } }
            matchHistory?.Dispose(); storePreview?.Dispose(); loginWindow?.Dispose(); api.Dispose(); tray.Dispose(); BeginInvoke(Close);
        };
    }

    private Control BuildHeader()
    {
        var header = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Background, Margin = Padding.Empty };
        var brand = new Panel { Dock = DockStyle.Left, Width = UiMetrics.Px(this, 315), BackColor = Theme.Background };
        brand.Paint += (_, e) =>
        {
            var size = UiMetrics.Px(brand, 40);
            AppIcon.Draw(e.Graphics, new Rectangle(0, UiMetrics.Px(brand, 1), size, size));
        };
        brand.Controls.Add(new Label { Text = Edition.IsTrial ? "SpikeSide 体験版" : "SpikeSide", ForeColor = Theme.Text, Font = new Font("Segoe UI", 18, FontStyle.Bold), AutoSize = true, Location = new Point(49, 2) });
        var loginBar = new FlowLayoutPanel { Anchor = AnchorStyles.Top | AnchorStyles.Right, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(0, 4, 0, 0), Margin = Padding.Empty };
        dashboardTab.Margin = Padding.Empty;
        matchHistoryButton.Margin = new Padding(6, 0, 0, 0);
        storeTab.Margin = new Padding(6, 0, 8, 0);
        logout.Margin = new Padding(7, 0, 0, 0); login.Margin = Padding.Empty;
        loginBar.Controls.Add(dashboardTab); loginBar.Controls.Add(matchHistoryButton); loginBar.Controls.Add(storeTab);
        loginBar.Controls.Add(login); loginBar.Controls.Add(logout);
        void PlaceActions()
        {
            loginBar.Location = new Point(Math.Max(0, header.ClientSize.Width - loginBar.Width), Math.Max(0, (header.ClientSize.Height - loginBar.Height) / 2));
        }
        header.Controls.Add(loginBar);
        header.Controls.Add(brand);
        header.Resize += (_, _) => PlaceActions();
        loginBar.SizeChanged += (_, _) => PlaceActions();
        PlaceActions();
        return header;
    }

    private Control BuildDashboard()
    {
        var columns = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Theme.Background, ColumnCount = 2, RowCount = 2, Margin = Padding.Empty, Padding = new Padding(0, 6, 0, 6) };
        columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58)); columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
        columns.RowStyles.Add(new RowStyle(SizeType.Percent, 50)); columns.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        matchMap.Margin = new Padding(0, 0, 16, 7);
        hero.Dock = DockStyle.Fill; hero.Margin = new Padding(0, 7, 16, 0);
        var sidebar = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Theme.Background, ColumnCount = 1, RowCount = 2, Margin = new Padding(0, 0, 0, 7) };
        sidebar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        sidebar.RowStyles.Add(new RowStyle(SizeType.Percent, 57)); sidebar.RowStyles.Add(new RowStyle(SizeType.Percent, 43));
        settingsPanel.Margin = new Padding(0, 0, 0, 12); mapSettingsCard.Margin = Padding.Empty; startPanel.Margin = new Padding(0, 7, 0, 0);
        sidebar.Controls.Add(settingsPanel, 0, 0); sidebar.Controls.Add(mapSettingsCard, 0, 1);
        columns.Controls.Add(matchMap, 0, 0); columns.Controls.Add(hero, 0, 1);
        columns.Controls.Add(sidebar, 1, 0); columns.Controls.Add(startPanel, 1, 1);

        var connection = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Theme.Surface, Padding = new Padding(9, 4, 9, 4), RowCount = 4, ColumnCount = 1, Margin = Padding.Empty };
        connection.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        connection.RowStyles.Add(new RowStyle(SizeType.Absolute, 32)); connection.RowStyles.Add(new RowStyle(SizeType.Absolute, 30));
        connection.RowStyles.Add(new RowStyle(SizeType.Absolute, 52)); connection.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        connection.Controls.Add(SectionTitle("接続設定"), 0, 0);
        accountInfo.Font = new Font("Yu Gothic UI", 9.5f, FontStyle.Bold); connection.Controls.Add(accountInfo, 0, 1);
        var regionRow = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Theme.Surface, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        regionRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 94)); regionRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        regionRow.Controls.Add(FieldLabel("接続地域"), 0, 0); region.Dock = DockStyle.Fill; region.Margin = Padding.Empty; regionRow.Controls.Add(region, 1, 0);
        connection.Controls.Add(regionRow, 0, 2);
        autoStart.Margin = new Padding(0, 4, 0, 0); autoStart.Dock = DockStyle.Fill; connection.Controls.Add(autoStart, 0, 3);
        settingsPanel.Controls.Add(connection);

        var maps = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Theme.Surface, Padding = new Padding(9, 5, 9, 6), ColumnCount = 1, RowCount = 3, Margin = Padding.Empty };
        maps.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        maps.RowStyles.Add(new RowStyle(SizeType.Absolute, 32)); maps.RowStyles.Add(new RowStyle(SizeType.Absolute, 30)); maps.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        if (Edition.IsTrial)
        {
            maps.Controls.Add(SectionTitle("体験版"), 0, 0);
            maps.Controls.Add(trialRemaining, 0, 1);
            maps.Controls.Add(trialDescription, 0, 2);
        }
        else
        {
            maps.Controls.Add(SectionTitle("マップ別設定"), 0, 0);
            mapSummary.Font = new Font("Yu Gothic UI", 9.5f); mapSummary.Margin = Padding.Empty; maps.Controls.Add(mapSummary, 0, 1);
            mapSettings.Margin = new Padding(0, 4, 0, 1); maps.Controls.Add(mapSettings, 0, 2);
        }
        mapSettingsCard.Controls.Add(maps);

        var action = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Theme.Surface, Padding = new Padding(9, 6, 9, 6), RowCount = 8, ColumnCount = 1, Margin = Padding.Empty };
        action.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        action.RowStyles.Add(new RowStyle(SizeType.Percent, 50)); action.RowStyles.Add(new RowStyle(SizeType.Absolute, 31));
        action.RowStyles.Add(new RowStyle(SizeType.Absolute, 54)); action.RowStyles.Add(new RowStyle(SizeType.Absolute, 54));
        action.RowStyles.Add(new RowStyle(SizeType.Absolute, 30)); action.RowStyles.Add(new RowStyle(SizeType.Absolute, 28));
        action.RowStyles.Add(new RowStyle(SizeType.Absolute, 30)); action.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        action.Controls.Add(SectionTitle("自動ピック"), 0, 1);
        start.Dock = DockStyle.Fill; start.Margin = new Padding(0, 0, 0, 5); start.Text = pickMonitoring ? "監視を停止" : "監視を開始"; action.Controls.Add(start, 0, 2);
        autoQueue.Dock = DockStyle.Fill; autoQueue.Margin = Padding.Empty;
        if (!Edition.IsTrial) action.Controls.Add(autoQueue, 0, 3);
        var statusRow = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Theme.Surface, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        statusRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 22)); statusRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        statusRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var led = new Panel { Dock = DockStyle.Fill, Margin = Padding.Empty }; statusLed = led;
        led.Paint += (_, e) =>
        {
            var color = monitor == null ? Theme.Muted : Theme.Green; var diameter = UiMetrics.Px(led, 10);
            using var brush = new SolidBrush(color); e.Graphics.SmoothingMode = SmoothingMode.AntiAlias; e.Graphics.FillEllipse(brush, UiMetrics.Px(led, 2), (led.Height - diameter) / 2, diameter, diameter);
        };
        status.Font = new Font("Yu Gothic UI", 10.5f, FontStyle.Bold); status.Margin = Padding.Empty;
        detail.Font = new Font("Yu Gothic UI", 10); detail.Margin = Padding.Empty;
        queueDetail.Font = new Font("Yu Gothic UI", 9); queueDetail.Margin = Padding.Empty;
        statusRow.Controls.Add(led, 0, 0); statusRow.Controls.Add(status, 1, 0);
        action.Controls.Add(statusRow, 0, 4); action.Controls.Add(detail, 0, 5); if (!Edition.IsTrial) action.Controls.Add(queueDetail, 0, 6);
        startPanel.Controls.Add(action); return columns;
    }

    private static Label SectionTitle(string text) => new() { Text = text, ForeColor = Theme.Text, Font = new Font("Yu Gothic UI", 12.5f, FontStyle.Bold), Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };

    private void RestoreAgent(Settings config)
    {
        Agent? Find(string id) => agents.FirstOrDefault(a => a.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        mainAgent = string.IsNullOrWhiteSpace(config.AgentId) ? null : Find(config.AgentId);
        pendingAgentId = mainAgent == null && !string.IsNullOrWhiteSpace(config.AgentId) ? config.AgentId : "";
    }
    private void SetHero() { hero.Agent = mainAgent; UpdateMap(displayedMap); }
    private void UpdateMapSummary() => mapSummary.Text = $"{MapCatalog.Maps.Count(m => mapAgents.ContainsKey(m.Key))} マップで個別設定中";
    private void UpdateTrialStatus()
    {
        try
        {
            var remaining = trialUsage.Remaining();
            SetTrialRemaining(remaining);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or System.Security.Cryptography.CryptographicException or System.Security.SecurityException)
        { trialRemainingCount = null; trialCorner.Text = "残り回数を確認できません"; trialRemaining.Text = "利用履歴を読み取れません。"; }
    }
    private void SetTrialRemaining(int remaining)
    {
        trialRemainingCount = remaining;
        trialCorner.Text = $"体験版 残り {remaining} / {TrialUsage.Limit} 回";
        trialRemaining.Text = $"残り {remaining} / {TrialUsage.Limit} 回";
        trialDescription.Text = remaining == 0 ? "体験版の利用回数を使い切りました。\n製品版をご利用ください。" : "1試合のピック試行で1回消費します。\nマップ別設定・自動インキューは利用不可";
    }
    private string? AuthorizeTrialPick(string matchId)
    {
        try
        {
            var allowed = trialUsage.Reserve(matchId);
            UpdateTrialStatus();
            return allowed ? null : TrialFinishedMessage;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or System.Security.Cryptography.CryptographicException or System.Security.SecurityException)
        {
            UpdateTrialStatus();
            return "体験版の利用履歴を保存できません";
        }
    }
    private string? CheckTrialMatch(string matchId)
    {
        if (trialRemainingCount != 0) return null;
        try { return trialUsage.CanUse(matchId) ? null : TrialFinishedMessage; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or JsonException or InvalidDataException or System.Security.Cryptography.CryptographicException or System.Security.SecurityException)
        { return "体験版の利用履歴を確認できません"; }
    }
    private void ReportPick(string message)
    {
        if (Edition.IsTrial && trialRemainingCount == 0 && message is ("マッチを検索中" or "自動ピック・確定を確認しました" or "この試合の処理は完了 · 次の試合も自動で検知します" or "選択確定済み · 次のマッチを検索中"))
            message = TrialFinishedMessage;
        Report(message);
    }
    private void UpdateMap(string? mapId)
    {
        displayedMap = mapId ?? "";
        var key = MapCatalog.Key(displayedMap);
        var selectedId = mapAgents.TryGetValue(key, out var mapped) ? mapped : mainAgent?.Id;
        var selected = agents.FirstOrDefault(a => a.Id.Equals(selectedId, StringComparison.OrdinalIgnoreCase));
        matchMap.SetMatch(displayedMap, selected?.Name ?? "");
    }
    private void ConfigureRelay()
    {
        using var dialog = new NotificationRelayDialog();
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        if (dialog.ClearRelay) relayOptions = null;
        else if (dialog.SelectedRelay != null) relayOptions = dialog.SelectedRelay;
    }
    private async Task PickAgent()
    {
        if (!api.HasAccountSession) { Report("Riotアカウントにログインしてください"); return; }
        if (ownedAgents == null || ownedSubject != api.Subject) await LoadOwnedAgents(lifetime.Token);
        if (ownedAgents == null || ownedSubject != api.Subject || !api.HasAccountSession) return;
        using var dialog = new AgentPickerDialog(agents, mainAgent?.Id, ownedAgents);
        if (dialog.ShowDialog(this) != DialogResult.OK || dialog.SelectedAgent == null) return;
        // The catalogue may have refreshed while the dialog was open; keep the chosen ID, use the current record.
        mainAgent = AgentCatalog.ResolveSelection(agents, dialog.SelectedAgent);
        pendingAgentId = "";
        SetHero();
        SaveSettings();
        UpdateConnectionUi();
    }

    private void ConfigureMaps()
    {
        if (Edition.IsTrial) return;
        if (!api.HasAccountSession || ownedAgents == null || ownedSubject != api.Subject) { Report("エージェントの所有情報を確認してください"); return; }
        using var dialog = new MapSettingsDialog(agents, mapAgents, ownedAgents);
        if (dialog.ShowDialog(this) != DialogResult.OK) return;
        mapAgents = dialog.MapAgents; UpdateMapSummary(); UpdateMap(displayedMap); SaveSettings();
    }

    private Settings ReadSettings() => new Settings
    {
        AgentId = mainAgent?.Id ?? "",
        MapAgents = new(mapAgents, StringComparer.OrdinalIgnoreCase),
        StartOnLaunch = autoStart.Checked,
        Region = RegionValues[Math.Clamp(region.SelectedIndex, 0, RegionValues.Length - 1)],
        SystemBrowserLogin = systemBrowserLogin
    }.Normalize();

    private bool SaveSettings()
    {
        try
        {
            var settings = ReadSettings();
            // Do not erase a saved agent that this catalogue does not know yet.
            if (mainAgent == null && pendingAgentId.Length > 0) settings.AgentId = pendingAgentId;
            Storage.Save(settings);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { Report("設定を保存できません"); return false; }
    }

    private void UpdateConnectionUi()
    {
        UpdateAgentAccessUi();
        ApplyAccountLabels(api.HasAccountSession, api.AccountName);
        login.Primary = false;
        login.Visible = true;
        login.Enabled = !closing && (loginAttempt != null || !busy && monitor == null && !api.HasAccountSession);
        dashboardTab.Enabled = !closing;
        matchHistoryButton.Enabled = !closing && !busy;
        storeTab.Enabled = !closing && !busy;
        logout.Visible = api.HasAccountSession;
        login.AccessibleName = api.HasAccountSession ? $"接続済み: {api.AccountName} / {api.RegionLabel}" : loginAttempt != null ? "ログインを中止" : "Riotアカウントにログイン";
        start.Text = stopping ? "停止中…" : pickMonitoring ? "監視を停止" : "監視を開始";
        start.Enabled = !busy && !closing && !stopping;
        autoQueue.Text = stopping ? "停止中…" : autoQueueActive ? "自動インキュー停止" : "自動インキュー開始";
        autoQueue.Primary = autoQueueActive;
        autoQueue.Enabled = !busy && !closing && !stopping;
        queueDetail.ForeColor = autoQueueActive ? Theme.Green : Theme.Muted;
        if (!autoQueueActive) SetQueueStatus("自動インキューは停止中");
        start.Invalidate();
        autoQueue.Invalidate();
    }
    private void UpdateAgentAccessUi()
    {
        var loggedIn = api.HasAccountSession && !closing;
        hero.ChooseEnabled = inputEnabled && loggedIn;
        hero.AgentAvailable = mainAgent == null || !loggedIn || ownedAgents == null || ownedSubject != api.Subject || ownedAgents.Contains(mainAgent.Id);
        mapSettingsCard.Enabled = true;
        mapSettings.Enabled = inputEnabled && loggedIn && ownedAgents != null && ownedSubject == api.Subject;
    }

    private void ApplyAccountLabels(bool connected, string accountName)
    {
        login.Text = connected ? accountName : loginAttempt != null ? "ログインを中止" : "Riotにログイン";
        accountInfo.Text = connected ? "接続済み" : "Riotアカウント: 未接続";
        accountInfo.ForeColor = connected ? Theme.Green : Theme.Muted;
        if (IsHandleCreated)
        {
            var minimum = UiMetrics.Px(login, 176);
            var maximum = UiMetrics.Px(login, 350);
            login.Width = connected ? Math.Clamp(TextRenderer.MeasureText(login.Text, login.Font).Width + UiMetrics.Px(login, 32), minimum, maximum) : minimum;
        }
    }

    private void ShowMainPage(MainPage page)
    {
        if (closing || selectedPage == page) return;
        selectedPage = page;
        var next = page switch
        {
            MainPage.MatchHistory => (Control)matchHistory!,
            MainPage.Store => storePreview!,
            _ => dashboardPage!
        };
        pageHost.SuspendLayout();
        try
        {
            next.BringToFront();
            next.Visible = true;
            foreach (Control control in pageHost.Controls)
                if (control != next) control.Hide();
        }
        finally { pageHost.ResumeLayout(); }
        Text = page switch
        {
            MainPage.MatchHistory => "試合分析",
            MainPage.Store => "STORE確認",
            _ => Edition.IsTrial ? "SpikeSide 体験版" : "SpikeSide"
        };
        dashboardTab.Primary = page == MainPage.Dashboard;
        matchHistoryButton.Primary = page == MainPage.MatchHistory;
        storeTab.Primary = page == MainPage.Store;
        if (page != MainPage.Dashboard)
            _ = ActivatePageSafelyAsync(page);
    }

    private async Task ActivatePageSafelyAsync(MainPage page)
    {
        try
        {
            if (page == MainPage.MatchHistory && matchHistory is not null)
                await matchHistory.ActivatePageAsync();
            else if (page == MainPage.Store && storePreview is not null)
                await storePreview.ActivatePageAsync();
        }
        catch (OperationCanceledException) when (closing || IsDisposed) { }
        catch (Exception ex)
        {
            NativePageStyle.LogPageError($"PageActivation-{page}", ex);
            if (page == MainPage.MatchHistory)
                matchHistory?.ReportActivationError("試合履歴を開けませんでした。Riotアカウントを確認して再度開いてください。");
            else if (page == MainPage.Store)
                storePreview?.ReportActivationError("STOREを開けませんでした。カタログ更新を押して再試行してください。");
        }
    }

    private enum MainPage { Dashboard, MatchHistory, Store }

    private async Task NotifyEmbeddedPagesAsync()
    {
        var history = selectedPage == MainPage.MatchHistory && matchHistory != null
            ? ActivatePageSafelyAsync(MainPage.MatchHistory) : Task.CompletedTask;
        await Task.WhenAll(history, storePreview?.NotifyAccountChangedAsync() ?? Task.CompletedTask);
    }

    private async Task LoadOwnedAgents(CancellationToken ct)
    {
        if (!api.HasAccountSession || closing) return;
        if (ownedAgents != null && ownedSubject == api.Subject) return;
        if (ownedLoad != null) { await ownedLoad.WaitAsync(ct); return; }
        var subject = api.Subject;
        async Task Fetch()
        {
            try
            {
                var result = await api.GetOwnedAgents(ct, agents);
                if (!closing && api.HasAccountSession && subject == api.Subject)
                {
                    ownedAgents = result; ownedSubject = subject;
                    UpdateAgentAccessUi();
                }
            }
            catch (OperationCanceledException) when (ct.IsCancellationRequested) { throw; }
            catch (Exception e) when (e is HttpRequestException or ApiException or IOException or InvalidDataException or JsonException or InvalidOperationException or OperationCanceledException)
            {
                if (!closing && api.HasAccountSession && subject == api.Subject)
                    Report("所有エージェントを取得できません · エージェント選択で再試行してください");
            }
        }
        ownedLoad = Fetch();
        try { await ownedLoad; }
        finally { ownedLoad = null; }
    }

    private async Task SignIn()
    {
        if (loginAttempt != null) { loginAttempt.Cancel(); return; }
        if (busy) return;
        using var attempt = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        loginAttempt = attempt;
        busy = true; SetInputEnabled(false); UpdateConnectionUi();
        try
        {
            refreshStop?.Cancel();
            if (accountRefresh != null) { try { await accountRefresh; } catch (OperationCanceledException) { } }
            api.Logout(); ownedAgents = null; ownedSubject = ""; engine = null; engineSubject = "";
            Report("ブラウザーでRiotにログインしてください");
            await using var browser = new PersistentBrowser();
            var result = await new SystemBrowserAuth(AuthFlow.NewState()).Run((url, _) => browser.Open(url, false), attempt.Token);
            if (result == null) { Report("ログインできませんでした · 再度お試しください"); return; }
            await api.Login(result, ReadSettings().Region, attempt.Token);
            systemBrowserLogin = true;
            SaveSettings();
            await LoadOwnedAgents(attempt.Token);
            if (ownedAgents != null) Report("アカウントを接続しました");
        }
        catch (OperationCanceledException)
        { if (!closing) Report(attempt.IsCancellationRequested ? "ログインを中止しました" : "ログインがタイムアウトしました"); }
        catch (SocketException) { Report("ブラウザーログイン用のポート80を使用できません"); }
        catch (Exception e) { Report(LoginError(e)); }
        finally { loginAttempt = null; busy = false; SetInputEnabled(true); UpdateConnectionUi(); }
        if (api.HasAccountSession) await NotifyEmbeddedPagesAsync();
        if (api.HasAccountSession && autoStart.Checked) { pickMonitoring = true; StartMonitor(); }
    }

    private async Task RestoreAccountSession(Settings config)
    {
        if (busy || closing || lifetime.IsCancellationRequested || !config.SystemBrowserLogin) return;
        Report("ログインを復元中");
        bool restored;
        try { restored = await RefreshAccountSession(lifetime.Token); }
        catch (OperationCanceledException) { return; }
        if (closing || busy) return;
        if (restored)
        {
            Report(ownedAgents == null ? "接続済み · エージェントの所有情報を取得できません" : "アカウントを接続しました");
            UpdateConnectionUi();
            await NotifyEmbeddedPagesAsync();
            if (autoStart.Checked) { pickMonitoring = true; StartMonitor(); }
        }
        else Report("ログインを復元できません · 再接続を試みます");
    }

    private async Task MaintainAccountSession()
    {
        try
        {
            while (!lifetime.IsCancellationRequested)
            {
                await Task.Delay(TimeSpan.FromMinutes(1), lifetime.Token);
                if (!busy && api.HasAccountSession && (ownedAgents == null || ownedSubject != api.Subject))
                    await LoadOwnedAgents(lifetime.Token);
                if (!systemBrowserLogin || busy || closing ||
                    api.HasAccountSession && DateTimeOffset.UtcNow < api.ExpiresAt.AddMinutes(-5)) continue;
                if (await RefreshAccountSession(lifetime.Token))
                {
                    UpdateConnectionUi();
                    if (monitor == null) Report(ownedAgents == null ? "接続済み · エージェントの所有情報を取得できません" : "アカウントを接続しました");
                    if (autoStart.Checked && monitor == null) { pickMonitoring = true; StartMonitor(); }
                }
            }
        }
        catch (OperationCanceledException) { }
    }

    private async Task<bool> RefreshAccountSession(CancellationToken ct, bool forceBrowser = false)
    {
        if (accountRefresh != null) return await accountRefresh.WaitAsync(ct);
        using var stop = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, ct);
        stop.CancelAfter(TimeSpan.FromSeconds(30));
        refreshStop = stop;
        accountRefresh = RefreshAccountSessionCore(stop.Token, forceBrowser);
        try { return await accountRefresh; }
        finally { accountRefresh = null; refreshStop = null; }
    }

    private async Task<bool> RefreshAccountSessionCore(CancellationToken ct, bool forceBrowser)
    {
        try
        {
            var settings = ReadSettings();
            if (!forceBrowser && api.HasAccountSession && await api.TryRefreshAccountFromLocal(settings.Region, ct))
            {
                await LoadOwnedAgents(ct);
                return true;
            }
            await using var browser = new PersistentBrowser();
            var result = await new SystemBrowserAuth(AuthFlow.NewState(), silent: true)
                .Run((url, _) => browser.Open(url, true), ct);
            if (result == null) return false;
            await api.Login(result, settings.Region, ct, api.HasAccountSession ? api.Subject : null);
            if (ownedSubject != api.Subject) { ownedAgents = null; ownedSubject = ""; }
            await LoadOwnedAgents(ct);
            UpdateConnectionUi();
            return true;
        }
        catch (OperationCanceledException) when (!lifetime.IsCancellationRequested) { return false; }
        catch (Exception e) when (e is SocketException or BrowserIsolationException or IOException or UnauthorizedAccessException or
            System.ComponentModel.Win32Exception or HttpRequestException or ApiException or ClientUnavailableException or InvalidOperationException or JsonException or InvalidDataException)
        { return false; }
    }

    private async Task SignOut(bool clearBrowserSession = true)
    {
        if (busy) return;
        busy = true;
        try
        {
            matchHistory?.ResetSession();
            storePreview?.ResetSession();
            ShowMainPage(MainPage.Dashboard);
            refreshStop?.Cancel();
            await StopMonitor();
            if (accountRefresh != null) { try { await accountRefresh; } catch (OperationCanceledException) { } }
            api.Logout(); ownedAgents = null; ownedSubject = ""; engine = null; engineSubject = "";
            if (storePreview != null) await storePreview.NotifyAccountChangedAsync();
            if (clearBrowserSession)
            {
                systemBrowserLogin = false; SaveSettings();
                PersistentBrowser.Clear();
                if (loginWindow != null) await loginWindow.ClearSession();
            }
        }
        catch { Report("ログイン情報を消去できませんでした · アプリを終了してください"); }
        finally { busy = false; UpdateConnectionUi(); }
    }

    private void SetInputEnabled(bool enabled)
    {
        inputEnabled = enabled;
        settingsPanel.Enabled = region.Enabled = autoStart.Enabled = enabled;
        hero.Enabled = enabled;
        UpdateAgentAccessUi();
        login.Enabled = enabled && !api.HasAccountSession;
        logout.Enabled = enabled && api.HasAccountSession;
        start.Enabled = !busy && !closing && !stopping;
        autoQueue.Enabled = !busy && !closing && !stopping;
    }

    private async Task TogglePickMonitoring()
    {
        if (pickMonitoring)
        {
            pickMonitoring = false;
            await StopMonitor();
            return;
        }
        if (!api.HasAccountSession) { Report("Riotアカウントにログインしてください"); return; }
        if (ownedAgents == null || ownedSubject != api.Subject) { Report("エージェントの所有情報を確認してください"); return; }
        if (mainAgent == null && mapAgents.Count == 0) { Report("エージェントを選んでください"); return; }
        pickMonitoring = true;
        if (monitor == null) StartMonitor();
        if (monitor == null) pickMonitoring = false;
        UpdateConnectionUi();
    }

    private async Task ToggleAutoQueue()
    {
        if (Edition.IsTrial) return;
        if (autoQueueActive)
        {
            var activeQueue = queueEngine;
            autoQueueActive = false;
            if (!pickMonitoring) await StopMonitor(activeQueue);
            else
            {
                UpdateConnectionUi();
                SetQueueStatus("キュー検索をキャンセル中");
                await CancelQueueSearch(activeQueue);
                Report("自動インキューを停止しました");
            }
            return;
        }
        autoQueueActive = true;
        SetQueueStatus("キュー情報を確認中");
        if (monitor == null) StartMonitor();
        if (monitor == null) autoQueueActive = false;
        UpdateConnectionUi();
        if (autoQueueActive) Report("自動インキューを開始しました");
    }

    private void StartMonitor()
    {
        if (busy || stopping || monitor != null || !pickMonitoring && !autoQueueActive) return;
        if (!RiotApi.IsValorantRunning())
        {
            pickMonitoring = false;
            autoQueueActive = false;
            Report("ゲームが起動されていないので監視を実行できません");
            UpdateConnectionUi();
            return;
        }
        var settings = ReadSettings();
        if (pickMonitoring && string.IsNullOrEmpty(settings.AgentId) && settings.MapAgents.Count == 0) { Report("エージェントを選んでください"); return; }
        if (!api.HasAccountSession) { Report("Riotアカウントにログインしてください"); return; }
        if (pickMonitoring && (ownedAgents == null || ownedSubject != api.Subject)) { pickMonitoring = false; Report("エージェントの所有情報を確認してください"); UpdateConnectionUi(); return; }
        if (pickMonitoring && settings.AgentId.Length > 0 && !ownedAgents!.Contains(settings.AgentId)) { pickMonitoring = false; Report("設定したエージェントは未所有です · 別のエージェントを選んでください"); UpdateConnectionUi(); return; }
        if (!SaveSettings()) return;
        monitor = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        queueEngine = new AutoQueueEngine(api, SetQueueStatus);
        SetInputEnabled(false);
        status.Text = "監視中"; status.ForeColor = Theme.Green;
        statusLed?.Invalidate();
        monitorTask = RunMonitor(settings, monitor.Token);
        UpdateConnectionUi();
    }

    private async Task RunMonitor(Settings settings, CancellationToken ct)
    {
        Report("マッチを検索中");
        var mapObserver = new MapObserver(api, UpdateMap);
        using var notificationsStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        var queueSignals = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
        Task? notifications = null;
        Task? relayNotifications = null;
        Task? notificationChecks = null;
        var retryAfter = DateTimeOffset.MinValue;
        var queueRetryAfter = DateTimeOffset.MinValue;
        var nextQueuePulse = DateTimeOffset.MinValue;
        var queueStateChanged = false;
        void Backoff(int milliseconds)
        {
            var until = DateTimeOffset.UtcNow.AddMilliseconds(milliseconds);
            if (until > retryAfter) retryAfter = until;
        }
        void QueueBackoff(int milliseconds)
        {
            var until = DateTimeOffset.UtcNow.AddMilliseconds(milliseconds);
            if (until > queueRetryAfter) queueRetryAfter = until;
        }
        void QueueChanged()
        {
            if (!autoQueueActive) return;
            queueStateChanged = true;
            queueSignals.Writer.TryWrite(true);
        }
        async Task WaitForRetry(CancellationToken token)
        {
            while (true)
            {
                var remaining = retryAfter - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero) return;
                await Task.Delay(remaining, token);
            }
        }
        async Task WaitForQueueRetry(CancellationToken token)
        {
            while (true)
            {
                var until = retryAfter > queueRetryAfter ? retryAfter : queueRetryAfter;
                var remaining = until - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero) return;
                await Task.Delay(remaining, token);
            }
        }
        async Task ProcessQueueNotifications()
        {
            while (await queueSignals.Reader.WaitToReadAsync(notificationsStop.Token))
            {
                while (queueSignals.Reader.TryRead(out _)) { }
                var stateChanged = queueStateChanged;
                queueStateChanged = false;
                await WaitForQueueRetry(notificationsStop.Token);
                if (!autoQueueActive || queueEngine == null) continue;
                try { await queueEngine.Tick(DateTimeOffset.UtcNow, notificationsStop.Token, stateChanged: stateChanged, enabled: () => autoQueueActive); }
                catch (ClientUnavailableException e) { SetQueueStatus(e.Message); QueueBackoff(ClientUnavailableIntervalMilliseconds); }
                catch (ApiException e)
                {
                    if (e.Status is 401 or 403)
                    {
                        Report("ログインを再接続中");
                        var refreshed = await RefreshAccountSession(notificationsStop.Token, forceBrowser: true);
                        QueueBackoff(refreshed ? TransientFailureIntervalMilliseconds : 30000);
                        continue;
                    }
                    var message = e.Status == 429 ? "混雑中 · 自動で再接続します" : "接続を再確認中";
                    SetQueueStatus(message);
                    if (e.Status == 429) Backoff(e.RetrySeconds * 1000);
                    else QueueBackoff(TransientFailureIntervalMilliseconds);
                }
                catch (OperationCanceledException) when (!notificationsStop.IsCancellationRequested) { SetQueueStatus("タイムアウト · 再接続中"); QueueBackoff(TransientFailureIntervalMilliseconds); }
                catch (HttpRequestException) { SetQueueStatus("ネットワークを再確認中"); QueueBackoff(TransientFailureIntervalMilliseconds); }
                catch (Exception e) when (e is JsonException or InvalidDataException or KeyNotFoundException)
                { SetQueueStatus("ゲームの応答を確認できません"); QueueBackoff(10000); }
            }
        }
        try
        {
            while (!ct.IsCancellationRequested && (pickMonitoring || autoQueueActive))
            {
                var interval = pickMonitoring ? FastPollIntervalMilliseconds : ClientUnavailableIntervalMilliseconds;
                try
                {
                    await WaitForRetry(ct);
                    if (DateTimeOffset.UtcNow >= api.ExpiresAt.AddMinutes(-3))
                    {
                        Report("ログインを更新中");
                        if (!await RefreshAccountSession(ct))
                        { Report("ログインを再接続中"); await Task.Delay(TimeSpan.FromSeconds(30), ct); continue; }
                        UpdateConnectionUi();
                    }
                    if (autoQueueActive && notifications == null)
                    {
                        notificationChecks = ProcessQueueNotifications();
                        notifications = RiotLocalEvents.Listen(api, QueueChanged, notificationsStop.Token);
                        if (relayOptions is { } options)
                        {
                            relayNotifications = NotificationRelayClient.Listen(options, () => api.Subject, QueueChanged,
                                _ => { }, notificationsStop.Token);
                        }
                    }
                    if (pickMonitoring)
                    {
                        if (engine == null || engineSubject != api.Subject) { engine = new PickEngine(api, ReportPick, api.IsCharacterSelectReady, UpdateMap, Edition.IsTrial ? AuthorizeTrialPick : null, Edition.IsTrial ? CheckTrialMatch : null, agent => ownedSubject == api.Subject && ownedAgents?.Contains(agent) == true); engineSubject = api.Subject; }
                        await engine.Tick(settings, DateTimeOffset.UtcNow, ct);
                    }
                    if (autoQueueActive && DateTimeOffset.UtcNow >= nextQueuePulse)
                    {
                        nextQueuePulse = DateTimeOffset.UtcNow.AddMilliseconds(ClientUnavailableIntervalMilliseconds);
                        queueSignals.Writer.TryWrite(false);
                    }
                    if (!pickMonitoring && autoQueueActive) await mapObserver.Tick(ct);
                }
                catch (ClientUnavailableException e) { Report(e.Message); if (autoQueueActive) SetQueueStatus(e.Message); interval = ClientUnavailableIntervalMilliseconds; }
                catch (ApiException e)
                {
                    if (e.Status is 401 or 403)
                    {
                        Report("ログインを再接続中");
                        var refreshed = await RefreshAccountSession(ct, forceBrowser: true);
                        await Task.Delay(refreshed ? TimeSpan.FromMilliseconds(TransientFailureIntervalMilliseconds) : TimeSpan.FromSeconds(30), ct);
                        continue;
                    }
                    var message = e.Status == 429 ? "混雑中 · 自動で再接続します" : e.Status == 404 ? "マッチを検索中" : "接続を再確認中";
                    Report(message);
                    if (autoQueueActive) SetQueueStatus(message);
                    interval = e.Status == 429 ? e.RetrySeconds * 1000 : TransientFailureIntervalMilliseconds;
                    if (e.Status == 429) Backoff(interval);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested) { Report("タイムアウト · 再接続中"); if (autoQueueActive) SetQueueStatus("タイムアウト · 再接続中"); interval = TransientFailureIntervalMilliseconds; }
                catch (HttpRequestException) { Report("ネットワークを再確認中"); if (autoQueueActive) SetQueueStatus("ネットワークを再確認中"); interval = TransientFailureIntervalMilliseconds; }
                catch (Exception e) when (e is JsonException or InvalidDataException or KeyNotFoundException)
                { Report("ゲームの応答を確認できません"); if (autoQueueActive) SetQueueStatus("ゲームの応答を確認できません"); interval = 10000; }
                await Task.Delay(interval, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch { Report("監視を停止しました · 再接続してお試しください"); }
        finally
        {
            notificationsStop.Cancel();
            if (notifications != null) { try { await notifications; } catch (Exception) { } }
            if (relayNotifications != null) { try { await relayNotifications; } catch (Exception) { } }
            if (notificationChecks != null) { try { await notificationChecks; } catch (Exception) { } }
            monitor?.Dispose(); monitor = null;
            monitorTask = null; queueEngine = null; pickMonitoring = false; autoQueueActive = false;
            SetInputEnabled(true); UpdateMap("");
            status.Text = "停止中"; status.ForeColor = Theme.Muted;
            statusLed?.Invalidate();
            UpdateConnectionUi();
        }
    }

    private Task StopMonitor(AutoQueueEngine? queueToCancel = null)
    {
        if (stopTask is { IsCompleted: false }) return stopTask;
        var activeQueue = queueToCancel ?? (autoQueueActive ? queueEngine : null);
        pickMonitoring = false; autoQueueActive = false;
        var source = monitor;
        if (source == null && activeQueue == null) { UpdateConnectionUi(); return Task.CompletedTask; }

        stopping = true;
        status.Text = "停止中"; status.ForeColor = Theme.Muted;
        Report("監視を停止しています");
        statusLed?.Invalidate();
        UpdateConnectionUi();
        try { source?.Cancel(); }
        catch (ObjectDisposedException) { }
        stopTask = CompleteStop(monitorTask, activeQueue);
        return stopTask;
    }

    private async Task CompleteStop(Task? running, AutoQueueEngine? activeQueue)
    {
        string? finalQueueStatus = null;
        try
        {
            if (running != null) await running;
            if (activeQueue != null)
            {
                await CancelQueueSearch(activeQueue);
                finalQueueStatus = lastQueueStatus;
            }
            else if (queueLeaveTask is { IsCompleted: false }) await queueLeaveTask;
        }
        finally
        {
            stopping = false;
            stopTask = null;
            UpdateConnectionUi();
            if (finalQueueStatus != null) SetQueueStatus(finalQueueStatus);
        }
        Report("監視を停止しました");
    }

    private Task CancelQueueSearch(AutoQueueEngine? activeQueue)
    {
        if (activeQueue == null) return Task.CompletedTask;
        if (queueLeaveTask is { IsCompleted: false }) return queueLeaveTask;
        queueLeaveTask = LeaveQueueSearch(activeQueue);
        return queueLeaveTask;
    }

    private async Task LeaveQueueSearch(AutoQueueEngine activeQueue)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            await activeQueue.LeaveActiveQueue(timeout.Token);
        }
        catch
        {
            SetQueueStatus("キューをキャンセルできません · VALORANT側で確認してください");
            Report("キューの停止を確認できませんでした");
        }
        finally { queueLeaveTask = null; }
    }

    private void StartAgentCatalogRefresh()
    {
        if (agentCatalogRefresh != null || closing || lifetime.IsCancellationRequested) return;
        agentCatalogRefresh = RefreshAgentCatalogAsync(lifetime.Token);
    }

    // Fetches the remote catalogue off the UI thread and merges it into the current one. Failures are
    // logged and otherwise silent: offline use is normal and must not disturb login or monitoring status.
    private async Task RefreshAgentCatalogAsync(CancellationToken ct)
    {
        List<Agent> remote;
        try
        {
            remote = await Task.Run(async () =>
            {
                using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(20), MaxResponseContentBufferSize = AgentCatalog.MaximumRemoteBytes };
                var json = await http.GetStringAsync(AgentCatalogUri, ct);
                return AgentCatalog.ParseRemote(json);
            }, ct);
        }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        catch (Exception e) when (e is HttpRequestException or OperationCanceledException or JsonException or InvalidDataException)
        { NativePageStyle.LogPageError("AgentCatalogRefresh", e); return; }
        if (closing || IsDisposed) return;

        var knownCache = cachedAgents;
        List<Agent> refreshedCache;
        try { refreshedCache = await Task.Run(() => AgentCatalog.UpdateCacheFile(AgentCachePath, knownCache, remote), ct); }
        catch (OperationCanceledException) when (ct.IsCancellationRequested) { return; }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The in-memory update still applies; the cache is retried on the next launch.
            NativePageStyle.LogPageError("AgentCatalogCache", e);
            refreshedCache = AgentCatalog.Merge([], knownCache, remote);
        }
        if (closing || IsDisposed) return;
        cachedAgents = refreshedCache;
        // Merge onto the live list so nothing currently shown or selected can disappear.
        ApplyAgentCatalog(AgentCatalog.Merge(embeddedAgents, agents, refreshedCache));
    }

    private void ApplyAgentCatalog(List<Agent> updated)
    {
        if (closing || IsDisposed || updated.Count == 0 || !AgentCatalog.ContainsAll(updated, agents)) return;
        if (updated.SequenceEqual(agents)) return;
        // Only display records change: selections are IDs and are re-resolved, never replaced or cleared.
        // Map choices and owned agents are ID sets and are left untouched; login controls are not involved.
        agents = updated;
        mainAgent = AgentCatalog.ResolveSelection(agents, mainAgent);
        // A running monitor captured its settings at start; resolving now would make the hero disagree with it.
        if (mainAgent == null && pendingAgentId.Length > 0 && monitor == null)
        {
            mainAgent = agents.FirstOrDefault(a => a.Id.Equals(pendingAgentId, StringComparison.OrdinalIgnoreCase));
            if (mainAgent != null) pendingAgentId = "";
        }
        SetHero();
        UpdateAgentAccessUi();
        matchHistory?.UpdateAgents(agents);
    }

    private void Report(string text)
    {
        if (IsDisposed || text == lastStatus) return;
        lastStatus = text; detail.Text = text; detail.Invalidate();
    }
    private void SetQueueStatus(string text)
    {
        if (IsDisposed || text == lastQueueStatus) return;
        lastQueueStatus = text; queueDetail.Text = text; queueDetail.Invalidate();
    }
    private static string LoginError(Exception e) => e switch
    {
        Microsoft.Web.WebView2.Core.WebView2RuntimeNotFoundException => "ログインにはMicrosoft Edge WebView2 Runtimeが必要です",
        BrowserIsolationException b => b.Message,
        ApiException a => $"接続できませんでした · {a.Status}",
        ClientUnavailableException c => c.Message,
        _ => "接続できませんでした · ネットワークをご確認ください"
    };
    private void Restore() { Show(); WindowState = FormWindowState.Normal; Activate(); }
    private static Label FieldLabel(string text) => new() { Text = text, Dock = DockStyle.Fill, Margin = new Padding(0, 1, 5, 0), ForeColor = Theme.Muted, TextAlign = ContentAlignment.MiddleLeft, Font = new Font("Yu Gothic UI", 10) };
    private static ThemedSelect RegionCombo() => new(["自動（おすすめ）", "アジア太平洋（東京など）", "北アメリカ", "ヨーロッパ", "韓国", "ブラジル", "ラテンアメリカ", "テスト環境（PBE）"])
    { AccessibleName = "アカウントの接続地域" };
}
