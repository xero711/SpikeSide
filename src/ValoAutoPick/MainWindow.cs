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
    private const int PregameRetryIntervalMilliseconds = 250;
    private const int ClientUnavailableIntervalMilliseconds = 1500;
    private const int TransientFailureIntervalMilliseconds = 2500;

    private static readonly string[] RegionValues = ["auto", "ap", "na", "eu", "kr", "br", "latam", "pbe"];
    private readonly ThemedSelect region = RegionCombo();
    private readonly SwitchToggle autoStart = new("自動で監視開始", false);
    private readonly ThemedButton login = new() { Text = "Riotにログイン", Size = new(176, UiMetrics.ButtonHeight), Compact = true };
    // Leave enough horizontal room for Japanese glyphs plus the button's text padding.
    private readonly ThemedButton logout = new() { Text = "ログアウト", Size = new(119, UiMetrics.ButtonHeight), Compact = true };
    // Page navigation uses the Tab style so the coral fill stays reserved for the main action of each page.
    private readonly ThemedButton dashboardTab = new() { Text = "ピック", Size = new(92, UiMetrics.ButtonHeight), Compact = true, Variant = ButtonVariant.Tab, Selected = true, AccessibleDescription = "Ctrl+1" };
    private readonly ThemedButton matchHistoryButton = new() { Text = "試合分析", Size = new(132, UiMetrics.ButtonHeight), Compact = true, Variant = ButtonVariant.Tab, AccessibleDescription = "Ctrl+2" };
    private readonly ThemedButton storeTab = new() { Text = "STORE", Size = new(104, UiMetrics.ButtonHeight), Compact = true, Variant = ButtonVariant.Tab, AccessibleDescription = "Ctrl+3" };
    private readonly ThemedButton liveTab = new() { Text = "ライブ", Size = new(100, UiMetrics.ButtonHeight), Compact = true, Variant = ButtonVariant.Tab, AccessibleDescription = "Ctrl+4" };
    private readonly ThemedButton appSettings = new() { Text = "設定", Size = new(84, UiMetrics.ButtonHeight), Compact = true };
    private readonly ToolTip shortcutTips = new() { InitialDelay = 600 };
    // Window state to return to when reopened from the tray (minimizing hides the window).
    private FormWindowState restoreState = FormWindowState.Normal;
    // MainWindow positions this page area below the header with ArrangeRoot.
    // Keeping Dock=Fill here makes WinForms cover the manually positioned header.
    private readonly Panel pageHost = new() { Dock = DockStyle.None, BackColor = Theme.Background };
    private readonly ThemedButton start = new() { Text = "監視を開始", Primary = true, Font = SharedFonts.Get("Yu Gothic UI", 15.5f, FontStyle.Bold) };
    private readonly ThemedButton autoQueue = new() { Text = "自動インキュー開始", Font = SharedFonts.Get("Yu Gothic UI", 13.5f, FontStyle.Bold), AccessibleDescription = "ゲーム内で選択中のモードに自動で参加します" };
    private readonly HeroPanel hero = new();
    private readonly RoundedCard settingsPanel = new() { Dock = DockStyle.Fill };
    private readonly RoundedCard mapSettingsCard = new() { Dock = DockStyle.Fill };
    private readonly MatchMapPanel matchMap = new() { Dock = DockStyle.Fill };
    private readonly Label mapSummary = new() { Dock = DockStyle.Fill, ForeColor = Theme.Muted, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };
    private readonly Label trialRemaining = new() { Dock = DockStyle.Fill, ForeColor = Theme.Text, Font = SharedFonts.Get("Yu Gothic UI", 11, FontStyle.Bold), TextAlign = ContentAlignment.MiddleLeft, Margin = Padding.Empty };
    private readonly Label trialDescription = new() { Dock = DockStyle.Fill, ForeColor = Theme.Text, Font = SharedFonts.Get("Yu Gothic UI", 9.5f), TextAlign = ContentAlignment.TopLeft, Margin = Padding.Empty, Text = "1試合のピック試行で1回消費します。\nマップ別設定・自動インキューは利用不可" };
    private readonly Label trialCorner = new() { ForeColor = Theme.Text, BackColor = Theme.Background, Font = SharedFonts.Get("Yu Gothic UI", 9, FontStyle.Bold), TextAlign = ContentAlignment.MiddleRight, AutoEllipsis = true, AccessibleName = "体験版の残り回数" };
    private readonly TrialUsage trialUsage = TrialUsage.CreateDefault();
    private string displayedMap = "";
    private readonly RoundedCard startPanel = new() { Dock = DockStyle.Fill };
    private readonly Label status = new() { Text = "停止中", ForeColor = Theme.Muted, Font = SharedFonts.Get("Yu Gothic UI", 8.2f, FontStyle.Bold), AutoSize = false, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill };
    private readonly Label detail = new() { Text = "", ForeColor = Theme.Muted, Font = SharedFonts.Get("Yu Gothic UI", 8.2f), AutoSize = false, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill };
    private readonly Label queueDetail = new() { Text = "自動インキューは停止中", ForeColor = Theme.Muted, Font = SharedFonts.Get("Yu Gothic UI", 8.2f), AutoSize = false, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill };
    private readonly Label accountInfo = new() { Text = "Riotアカウント: 未接続", ForeColor = Theme.Muted, Font = SharedFonts.Get("Yu Gothic UI", 9.5f), AutoSize = false, AutoEllipsis = true, TextAlign = ContentAlignment.MiddleLeft, Dock = DockStyle.Fill };
    private readonly Icon trayIcon = AppIcon.Create();
    private readonly NotifyIcon tray = new() { Text = Edition.DisplayName, Visible = true };
    private readonly TrayContextMenu trayMenu = new();
    private TrayStatusItem? trayStatus;
    private TrayToggleItem? trayPickToggle;
    private TrayToggleItem? trayQueueToggle;
    private string trayWarningText = "";
    private readonly RiotApi api = new();
    private readonly PickDiagnosticsLogger pickDiagnostics = new();
    private MatchHistoryControl? matchHistory;
    private StorePreviewControl? storePreview;
    private LiveMatchControl? liveMatch;
    private Control? dashboardPage;
    private MainPage selectedPage;
    private readonly CancellationTokenSource lifetime = new();
    private LoginWindow? loginWindow;
    private CancellationTokenSource? loginAttempt;
    private CancellationTokenSource? refreshStop;
    private Task<bool>? accountRefresh;
    private Task? ownedLoad;
    private Task? signInTask;
    private CancellationTokenSource? monitor;
    private Task? monitorTask;
    private Task? stopTask;
    private Task? queueLeaveTask;
    private PickEngine? engine;
    private AutoQueueEngine? queueEngine;
    private RelayOptions? relayOptions;
    private string engineSubject = "", lastStatus = "", lastQueueStatus = "";
    private bool closing, allowClose, busy, stopping, pickMonitoring, autoQueueActive, systemBrowserLogin, inputEnabled = true;
    private bool resourcesDisposed;
    private string themeName = "dark";
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
    private readonly Func<bool> valorantRunning;

    public MainWindow(string? smokePath = null, bool loginSmokeTest = false, string? previewAgentId = null, bool pickerSmokeTest = false, bool mapSmokeTest = false, bool skipBackgroundWork = false, Func<bool>? valorantRunning = null)
        : base(resizable: false)
    {
        this.smokePath = smokePath;
        this.previewAgentId = previewAgentId;
        this.valorantRunning = valorantRunning ?? RiotApi.IsValorantRunning;
        Text = Edition.DisplayName;
        ClientSize = new Size(1600, 860); MinimumSize = new Size(1200, 760); StartPosition = FormStartPosition.CenterScreen;
        BackColor = Theme.Background; ForeColor = Theme.Text; Font = SharedFonts.Get("Yu Gothic UI", 10.5f);
        tray.Icon = trayIcon;

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
        themeName = config.Theme;
        if (previewAgentId != null) mainAgent = agents.FirstOrDefault(a => a.Id.Equals(previewAgentId, StringComparison.OrdinalIgnoreCase));
        autoStart.Checked = config.StartOnLaunch;
        region.SelectedIndex = Math.Max(0, Array.IndexOf(RegionValues, config.Region));

        var root = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Background, AutoScroll = false };
        var header = BuildHeader(); header.Dock = DockStyle.None; header.TabIndex = 0;
        var dashboard = BuildDashboard(); dashboard.Dock = DockStyle.Fill; dashboard.TabIndex = 1;
        dashboardPage = dashboard;
        pageHost.Controls.Add(dashboard);
        var version = new Label { Text = "v" + (Assembly.GetExecutingAssembly().GetName().Version?.ToString(3) ?? ""), ForeColor = Theme.Muted, BackColor = Theme.Background, Font = SharedFonts.Get("Segoe UI", 8.5f), TextAlign = ContentAlignment.MiddleRight, AutoSize = false, TabStop = false, AccessibleName = "バージョン" };
        root.Controls.Add(pageHost); root.Controls.Add(header); root.Controls.Add(version);
        if (Edition.IsTrial) root.Controls.Add(trialCorner);
        Controls.Add(root);
        void ArrangeRoot()
        {
            var width = Math.Max(UiMetrics.Px(this, 850), root.ClientSize.Width - UiMetrics.Px(this, UiMetrics.PageInset * 2));
            var height = Math.Max(UiMetrics.Px(this, 640), root.ClientSize.Height - UiMetrics.Px(this, 87));
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
        liveTab.Click += (_, _) => ShowMainPage(MainPage.Live);
        appSettings.Click += (_, _) => ShowSettings();
        shortcutTips.SetToolTip(dashboardTab, "ピック (Ctrl+1)");
        shortcutTips.SetToolTip(matchHistoryButton, "試合分析 (Ctrl+2)");
        shortcutTips.SetToolTip(storeTab, "STORE (Ctrl+3)");
        shortcutTips.SetToolTip(liveTab, "ライブ (Ctrl+4)");
        start.Click += async (_, _) => await TogglePickMonitoring();
        autoQueue.Click += async (_, _) => await ToggleAutoQueue();
        region.SelectedIndexChanged += async (_, _) => { SaveSettings(); if (api.HasAccountSession) { await SignOut(clearBrowserSession: false); await RestoreAccountSession(ReadSettings()); } };
        autoStart.CheckedChanged += (_, _) => SaveSettings();
        Resize += (_, _) => { if (WindowState != FormWindowState.Minimized) restoreState = WindowState; };
        Resize += (_, _) => { if (WindowState == FormWindowState.Minimized) { Hide(); tray.ShowBalloonTip(1500, Text, monitor != null ? autoQueueActive ? pickMonitoring ? "自動ピック・インキューを監視中です" : "自動インキューを監視中です" : "自動ピックを監視中です" : "監視は停止中です", ToolTipIcon.Info); } };
        VisibleChanged += (_, _) => { if (Visible && monitor != null && System.Windows.SystemParameters.ClientAreaAnimation) ledPulseTimer.Start(); };
        tray.DoubleClick += (_, _) => Restore();
        var trayHeader = new TrayHeaderItem(Edition.IsTrial);
        trayStatus = new TrayStatusItem(
            "停止処理中", "監視は停止中", "自動ピック・インキューを監視中", "自動インキューを監視中", "自動ピックを監視中",
            "Riotにログインしてください", "VALORANTが起動していません", "VALORANTが終了したため停止しました", "エージェントを選んでください", "設定エージェントは未解放です", "所有情報を確認してください");
        trayPickToggle = new TrayToggleItem("自動ピック監視", async (_, _) => await TogglePickMonitoringFromTray());
        trayQueueToggle = new TrayToggleItem("自動インキュー", async (_, _) => await ToggleAutoQueueFromTray()) { Visible = !Edition.IsTrial };
        var trayOpen = new TrayActionItem("ウィンドウを開く", (_, _) => Restore());
        var trayExit = new TrayActionItem($"{Edition.DisplayName} を終了", (_, _) => Close(), destructive: true);
        trayMenu.Items.AddRange([trayHeader, trayStatus, new TraySeparator(), trayPickToggle, trayQueueToggle, new TraySeparator(), trayOpen, new TraySeparator(), trayExit, new TraySeparator(line: false, height: 4)]);
        trayMenu.Opening += (_, _) => SyncTrayMenu();
        trayMenu.Closed += (_, _) => trayWarningText = "";
        tray.ContextMenuStrip = trayMenu;
        SyncTrayMenu();
        UpdateConnectionUi();
        Load += (_, _) => { ActiveControl = login.Visible ? login : region; root.AutoScrollPosition = Point.Empty; };

        Shown += async (_, _) =>
        {
            if (smokePath != null)
            {
                if (Environment.GetCommandLineArgs().Contains("--preview-tray"))
                {
                    trayMenu.Show(PointToScreen(new Point(UiMetrics.Px(this, 30), UiMetrics.Px(this, 80))));
                    await Task.Delay(700);
                    using var shot = new Bitmap(trayMenu.Width, trayMenu.Height);
                    using (var graphics = Graphics.FromImage(shot)) graphics.CopyFromScreen(trayMenu.Location, Point.Empty, trayMenu.Size);
                    shot.Save(smokePath); trayMenu.Close(); Close(); return;
                }
                if (Environment.GetCommandLineArgs().Contains("--preview-settings"))
                {
                    using var dialog = new SettingsDialog(Theme.IsLight ? "light" : "dark", _ => true);
                    dialog.Shown += async (_, _) =>
                    {
                        await Task.Delay(700); dialog.TopMost = true; dialog.Activate();
                        using var shot = new Bitmap(dialog.Width, dialog.Height);
                        using (var graphics = Graphics.FromImage(shot)) graphics.CopyFromScreen(dialog.Location, Point.Empty, dialog.Size);
                        shot.Save(smokePath); dialog.Close();
                    };
                    dialog.ShowDialog(this); Close(); return;
                }
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
                if (Environment.GetCommandLineArgs().Contains("--preview-live"))
                {
                    ShowMainPage(MainPage.Live);
                    liveMatch!.ShowPreview();
                }
                else if (Environment.GetCommandLineArgs().Contains("--preview-match-history"))
                {
                    ShowMainPage(MainPage.MatchHistory);
                    await matchHistory!.ActivatePageAsync();
                    if (Environment.GetCommandLineArgs().Contains("--preview-career-tab"))
                        matchHistory!.ShowCareerForPreview();
                    if (Environment.GetCommandLineArgs().Contains("--preview-trend"))
                        matchHistory!.ShowTrendForPreview();
                    if (Environment.GetCommandLineArgs().Contains("--preview-history-next"))
                    {
                        await Task.Delay(700);
                        matchHistory!.ShowNextHistoryPageForPreview();
                        if (Environment.GetCommandLineArgs().Contains("--preview-history-last"))
                            for (var i = 0; i < 20; i++) matchHistory.ShowNextHistoryPageForPreview();
                    }
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
            else if (!skipBackgroundWork)
            {
                // Runs in the background; login restoration never waits for the catalogue.
                StartAgentCatalogRefresh();
                await RestoreAccountSession(config);
                _ = MaintainAccountSession();
            }
        };
        FormClosing += async (_, e) =>
        {
            if (allowClose) return;
            e.Cancel = true;
            if (closing) return;
            closing = true; trayExit.Enabled = false; lifetime.Cancel();
            try
            {
                await StopMonitor();
                foreach (var task in new Task?[] { signInTask, accountRefresh, ownedLoad, agentCatalogRefresh })
                    if (task != null) try { await task; } catch (Exception) { }
                if (matchHistory != null) await matchHistory.ShutdownAsync();
            }
            catch (Exception ex) { NativePageStyle.LogPageError("Shutdown", ex); }
            finally
            {
                ReleaseResources();
                allowClose = true;
                if (!IsDisposed && IsHandleCreated) BeginInvoke(Close);
            }
        };
    }

    private readonly System.Windows.Forms.Timer ledPulseTimer = new() { Interval = 60 };

    private void ReleaseResources()
    {
        if (resourcesDisposed) return;
        resourcesDisposed = true;
        lifetime.Cancel();
        matchHistory?.Dispose(); storePreview?.Dispose(); loginWindow?.Dispose(); api.Dispose();
        tray.ContextMenuStrip = null; trayMenu.Dispose(); tray.Dispose(); trayIcon.Dispose(); shortcutTips.Dispose();
        ledPulseTimer.Stop(); ledPulseTimer.Dispose(); lifetime.Dispose();
        foreach (var control in new Control[] { autoQueue, mapSettings, trialRemaining, trialDescription, trialCorner })
            if (control.Parent is null && !control.IsDisposed) control.Dispose();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing) { closing = true; ReleaseResources(); }
        base.Dispose(disposing);
    }

    private Control BuildHeader()
    {
        // The window caption already shows the app icon and name (and the edition), so the header only
        // carries page navigation on the left and the account actions on the right.
        var header = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Background, Margin = Padding.Empty };
        var navigation = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(0, UiMetrics.Px(this, UiMetrics.TightGap), 0, 0), Margin = Padding.Empty, AccessibleRole = AccessibleRole.PageTabList, AccessibleName = "ページ" };
        var loginBar = new FlowLayoutPanel { FlowDirection = FlowDirection.LeftToRight, WrapContents = false, AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, Padding = new Padding(0, UiMetrics.Px(this, UiMetrics.TightGap), 0, 0), Margin = Padding.Empty };
        dashboardTab.Margin = Padding.Empty;
        matchHistoryButton.Margin = new Padding(UiMetrics.Px(this, UiMetrics.ControlGap), 0, 0, 0);
        storeTab.Margin = new Padding(UiMetrics.Px(this, UiMetrics.ControlGap), 0, 0, 0);
        liveTab.Margin = new Padding(UiMetrics.Px(this, UiMetrics.ControlGap), 0, 0, 0);
        logout.Margin = new Padding(UiMetrics.Px(this, UiMetrics.ControlGap), 0, 0, 0); login.Margin = Padding.Empty;
        navigation.Controls.Add(dashboardTab); navigation.Controls.Add(matchHistoryButton); navigation.Controls.Add(storeTab);
        navigation.Controls.Add(liveTab);
        appSettings.Margin = new Padding(UiMetrics.Px(this, UiMetrics.ControlGap), 0, 0, 0);
        loginBar.Controls.Add(login); loginBar.Controls.Add(logout); loginBar.Controls.Add(appSettings);
        void PlaceActions()
        {
            navigation.Location = new Point(0, Math.Max(0, (header.ClientSize.Height - navigation.Height) / 2));
            loginBar.Location = new Point(Math.Max(navigation.Right + UiMetrics.Px(header, UiMetrics.CardInset), header.ClientSize.Width - loginBar.Width),
                Math.Max(0, (header.ClientSize.Height - loginBar.Height) / 2));
        }
        header.Controls.Add(navigation);
        header.Controls.Add(loginBar);
        header.Resize += (_, _) => PlaceActions();
        navigation.SizeChanged += (_, _) => PlaceActions();
        loginBar.SizeChanged += (_, _) => PlaceActions();
        PlaceActions();
        return header;
    }

    private Control BuildDashboard()
    {
        var columns = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Theme.Background, ColumnCount = 2, RowCount = 2, Margin = Padding.Empty, Padding = new Padding(0, UiMetrics.Px(this, UiMetrics.ControlGap), 0, UiMetrics.Px(this, UiMetrics.ControlGap)) };
        columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 58)); columns.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 42));
        columns.RowStyles.Add(new RowStyle(SizeType.Percent, 50)); columns.RowStyles.Add(new RowStyle(SizeType.Percent, 50));
        matchMap.Margin = new Padding(0, 0, UiMetrics.Px(this, UiMetrics.CardInset), UiMetrics.Px(this, UiMetrics.ControlGap));
        hero.Dock = DockStyle.Fill; hero.Margin = new Padding(0, UiMetrics.Px(this, UiMetrics.ControlGap), UiMetrics.Px(this, UiMetrics.CardInset), 0);
        var sidebar = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Theme.Background, ColumnCount = 1, RowCount = 2, Margin = new Padding(0, 0, 0, UiMetrics.Px(this, UiMetrics.ControlGap)) };
        sidebar.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        sidebar.RowStyles.Add(new RowStyle(SizeType.Percent, 55));
        sidebar.RowStyles.Add(new RowStyle(SizeType.Percent, 45));
        settingsPanel.Margin = new Padding(0, 0, 0, UiMetrics.Px(this, UiMetrics.ControlGap)); mapSettingsCard.Margin = Padding.Empty;
        startPanel.Margin = new Padding(0, UiMetrics.Px(this, UiMetrics.ControlGap), 0, 0);
        sidebar.Controls.Add(settingsPanel, 0, 0); sidebar.Controls.Add(mapSettingsCard, 0, 1);
        columns.Controls.Add(matchMap, 0, 0); columns.Controls.Add(hero, 0, 1);
        columns.Controls.Add(sidebar, 1, 0); columns.Controls.Add(startPanel, 1, 1);

        var connection = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Theme.Surface, Padding = new Padding(9, 4, 9, 4), RowCount = 4, ColumnCount = 1, Margin = Padding.Empty };
        connection.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        connection.RowStyles.Add(new RowStyle(SizeType.Absolute, 28)); connection.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        connection.RowStyles.Add(new RowStyle(SizeType.Absolute, 44)); connection.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        connection.Controls.Add(SectionTitle("接続設定"), 0, 0);
        accountInfo.Font = SharedFonts.Get("Yu Gothic UI", 9.5f, FontStyle.Bold); connection.Controls.Add(accountInfo, 0, 1);
        var regionRow = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Theme.Surface, ColumnCount = 2, RowCount = 1, Margin = Padding.Empty };
        regionRow.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        regionRow.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 94)); regionRow.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        regionRow.Controls.Add(FieldLabel("接続地域"), 0, 0); region.Dock = DockStyle.Fill; region.Margin = Padding.Empty; regionRow.Controls.Add(region, 1, 0);
        connection.Controls.Add(regionRow, 0, 2);
        autoStart.Margin = new Padding(0, 4, 0, 0); autoStart.Dock = DockStyle.Fill; connection.Controls.Add(autoStart, 0, 3);
        settingsPanel.Controls.Add(connection);

        var maps = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Theme.Surface, Padding = new Padding(9, 5, 9, 6), ColumnCount = 1, RowCount = 3, Margin = Padding.Empty };
        maps.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        maps.RowStyles.Add(new RowStyle(SizeType.Absolute, 26)); maps.RowStyles.Add(new RowStyle(SizeType.Absolute, 24)); maps.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        if (Edition.IsTrial)
        {
            maps.Controls.Add(SectionTitle("体験版"), 0, 0);
            maps.Controls.Add(trialRemaining, 0, 1);
            maps.Controls.Add(trialDescription, 0, 2);
        }
        else
        {
            maps.Controls.Add(SectionTitle("マップ別設定"), 0, 0);
            mapSummary.Font = SharedFonts.Get("Yu Gothic UI", 9.5f); mapSummary.Margin = Padding.Empty; maps.Controls.Add(mapSummary, 0, 1);
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
        typeof(Panel).GetProperty("DoubleBuffered", BindingFlags.Instance | BindingFlags.NonPublic)?.SetValue(led, true);
        ledPulseTimer.Tick += (_, _) =>
        {
            if (monitor == null || !Visible || WindowState == FormWindowState.Minimized || !System.Windows.SystemParameters.ClientAreaAnimation)
            {
                ledPulseTimer.Stop();
                led.Invalidate();
                return;
            }
            led.Invalidate();
        };
        led.Paint += (_, e) =>
        {
            e.Graphics.SmoothingMode = SmoothingMode.AntiAlias;
            var active = monitor != null;
            var color = active ? Theme.Green : Theme.Muted;
            var diameter = UiMetrics.Px(led, 9);
            var cx = UiMetrics.Px(led, 3) + diameter / 2f;
            var cy = led.Height / 2f;
            if (active && System.Windows.SystemParameters.ClientAreaAnimation)
            {
                var wave = (float)(0.5 + 0.5 * Math.Sin(Environment.TickCount64 / 320.0));
                var haloDiameter = diameter + UiMetrics.Px(led, 5) * wave;
                var alpha = (int)Math.Round(35 + 55 * (1f - wave));
                using var halo = new SolidBrush(Color.FromArgb(alpha, Theme.Green));
                e.Graphics.FillEllipse(halo, cx - haloDiameter / 2f, cy - haloDiameter / 2f, haloDiameter, haloDiameter);
            }
            using var brush = new SolidBrush(color);
            e.Graphics.FillEllipse(brush, cx - diameter / 2f, cy - diameter / 2f, diameter, diameter);
        };
        status.Font = SharedFonts.Get("Yu Gothic UI", 10.5f, FontStyle.Bold); status.Margin = Padding.Empty;
        detail.Font = SharedFonts.Get("Yu Gothic UI", 10); detail.Margin = Padding.Empty;
        queueDetail.Font = SharedFonts.Get("Yu Gothic UI", 9); queueDetail.Margin = Padding.Empty;
        statusRow.Controls.Add(led, 0, 0); statusRow.Controls.Add(status, 1, 0);
        action.Controls.Add(statusRow, 0, 4); action.Controls.Add(detail, 0, 5); if (!Edition.IsTrial) action.Controls.Add(queueDetail, 0, 6);
        startPanel.Controls.Add(action); return columns;
    }

    private static Label SectionTitle(string text) => new() { Text = text, ForeColor = Theme.Text, Font = SharedFonts.Get("Yu Gothic UI", 12.5f, FontStyle.Bold), Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleLeft, AutoEllipsis = true };

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
        mapAgents = dialog.MapAgents; UpdateMapSummary(); UpdateMap(displayedMap); SaveSettings(); UpdateConnectionUi();
    }

    private Settings ReadSettings() => new Settings
    {
        AgentId = mainAgent?.Id ?? "",
        MapAgents = new(mapAgents, StringComparer.OrdinalIgnoreCase),
        StartOnLaunch = autoStart.Checked,
        Region = RegionValues[Math.Clamp(region.SelectedIndex, 0, RegionValues.Length - 1)],
        SystemBrowserLogin = systemBrowserLogin,
        Theme = themeName
    }.Normalize();

    private void ShowSettings()
    {
        using var dialog = new SettingsDialog(themeName, SetTheme);
        dialog.ShowDialog(this);
    }

    private bool SetTheme(string value)
    {
        if (themeName == value) return true;
        var previous = themeName;
        themeName = value;
        if (!SaveSettings()) { themeName = previous; return false; }
        Theme.Apply(Application.OpenForms.Cast<Control>().ToArray(), value);
        return true;
    }

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
        if (closing || IsDisposed || Disposing) return;
        UpdateAgentAccessUi();
        ApplyAccountLabels(api.HasAccountSession, api.AccountName);
        // One accent-filled action at a time: sign in first, then (after an agent is set) start monitoring.
        login.Primary = selectedPage == MainPage.Dashboard && !api.HasAccountSession && loginAttempt == null;
        login.Visible = true;
        login.Enabled = !closing && (loginAttempt != null || !busy && monitor == null && !api.HasAccountSession);
        dashboardTab.Enabled = !closing;
        matchHistoryButton.Enabled = !closing && !busy;
        storeTab.Enabled = !closing && !busy;
        liveTab.Enabled = !closing && !busy;
        logout.Visible = api.HasAccountSession;
        login.AccessibleName = api.HasAccountSession ? $"接続済み: {api.AccountName} / {api.RegionLabel}" : loginAttempt != null ? "ログインを中止" : "Riotアカウントにログイン";
        start.Text = stopping ? "停止中…" : pickMonitoring ? "監視を停止" : "監視を開始";
        start.Enabled = !busy && !closing && !stopping;
        start.Primary = pickMonitoring || api.HasAccountSession && (mainAgent != null || mapAgents.Count > 0);
        autoQueue.Text = stopping ? "停止中…" : autoQueueActive ? "自動インキュー停止" : "自動インキュー開始";
        autoQueue.Primary = autoQueueActive;
        autoQueue.Enabled = !busy && !closing && !stopping && queueLeaveTask is not { IsCompleted: false };
        queueDetail.ForeColor = autoQueueActive ? Theme.Green : Theme.Muted;
        if (!autoQueueActive) SetQueueStatus("自動インキューは停止中");
        start.Invalidate();
        autoQueue.Invalidate();
        SyncTrayMenu();
    }

    private void SyncTrayMenu()
    {
        if (closing || IsDisposed || Disposing || trayStatus == null || trayPickToggle == null || trayQueueToggle == null) return;
        var canToggle = !busy && !closing && !stopping;
        trayPickToggle.IsOn = pickMonitoring && !stopping;
        trayPickToggle.Enabled = canToggle;
        trayQueueToggle.IsOn = autoQueueActive && !stopping;
        trayQueueToggle.Enabled = canToggle && !Edition.IsTrial && queueLeaveTask is not { IsCompleted: false };
        if (trayWarningText.Length > 0 && monitor == null && !stopping)
        {
            trayStatus.Text = trayWarningText;
            trayStatus.Active = false;
            trayStatus.Warning = true;
        }
        else
        {
            trayStatus.Text = stopping ? "停止処理中" : monitor == null ? "監視は停止中" : pickMonitoring && autoQueueActive ? "自動ピック・インキューを監視中" : autoQueueActive ? "自動インキューを監視中" : "自動ピックを監視中";
            trayStatus.Active = monitor != null && !stopping;
            trayStatus.Warning = false;
        }
        TrayMenuRenderer.ApplyMetrics(trayMenu);
        trayMenu.Invalidate();
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
        using var redraw = PageRedrawScope.Begin(pageHost);
        selectedPage = page;
        var next = page switch
        {
            MainPage.MatchHistory => GetMatchHistory(),
            MainPage.Store => GetStorePreview(),
            MainPage.Live => GetLiveMatch(),
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
        finally { pageHost.ResumeLayout(true); }
        next.PerformLayout();
        if (next is StorePreviewControl storePage) storePage.PreparePageLayout();
        Text = page switch
        {
            MainPage.MatchHistory => "試合分析",
            MainPage.Store => "STORE確認",
            MainPage.Live => "ライブトラッカー",
            _ => Edition.IsTrial ? "SpikeSide 体験版" : "SpikeSide"
        };
        dashboardTab.Selected = page == MainPage.Dashboard;
        matchHistoryButton.Selected = page == MainPage.MatchHistory;
        storeTab.Selected = page == MainPage.Store;
        liveTab.Selected = page == MainPage.Live;
        UpdateConnectionUi();
        if (page != MainPage.Dashboard)
            _ = ActivatePageSafelyAsync(page);
    }

    private MatchHistoryControl GetMatchHistory()
    {
        if (matchHistory != null) return matchHistory;
        var page = new MatchHistoryControl(api, agents) { Visible = false, Dock = DockStyle.Fill, TabIndex = 2 };
        page.LoginRequested += async (_, _) => await SignInFromPage();
        pageHost.Controls.Add(page);
        return matchHistory = page;
    }

    private LiveMatchControl GetLiveMatch()
    {
        if (liveMatch != null) return liveMatch;
        var page = new LiveMatchControl(api, agents) { Visible = false, Dock = DockStyle.Fill, TabIndex = 4 };
        pageHost.Controls.Add(page);
        return liveMatch = page;
    }

    private void ScheduleMatchHistorySync()
    {
        var subject = api.Subject;
        _ = SyncAfterMatchAsync(subject);
    }

    private async Task SyncAfterMatchAsync(string subject)
    {
        try
        {
            await Task.Delay(TimeSpan.FromSeconds(8), lifetime.Token);
            if (closing || !api.HasAccountSession || !string.Equals(subject, api.Subject, StringComparison.OrdinalIgnoreCase)) return;
            await GetMatchHistory().SyncAfterMatchAsync(subject);
        }
        catch (Exception) { /* History sync must not interrupt monitoring. */ }
    }

    private StorePreviewControl GetStorePreview()
    {
        if (storePreview != null) return storePreview;
        var page = new StorePreviewControl(api) { Visible = false, Dock = DockStyle.Fill, TabIndex = 3 };
        page.WishlistArrival += message => { if (!closing) tray.ShowBalloonTip(5000, "ウィッシュリストに入荷", message, ToolTipIcon.Info); };
        page.LoginRequested += async (_, _) => await SignInFromPage();
        page.LogoutRequested += async (_, _) => await SignOut();
        pageHost.Controls.Add(page);
        return storePreview = page;
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

    private enum MainPage { Dashboard, MatchHistory, Store, Live }

    private async Task NotifyEmbeddedPagesAsync()
    {
        var history = selectedPage == MainPage.MatchHistory && matchHistory != null
            ? ActivatePageSafelyAsync(MainPage.MatchHistory) : Task.CompletedTask;
        await Task.WhenAll(
            history,
            storePreview?.NotifyAccountChangedAsync() ?? Task.CompletedTask,
            liveMatch?.NotifyAccountChangedAsync() ?? Task.CompletedTask);
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

    // Login buttons inside the embedded pages only ever start a login. Unlike the header button they never
    // cancel one in progress (their label does not change to "中止"), and they do nothing once connected.
    private Task SignInFromPage()
    {
        if (closing || busy || loginAttempt != null || api.HasAccountSession) return Task.CompletedTask;
        return SignIn();
    }

    private Task SignIn()
    {
        if (loginAttempt != null) { loginAttempt.Cancel(); return Task.CompletedTask; }
        if (busy || closing || IsDisposed) return Task.CompletedTask;
        signInTask = SignInCore();
        return signInTask;
    }

    private async Task SignInCore()
    {
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
            var result = await new SystemBrowserAuth(AuthFlow.NewState()).Run((url, _) => browser.Open(url, false, attempt.Token), attempt.Token);
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
        finally { loginAttempt = null; busy = false; if (!closing && !IsDisposed) { SetInputEnabled(true); UpdateConnectionUi(); } }
        if (closing || IsDisposed) return;
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
                var wasConnected = api.HasAccountSession;
                if (await RefreshAccountSession(lifetime.Token))
                {
                    UpdateConnectionUi();
                    if (monitor == null) Report(ownedAgents == null ? "接続済み · エージェントの所有情報を取得できません" : "アカウントを接続しました");
                    if (!wasConnected && autoStart.Checked && monitor == null) { pickMonitoring = true; StartMonitor(); }
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
                .Run((url, _) => browser.Open(url, true, ct), ct);
            if (result == null) return false;
            await api.Login(result, settings.Region, ct, api.HasAccountSession ? api.Subject : null);
            if (ownedSubject != api.Subject) { ownedAgents = null; ownedSubject = ""; }
            await LoadOwnedAgents(ct);
            UpdateConnectionUi();
            return true;
        }
        catch (OperationCanceledException) when (!lifetime.IsCancellationRequested) { return false; }
        catch (Exception e) when (e is SocketException or BrowserIsolationException or IOException or UnauthorizedAccessException or
            System.ComponentModel.Win32Exception or HttpRequestException or ApiException or ClientUnavailableException or InvalidOperationException or JsonException or InvalidDataException or AggregateException)
        { return false; }
    }

    private async Task SignOut(bool clearBrowserSession = true)
    {
        if (busy) return;
        busy = true; trayWarningText = ""; SetInputEnabled(false); UpdateConnectionUi();
        try
        {
            matchHistory?.ResetSession();
            storePreview?.ResetSession();
            liveMatch?.ResetSession();
            ShowMainPage(MainPage.Dashboard);
            refreshStop?.Cancel();
            await StopMonitor();
            if (accountRefresh != null) { try { await accountRefresh; } catch (OperationCanceledException) { } }
            api.Logout(); ownedAgents = null; ownedSubject = ""; engine = null; engineSubject = "";
            if (storePreview != null) await storePreview.NotifyAccountChangedAsync();
            if (liveMatch != null) await liveMatch.NotifyAccountChangedAsync();
            if (clearBrowserSession)
            {
                systemBrowserLogin = false; SaveSettings();
                PersistentBrowser.Clear();
                if (loginWindow != null) await loginWindow.ClearSession();
            }
        }
        catch { Report("ログイン情報を消去できませんでした · アプリを終了してください"); }
        finally { busy = false; if (!closing && !IsDisposed) { SetInputEnabled(true); UpdateConnectionUi(); } }
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
        autoQueue.Enabled = !busy && !closing && !stopping && queueLeaveTask is not { IsCompleted: false };
    }

    private async Task TogglePickMonitoring()
    {
        trayWarningText = "";
        if (pickMonitoring)
        {
            pickMonitoring = false;
            if (!autoQueueActive) await StopMonitor();
            else
            {
                UpdateConnectionUi();
                Report("自動ピック監視を停止しました");
            }
            return;
        }
        if (!ValidatePickMonitoring(ReadSettings())) { UpdateConnectionUi(); return; }
        pickMonitoring = true;
        if (monitor == null) StartMonitor();
        if (monitor == null) pickMonitoring = false;
        UpdateConnectionUi();
    }

    private async Task TogglePickMonitoringFromTray()
    {
        var wasMonitoring = pickMonitoring;
        await TogglePickMonitoring();
        if (!wasMonitoring && !pickMonitoring)
        {
            trayWarningText = ResolveTrayWarning();
            SyncTrayMenu();
        }
    }

    private async Task ToggleAutoQueueFromTray()
    {
        var wasActive = autoQueueActive;
        trayWarningText = "";
        await ToggleAutoQueue();
        if (!wasActive && !autoQueueActive)
        {
            trayWarningText = ResolveTrayWarning();
            SyncTrayMenu();
        }
    }

    private string ResolveTrayWarning()
    {
        if (!api.HasAccountSession) return "Riotにログインしてください";
        if (!valorantRunning()) return "VALORANTが起動していません";
        if (ownedAgents == null || ownedSubject != api.Subject) return "所有情報を確認してください";
        if (mainAgent == null && mapAgents.Count == 0) return "エージェントを選んでください";
        var settings = ReadSettings();
        if (settings.AgentId.Length > 0 && !ownedAgents.Contains(settings.AgentId)) return "設定エージェントは未解放です";
        return "監視を開始できません";
    }

    private bool ValidatePickMonitoring(Settings settings)
    {
        if (!api.HasAccountSession) { Report("Riotアカウントにログインしてください"); return false; }
        if (ownedAgents == null || ownedSubject != api.Subject) { Report("エージェントの所有情報を確認してください"); return false; }
        if (mainAgent == null && mapAgents.Count == 0) { Report("エージェントを選んでください"); return false; }
        if (settings.AgentId.Length > 0 && !ownedAgents.Contains(settings.AgentId))
        {
            Report("設定したエージェントは未解放です · 別のエージェントを選んでください");
            return false;
        }
        return true;
    }

    private async Task ToggleAutoQueue()
    {
        if (Edition.IsTrial) return;
        trayWarningText = "";
        if (autoQueueActive)
        {
            var activeQueue = queueEngine;
            autoQueueActive = false;
            if (!pickMonitoring) await StopMonitor(activeQueue);
            else
            {
                UpdateConnectionUi();
                SetQueueStatus("キュー検索をキャンセル中");
                var leaving = CancelQueueSearch(activeQueue);
                UpdateConnectionUi();
                await leaving;
                UpdateConnectionUi();
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
        trayWarningText = "";
        if (monitor != null || !pickMonitoring && !autoQueueActive) return;
        if (busy || stopping || closing)
        {
            pickMonitoring = autoQueueActive = false;
            UpdateConnectionUi();
            return;
        }
        if (!valorantRunning())
        {
            pickMonitoring = false;
            autoQueueActive = false;
            Report("ゲームが起動されていないので監視を実行できません");
            UpdateConnectionUi();
            return;
        }
        var settings = ReadSettings();
        if (!api.HasAccountSession) { pickMonitoring = autoQueueActive = false; Report("Riotアカウントにログインしてください"); UpdateConnectionUi(); return; }
        if (pickMonitoring && !ValidatePickMonitoring(settings)) { pickMonitoring = autoQueueActive = false; UpdateConnectionUi(); return; }
        if (!SaveSettings()) { pickMonitoring = autoQueueActive = false; UpdateConnectionUi(); return; }
        monitor = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        queueEngine = new AutoQueueEngine(api, SetQueueStatus);
        SetInputEnabled(false);
        status.Text = "監視中"; status.ForeColor = Theme.Green;
        if (System.Windows.SystemParameters.ClientAreaAnimation) ledPulseTimer.Start();
        statusLed?.Invalidate();
        monitorTask = RunMonitor(settings, monitor.Token);
        UpdateConnectionUi();
    }

    private async Task RunMonitor(Settings settings, CancellationToken ct)
    {
        Report("マッチを検索中");
        var mapObserver = new MapObserver(api, UpdateMap, ScheduleMatchHistorySync);
        using var notificationsStop = CancellationTokenSource.CreateLinkedTokenSource(ct);
        using var pickWake = new SemaphoreSlim(0, 1);
        var queueSignals = Channel.CreateBounded<bool>(new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropWrite });
        Task? notifications = null;
        Task? relayNotifications = null;
        Task? notificationChecks = null;
        bool GameStillRunning()
        {
            if (valorantRunning()) return true;
            pickMonitoring = false; autoQueueActive = false;
            trayWarningText = "VALORANTが終了したため停止しました";
            Report("VALORANTが終了したため監視を停止しました");
            return false;
        }
        async Task<bool> WaitWhileGameRunning(TimeSpan duration)
        {
            var until = DateTimeOffset.UtcNow + duration;
            do
            {
                if (!GameStillRunning()) return false;
                var remaining = until - DateTimeOffset.UtcNow;
                if (remaining <= TimeSpan.Zero) return true;
                await Task.Delay(remaining < TimeSpan.FromSeconds(1) ? remaining : TimeSpan.FromSeconds(1), ct);
            } while (true);
        }
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
        void WakePickMonitor()
        {
            if (!pickMonitoring) return;
            try { if (pickWake.CurrentCount == 0) pickWake.Release(); }
            catch (Exception e) when (e is SemaphoreFullException or ObjectDisposedException) { }
        }
        void QueueChanged()
        {
            if (!autoQueueActive) return;
            queueStateChanged = true;
            queueSignals.Writer.TryWrite(true);
        }
        void OnLocalEvent(RiotLocalEvents.Notification notification)
        {
            if (notification == RiotLocalEvents.Notification.PregameChanged)
            {
                WakePickMonitor();
                QueueChanged();
                return;
            }
            if (notification == RiotLocalEvents.Notification.QueueChanged) QueueChanged();
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
                if (!autoQueueActive || queueEngine == null || !valorantRunning()) continue;
                try { await queueEngine.Tick(DateTimeOffset.UtcNow, notificationsStop.Token, stateChanged: stateChanged, enabled: () => autoQueueActive && valorantRunning()); }
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
                var interval = pickMonitoring ? (engine?.SuggestedPollIntervalMs ?? FastPollIntervalMilliseconds) : ClientUnavailableIntervalMilliseconds;
                try
                {
                    await WaitForRetry(ct);
                    if (!GameStillRunning()) break;
                    // Proactively refresh tokens while idle in lobby so a slow auth refresh never collides with character select.
                    var inPregame = engine?.IsPregameActive == true;
                    var refreshThreshold = engine?.IsInMatch == true ? TimeSpan.FromMinutes(2) : TimeSpan.FromMinutes(12);
                    var mustRefreshNow = !inPregame
                        ? DateTimeOffset.UtcNow >= api.ExpiresAt - refreshThreshold
                        : DateTimeOffset.UtcNow >= api.ExpiresAt.AddSeconds(-30);
                    if (mustRefreshNow)
                    {
                        Report("ログインを更新中");
                        if (!await RefreshAccountSession(ct))
                        { Report("ログインを再接続中"); if (!await WaitWhileGameRunning(TimeSpan.FromSeconds(30))) break; continue; }
                        UpdateConnectionUi();
                    }
                    if ((pickMonitoring || autoQueueActive) && notifications == null)
                    {
                        notifications = RiotLocalEvents.Listen(api, OnLocalEvent, api.SetLocalEventConnected, notificationsStop.Token);
                    }
                    if (autoQueueActive && notificationChecks == null)
                    {
                        notificationChecks = ProcessQueueNotifications();
                        if (relayOptions is { } options)
                        {
                            relayNotifications = NotificationRelayClient.Listen(options, () => api.Subject, () => { WakePickMonitor(); QueueChanged(); },
                                _ => { }, notificationsStop.Token);
                        }
                    }
                    if (pickMonitoring)
                    {
                        if (engine == null || engineSubject != api.Subject)
                        {
                            engine = new PickEngine(api, ReportPick, api.IsCharacterSelectReady, UpdateMap,
                                Edition.IsTrial ? AuthorizeTrialPick : null, Edition.IsTrial ? CheckTrialMatch : null,
                                agent => ownedSubject == api.Subject && ownedAgents?.Contains(agent) == true,
                                ScheduleMatchHistorySync, pickDiagnostics.Record);
                            engineSubject = api.Subject;
                        }
                        await engine.Tick(settings, DateTimeOffset.UtcNow, ct);
                        interval = engine.SuggestedPollIntervalMs;
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
                        if (!await WaitWhileGameRunning(refreshed ? TimeSpan.FromMilliseconds(TransientFailureIntervalMilliseconds) : TimeSpan.FromSeconds(30))) break;
                        continue;
                    }
                    var message = e.Status == 429 ? "混雑中 · 自動で再接続します" : e.Status == 404 ? "マッチを検索中" : "接続を再確認中";
                    Report(message);
                    if (autoQueueActive) SetQueueStatus(message);
                    interval = e.Status == 429 ? e.RetrySeconds * 1000 : engine?.IsPregameActive == true ? PregameRetryIntervalMilliseconds : TransientFailureIntervalMilliseconds;
                    if (e.Status == 429) Backoff(interval);
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    Report("タイムアウト · 再接続中"); if (autoQueueActive) SetQueueStatus("タイムアウト · 再接続中");
                    interval = engine?.IsPregameActive == true ? PregameRetryIntervalMilliseconds : TransientFailureIntervalMilliseconds;
                }
                catch (HttpRequestException)
                {
                    Report("ネットワークを再確認中"); if (autoQueueActive) SetQueueStatus("ネットワークを再確認中");
                    interval = engine?.IsPregameActive == true ? PregameRetryIntervalMilliseconds : TransientFailureIntervalMilliseconds;
                }
                catch (Exception e) when (e is JsonException or InvalidDataException or KeyNotFoundException)
                { Report("ゲームの応答を確認できません"); if (autoQueueActive) SetQueueStatus("ゲームの応答を確認できません"); interval = 10000; }
                if (pickMonitoring) await pickWake.WaitAsync(interval, ct);
                else await Task.Delay(interval, ct);
            }
        }
        catch (OperationCanceledException) { }
        catch
        {
            if (!valorantRunning())
            {
                pickMonitoring = false;
                autoQueueActive = false;
                trayWarningText = "VALORANTが終了したため停止しました";
                Report("VALORANTが終了したため監視を停止しました");
            }
            else Report("監視を停止しました · 再接続してお試しください");
        }
        finally
        {
            notificationsStop.Cancel();
            api.SetLocalEventConnected("", false);
            if (notifications != null) { try { await notifications; } catch (Exception) { } }
            if (relayNotifications != null) { try { await relayNotifications; } catch (Exception) { } }
            if (notificationChecks != null) { try { await notificationChecks; } catch (Exception) { } }
            monitor?.Dispose(); monitor = null;
            monitorTask = null; queueEngine = null; pickMonitoring = false; autoQueueActive = false;
            ResolvePendingAgent();
            if (!busy && !closing) SetInputEnabled(true);
            UpdateMap("");
            status.Text = "停止中"; status.ForeColor = Theme.Muted;
            ledPulseTimer.Stop();
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
        if (source == null && activeQueue == null && queueLeaveTask is not { IsCompleted: false }) { UpdateConnectionUi(); return Task.CompletedTask; }

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
        ResolvePendingAgent();
        SetHero();
        UpdateAgentAccessUi();
        matchHistory?.UpdateAgents(agents);
        liveMatch?.UpdateAgents(agents);
    }

    private void ResolvePendingAgent()
    {
        if (monitor != null || mainAgent != null || pendingAgentId.Length == 0) return;
        mainAgent = agents.FirstOrDefault(a => a.Id.Equals(pendingAgentId, StringComparison.OrdinalIgnoreCase));
        if (mainAgent is null) return;
        pendingAgentId = "";
        SetHero();
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
    private void Restore()
    {
        WindowState = restoreState;
        Show();
        if (monitor != null && System.Windows.SystemParameters.ClientAreaAnimation) ledPulseTimer.Start();
        // A modal child disables this window. Restoring only the owner can leave
        // the actual dialog behind it (or minimized), making the app appear stuck.
        Form target = this;
        while (target.OwnedForms.LastOrDefault(form => form.Modal && !form.IsDisposed) is { } modal)
            target = modal;
        if (target.WindowState == FormWindowState.Minimized) target.WindowState = FormWindowState.Normal;
        if (!target.Visible) target.Show();
        target.BringToFront();
        target.Activate();
    }

    protected override bool ProcessCmdKey(ref Message msg, Keys keyData)
    {
        // Page shortcuts work from anywhere in the window; disabled tabs keep their usual restrictions.
        ThemedButton? target = null;
        if ((keyData & Keys.Modifiers) == Keys.Control)
        {
            var key = keyData & Keys.KeyCode;
            if (key is Keys.D1 or Keys.NumPad1) target = dashboardTab;
            else if (key is Keys.D2 or Keys.NumPad2) target = matchHistoryButton;
            else if (key is Keys.D3 or Keys.NumPad3) target = storeTab;
            else if (key is Keys.D4 or Keys.NumPad4) target = liveTab;
        }
        if (target != null)
        {
            if (target.Enabled && target.Visible) target.PerformClick();
            return true;
        }
        if (keyData == Keys.F5 && !closing)
        {
            if (selectedPage == MainPage.MatchHistory && matchHistory != null) { _ = matchHistory.RefreshFromShortcutAsync(); return true; }
            if (selectedPage == MainPage.Store && storePreview != null) { _ = storePreview.RefreshFromShortcutAsync(); return true; }
        }
        return base.ProcessCmdKey(ref msg, keyData);
    }
    private static Label FieldLabel(string text) => new() { Text = text, Dock = DockStyle.Fill, Margin = new Padding(0, 1, 5, 0), ForeColor = Theme.Muted, TextAlign = ContentAlignment.MiddleLeft, Font = SharedFonts.Get("Yu Gothic UI", 10) };
    private static ThemedSelect RegionCombo() => new(["自動（おすすめ）", "アジア太平洋（東京など）", "北アメリカ", "ヨーロッパ", "韓国", "ブラジル", "ラテンアメリカ", "テスト環境（PBE）"])
    { AccessibleName = "アカウントの接続地域" };
}
