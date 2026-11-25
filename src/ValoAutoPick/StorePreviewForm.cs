using System.Net.Http;
using System.Text.Json;
using System.Windows.Forms.Integration;
using MediaElement = System.Windows.Controls.MediaElement;
using MediaState = System.Windows.Controls.MediaState;
using Stretch = System.Windows.Media.Stretch;

namespace ValoAutoPick;

public sealed class StorePreviewControl : UserControl
{
    private const string SignInStatus = "Riotアカウントへ接続しています…";
    private const string SkinLevelType = "e7c63390-eda7-46e0-bb7a-a6abdacd2433";
    private const string BuddyType = "dd3bf334-87f3-40bd-b043-682a57a8dc3a";
    private const string SprayType = "d5f120f8-ff8c-4aac-92ea-f2b5acbe9475";
    private const string PlayerCardType = "3f296c07-64c3-494c-923b-fe692a4fa1bd";
    private static readonly Dictionary<string, string> CurrencyNames = new(StringComparer.OrdinalIgnoreCase)
    {
        ["85ad13f7-3d1b-5128-9eb2-7cd8ee0b5741"] = "VP",
        ["85ca954a-41f2-ce94-9b45-8ca3dd39a00d"] = "KC"
    };
    private readonly CatalogClient catalog = new();
    private readonly RiotApi riot;
    private readonly bool ownsRiot;
    private readonly bool shellAuthentication;
    private readonly CancellationTokenSource lifetime = new();
    private readonly System.Windows.Forms.Timer countdownTimer = new() { Interval = 1000 };
    // Page and section switches use the Tab style; the coral fill is kept for actions only.
    private readonly ThemedButton storeTab = new() { Text = "今日のSTORE", Size = new(180, 40), Compact = true, Variant = ButtonVariant.Tab, Selected = true };
    private readonly ThemedButton catalogTab = new() { Text = "武器カタログ", Size = new(168, 40), Compact = true, Variant = ButtonVariant.Tab };
    private readonly ThemedButton dailySectionTab = new() { Text = "デイリー", Size = new(132, 38), Compact = true, Variant = ButtonVariant.Tab, Selected = true };
    private readonly ThemedButton nightSectionTab = new() { Text = "ナイトマーケット", Size = new(176, 38), Compact = true, Visible = false, Variant = ButtonVariant.Tab };
    private readonly ThemedButton bundleSectionTab = new() { Text = "注目バンドル", Size = new(150, 38), Compact = true, Visible = false, Variant = ButtonVariant.Tab };
    private readonly ThemedButton accessorySectionTab = new() { Text = "アクセサリー", Size = new(142, 38), Compact = true, Visible = false, Variant = ButtonVariant.Tab };
    private readonly ThemedButton previousCatalogPage = new() { Text = "前へ", Size = new(92, 34), Compact = true };
    private readonly ThemedButton nextCatalogPage = new() { Text = "次へ", Size = new(92, 34), Compact = true };
    private readonly Label catalogPageLabel = NativePageStyle.Label("1 / 1", NativePageStyle.Small, Theme.Muted);
    private readonly ThemedButton loginButton = new() { Text = "Riotにログイン", Size = new(162, 40), Compact = true };
    private readonly ThemedButton logoutButton = new() { Text = "ログアウト", Size = new(122, 40), Compact = true };
    private readonly ThemedButton refreshStoreButton = new() { Text = "今日のショップを更新", Size = new(204, 40), Compact = true };
    private readonly ThemedButton refreshCatalogButton = new() { Text = "カタログ更新", Size = new(150, 40), Compact = true };
    private readonly ThemedButton playVideoButton = new() { Text = "▶ 再生", Size = new(125, UiMetrics.CompactButtonHeight), Compact = true, AccessibleName = "選択中スキンの動画を再生" };
    private readonly ThemedButton playbackButton = new() { Text = "⏸ 一時停止", Size = new(125, UiMetrics.CompactButtonHeight), Compact = true, Visible = false };
    private readonly ThemedButton muteVideoButton = new() { Text = "音声をオン", Size = new(125, UiMetrics.CompactButtonHeight), Compact = true, Visible = false };
    private readonly ThemedButton stopVideoButton = new() { Text = "画像に戻る", Size = new(125, UiMetrics.CompactButtonHeight), Compact = true };
    private readonly ToolTip mediaToolTips = new() { InitialDelay = 500, ShowAlways = true };
    private readonly Label accountLabel = NativePageStyle.Label("Riotアカウント未接続", NativePageStyle.Small, Theme.Muted);
    private readonly Label skinCountLabel = NativePageStyle.Label("—", NativePageStyle.Metric, Theme.Text);
    private readonly Label chromaCountLabel = NativePageStyle.Label("—", NativePageStyle.Metric, Theme.Text);
    private readonly Label videoCountLabel = NativePageStyle.Label("—", NativePageStyle.Metric, Theme.Text);
    private readonly Label statusLabel = NativePageStyle.Label("", NativePageStyle.Small, Theme.Muted);
    private readonly WaitingIndicator statusSpinner = new(ringSize: 14, textGap: 6, showDelay: 80) { Dock = DockStyle.Left, Visible = false };
    private readonly List<(StorefrontBundle Bundle, Label Detail)> bundleDetails = [];
    private readonly Label catalogTitle = NativePageStyle.Label("スキンを選ぶと、画像・動画・カラーバリエーションを表示します。", NativePageStyle.Subheading);
    private readonly Label catalogMeta = NativePageStyle.Label("", NativePageStyle.Small, Theme.Muted);
    private readonly Label previewMessage = NativePageStyle.Label("スキンを選ぶとプレビューが表示されます。", NativePageStyle.Small, Theme.Muted);
    private readonly Label dailyTimerLabel = NativePageStyle.Label("—", NativePageStyle.SmallBold, Theme.Text);
    private readonly Label bundleTimerLabel = NativePageStyle.Label("—", NativePageStyle.SmallBold, Theme.Text);
    private readonly Label nightTimerLabel = NativePageStyle.Label("—", NativePageStyle.SmallBold, Theme.Text);
    private readonly Label accessoryTimerLabel = NativePageStyle.Label("—", NativePageStyle.SmallBold, Theme.Text);
    private readonly Panel storeSections = new() { Dock = DockStyle.Fill, BackColor = Theme.Background, Margin = Padding.Empty };
    private readonly Panel dailyOffersHost = new() { Dock = DockStyle.Fill, BackColor = Theme.Surface, Margin = Padding.Empty };
    private readonly SkeletonScreen dailySkeleton = new(SkeletonLayout.StoreOffers) { Dock = DockStyle.Fill, BackColor = Theme.Surface, Visible = false };
    private readonly ResponsiveCardGrid dailyOffers = OfferGrid(1.5);
    private readonly ResponsiveCardGrid nightOffers = OfferGrid(1.35);
    private readonly ResponsiveCardGrid bundleCards = OfferGrid(1.5);
    private readonly ResponsiveCardGrid accessoryCards = OfferGrid(1.2);
    private readonly FlowLayoutPanel catalogGrid = new() { Dock = DockStyle.Fill, AutoScroll = false, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, BackColor = Theme.Surface, Margin = Padding.Empty, Padding = new Padding(2) };
    private readonly Panel catalogGridHost = new() { Dock = DockStyle.Fill, BackColor = Theme.Surface, Margin = Padding.Empty };
    private readonly SkeletonScreen catalogSkeleton = new(SkeletonLayout.CatalogCards) { Dock = DockStyle.Fill, BackColor = Theme.Surface, Visible = false };
    private readonly FlowLayoutPanel chromaOptions = new() { AutoSize = true, WrapContents = true, FlowDirection = FlowDirection.LeftToRight, BackColor = Theme.Surface, Margin = Padding.Empty };
    private readonly FlowLayoutPanel levelOptions = new() { AutoSize = true, WrapContents = true, FlowDirection = FlowDirection.LeftToRight, BackColor = Theme.Surface, Margin = Padding.Empty };
    private readonly Panel storePage = new() { Dock = DockStyle.Fill, BackColor = Theme.Background };
    private readonly Panel catalogPage = new() { Dock = DockStyle.Fill, BackColor = Theme.Background, Visible = false };
    private Control previewPanel = null!;
    private bool disposingStore;
    private EventHandler<System.Windows.ExceptionRoutedEventArgs>? mediaFailedHandler;
    private System.Windows.RoutedEventHandler? mediaOpenedHandler;
    private System.Windows.RoutedEventHandler? mediaEndedHandler;
    private readonly Panel visualHost = new() { Dock = DockStyle.Fill, BackColor = Theme.Surface, Margin = Padding.Empty };
    private readonly SharpImageBox previewImage = new() { Dock = DockStyle.Fill, BackColor = Color.FromArgb(10, 13, 19), RoundedCorners = true, DarkStage = true };
    private readonly ElementHost videoHost = new() { Dock = DockStyle.Fill, BackColor = Color.FromArgb(10, 13, 19), Visible = false };
    private readonly SkeletonScreen videoLoadingOverlay = new(SkeletonLayout.MediaPreview) { Dock = DockStyle.Fill, BackColor = Color.FromArgb(10, 13, 19), Visible = false };
    private readonly System.Windows.Forms.Timer videoOpenTimer = new() { Interval = 20_000 };
    private readonly System.Windows.Controls.Grid videoSurface = new()
    {
        Background = System.Windows.Media.Brushes.Black,
        HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
        VerticalAlignment = System.Windows.VerticalAlignment.Stretch
    };
    private readonly MediaElement mediaPlayer = new() { LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Stop, Stretch = Stretch.Uniform, IsMuted = true, Volume = .2 };
    private readonly ThemedTextEntry searchBox = new() { PlaceholderText = "スキン名・武器名で検索" };
    private readonly ThemedSelect categorySelect = new(["すべてのカテゴリー"]);
    private readonly ThemedSelect weaponSelect = new(["すべての武器"]);
    private CancellationTokenSource? storeImageLoads;
    private LoginWindow? loginWindow;
    private CatalogSnapshot? catalogSnapshot;
    private Storefront? storefront;
    private DateTimeOffset storefrontFetchedAt;
    private IReadOnlyList<SkinCard> allSkins = [];
    private Dictionary<string, (SkinCard Skin, int Level)> rewardIndex = new(StringComparer.OrdinalIgnoreCase);
    private SkinCard? selectedSkin;
    private int selectedChroma;
    private int selectedLevel;
    private int catalogPageIndex;
    private int catalogPageSize = 20;
    private int catalogCardImageHeight;
    private int catalogCardWidth;
    private int catalogCardTitleHeight;
    private int catalogCardSubtitleHeight;
    private bool catalogEnsureSelectedVisible;
    private int previewGeneration;
    private int catalogMetricsGeneration;
    private (SkinCard? Skin, int Chroma, int Level)? displayedPreview;
    private CancellationTokenSource? previewImageLoad;
    private CancellationTokenSource? catalogMetricsStop;
    private int storeGeneration;
    private CancellationTokenSource? activeStoreLoad;
    private CancellationTokenSource? videoLoad;
    private bool videoOpening;
    private bool videoPaused;
    private TaskCompletionSource<bool>? playbackOpened;
    private Action? resizePreviewLayout;
    private Action? resizeMediaActions;
    private bool catalogLoaded;
    private bool catalogTextMetricsReady;
    private bool loadingCatalog;
    private Task? catalogLoadTask;
    private string catalogLoadMessage = "武器カタログを開くと一覧を表示します。";
    private bool loadingStore;
    private bool signingIn;
    private bool changingFilters;
    private bool loopVideo = true;
    private string settledStatus = "";
    private bool settledStatusError;
    private readonly List<string> activeProgressStatuses = [];

    public event EventHandler? LoginRequested;
    public event EventHandler? LogoutRequested;
    internal string PreviewVideoResult { get; private set; } = "NotStarted";

    public StorePreviewControl() : this(new RiotApi(), ownsRiot: true, shellAuthentication: false) { }
    public StorePreviewControl(RiotApi api) : this(api, ownsRiot: false, shellAuthentication: true) { }

    private StorePreviewControl(RiotApi api, bool ownsRiot, bool shellAuthentication)
    {
        riot = api;
        this.ownsRiot = ownsRiot;
        this.shellAuthentication = shellAuthentication;
        Dock = DockStyle.Fill;
        BackColor = Theme.Background;
        mediaPlayer.HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch;
        mediaPlayer.VerticalAlignment = System.Windows.VerticalAlignment.Stretch;
        videoSurface.Children.Add(mediaPlayer);
        videoHost.Child = videoSurface;
        videoHost.SizeChanged += (_, _) => ResizeVideoSurface();
        videoHost.DpiChangedAfterParent += (_, _) => ResizeVideoSurface();
        videoOpenTimer.Tick += (_, _) =>
        {
            videoOpenTimer.Stop();
            if (!videoOpening || !videoLoadingOverlay.Visible || IsDisposed || Disposing) return;
            PreviewVideoResult = "MediaOpenTimedOut";
            ShowPreviewImage();
            SetStatus("動画の読み込みに時間がかかっています。もう一度お試しください。", true);
        };
        mediaFailedHandler = (_, e) =>
        {
            if (disposingStore || IsDisposed || Disposing) return;
            if (!videoOpening && !videoHost.Visible) return;
            videoOpenTimer.Stop();
            videoOpening = false;
            videoLoadingOverlay.Loading = false;
            videoLoadingOverlay.Visible = false;
            PreviewVideoResult = $"MediaFailed:{e.ErrorException.GetType().Name}:{e.ErrorException.Message}";
            playbackOpened?.TrySetResult(false);
            NativePageStyle.LogPageError("StoreVideo", e.ErrorException);
            ShowPreviewImage();
            SetStatus("この動画を再生できませんでした。画像プレビューは引き続き利用できます。", true);
        };
        mediaPlayer.MediaFailed += mediaFailedHandler;
        mediaOpenedHandler = (_, _) =>
        {
            if (disposingStore || IsDisposed || Disposing) return;
            if (!videoOpening) return;
            videoOpenTimer.Stop();
            videoOpening = false;
            videoLoadingOverlay.Loading = false;
            videoLoadingOverlay.Visible = false;
            previewImage.Visible = false;
            // Leave the 1x1 parking spot from PlayPreviewVideoAsync and take the full preview area.
            RestoreVideoHost(visible: true);
            videoHost.BringToFront();
            ResizeVideoSurface();
            videoSurface.UpdateLayout();
            PreviewVideoResult = $"MediaOpened:{mediaPlayer.NaturalVideoWidth}x{mediaPlayer.NaturalVideoHeight}:{mediaPlayer.ActualWidth:0}x{mediaPlayer.ActualHeight:0}:{videoHost.ClientSize.Width}x{videoHost.ClientSize.Height}";
            playVideoButton.Visible = false;
            stopVideoButton.Text = "画像へ";
            stopVideoButton.Invalidate();
            stopVideoButton.Visible = true;
            playbackOpened?.TrySetResult(true);
            videoPaused = false;
            UpdateVideoControls();
            playbackButton.Visible = true;
            muteVideoButton.Visible = true;
            SetStatus("", false);
        };
        mediaPlayer.MediaOpened += mediaOpenedHandler;
        mediaEndedHandler = (_, _) =>
        {
            if (disposingStore || IsDisposed || Disposing) return;
            if (!loopVideo || !videoHost.Visible) return;
            mediaPlayer.Position = TimeSpan.Zero;
            mediaPlayer.Play();
        };
        mediaPlayer.MediaEnded += mediaEndedHandler;
        BuildInterface();
        WireEvents();
        UpdateAccountState();
        RenderStorefront();
        RenderCatalogGrid();
        countdownTimer.Tick += (_, _) => UpdateCountdowns();
        if (Visible) countdownTimer.Start();
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing && !disposingStore)
        {
            disposingStore = true;
            lifetime.Cancel();
            activeStoreLoad?.Cancel();
            videoLoad?.Cancel();
            catalogMetricsStop?.Cancel();
            if (mediaFailedHandler is not null) mediaPlayer.MediaFailed -= mediaFailedHandler;
            if (mediaOpenedHandler is not null) mediaPlayer.MediaOpened -= mediaOpenedHandler;
            if (mediaEndedHandler is not null) mediaPlayer.MediaEnded -= mediaEndedHandler;
            StopMedia();
            videoHost.Child = null;
            videoOpenTimer.Dispose();
            countdownTimer.Stop(); countdownTimer.Dispose();
            mediaToolTips.Dispose();
            catalog.Dispose(); loginWindow?.Dispose();
            if (ownsRiot) riot.Dispose();
            previewImageLoad?.Cancel();
            previewImageLoad?.Dispose();
            storeImageLoads?.Cancel(); storeImageLoads?.Dispose(); storeImageLoads = null;
            lifetime.Dispose();
        }
        base.Dispose(disposing);
    }

    public async Task ActivatePageAsync()
    {
        if (IsDisposed) return;
        try
        {
            // Start both synchronous loading phases before yielding to the message loop.
            var storeTask = riot.HasAccountSession && (storefront is null || RemainingSeconds(storefront.DailySkins.RemainingSeconds) == 0)
                ? LoadStorefrontAsync() : Task.CompletedTask;
            if (!catalogLoaded) await LoadCatalogAsync(refresh: false);
            await storeTask;
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            NativePageStyle.LogPageError("StoreActivation", ex);
            SetStatus("STOREを読み込めませんでした。カタログ更新を押して再試行してください。", true);
        }
    }

    internal void ReportActivationError(string message) => SetStatus(message, true);

    /// <summary>F5 from the shell: refreshes whichever list is on screen, using the same buttons' rules.</summary>
    public async Task RefreshFromShortcutAsync()
    {
        if (IsDisposed || !Visible) return;
        try
        {
            if (catalogTab.Selected) { if (refreshCatalogButton.Enabled) await LoadCatalogAsync(refresh: true); }
            else if (refreshStoreButton.Enabled) await LoadStorefrontAsync(force: true);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (ObjectDisposedException) { }
    }

    // Run after the page receives its final host bounds, before its first paint.
    internal void PreparePageLayout()
    {
        Controls.OfType<TableLayoutPanel>().FirstOrDefault()?.PerformLayout();
        storePage.Parent?.PerformLayout();
        if (storePage.Visible)
        {
            storePage.PerformLayout();
            LayoutStoreGrids();
        }
    }

    internal void ShowCatalogForPreview() => ShowCatalogPage();

    internal async Task<bool> PlayVideoForPreviewAsync(TimeSpan timeout)
    {
        if (playVideoButton.Tag is not string url || string.IsNullOrWhiteSpace(url))
        {
            PreviewVideoResult = "NoVideoUrl";
            return false;
        }
        var completion = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        playbackOpened = completion;
        using var timeoutStop = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        timeoutStop.CancelAfter(timeout);
        try
        {
            PreviewVideoResult = "Loading";
            if (!await PlayPreviewVideoAsync(timeoutStop.Token))
            {
                if (timeoutStop.IsCancellationRequested && !lifetime.IsCancellationRequested)
                    PreviewVideoResult = "TimedOut";
                return false;
            }
            return await completion.Task.WaitAsync(timeoutStop.Token);
        }
        catch (OperationCanceledException)
        {
            var cancelledByLifetime = lifetime.IsCancellationRequested;
            PreviewVideoResult = cancelledByLifetime ? "Cancelled" : "TimedOut";
            if (!cancelledByLifetime) ShowPreviewImage();
            return false;
        }
        finally
        {
            if (ReferenceEquals(playbackOpened, completion)) playbackOpened = null;
        }
    }

    public async Task NotifyAccountChangedAsync()
    {
        if (IsDisposed) return;
        UpdateAccountState();
        if (riot.HasAccountSession) await LoadStorefrontAsync();
        else ResetSession();
    }

    public void ResetSession()
    {
        storeGeneration++;
        activeStoreLoad?.Cancel();
        activeStoreLoad = null;
        loadingStore = false;
        ShowPreviewImage();
        storefront = null;
        storefrontFetchedAt = default;
        UpdateAccountState();
        RenderStorefront();
        activeProgressStatuses.RemoveAll(message => message.StartsWith("今日のショップ", StringComparison.Ordinal));
        settledStatus = "";
        settledStatusError = false;
        SetStatus("", false);
    }

    private void BuildInterface()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 3, BackColor = Theme.Background, Padding = Padding.Empty, Margin = Padding.Empty };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var headerRow = new RowStyle(SizeType.Absolute, UiMetrics.Px(this, 90));
        root.RowStyles.Add(headerRow);
        var navigationRow = new RowStyle(SizeType.Absolute, UiMetrics.Px(this, 40));
        root.RowStyles.Add(navigationRow);
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.Controls.Add(BuildHeader(headerRow), 0, 0);
        root.Controls.Add(BuildNavigation(navigationRow), 0, 1);
        root.Controls.Add(BuildPages(), 0, 2);
        Controls.Add(root);
    }

    private Control BuildHeader(RowStyle row)
    {
        var header = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Background, Margin = Padding.Empty };
        var title = NativePageStyle.Label("STORE  /  今日のラインナップ", NativePageStyle.Heading);
        var statusRow = new Panel { BackColor = Theme.Background, Margin = Padding.Empty, AccessibleName = "STORE の状態" };
        statusLabel.Dock = DockStyle.Fill;
        statusLabel.AutoSize = false;
        statusLabel.AutoEllipsis = true;
        statusRow.Controls.Add(statusLabel);
        statusRow.Controls.Add(statusSpinner);
        var counts = new RoundedCard { Margin = Padding.Empty, Padding = Padding.Empty, AccessibleName = "カタログの収録件数" };
        var blocks = new[] { CountBlock(skinCountLabel, "武器スキン"), CountBlock(chromaCountLabel, "カラー"), CountBlock(videoCountLabel, "動画") };
        counts.Controls.AddRange(blocks);
        header.Controls.Add(title); header.Controls.Add(statusRow); header.Controls.Add(counts);
        void Place()
        {
            var s = new Func<int, int>(value => UiMetrics.Px(header, value));
            var inset = s(UiMetrics.ControlGap);
            var blockHeight = blocks.Max(block => block.Height);
            var cardHeight = blockHeight + inset * 2;
            var cardWidth = s(360);
            var headerHeight = cardHeight + s(UiMetrics.ControlGap);
            if (row.Height != headerHeight) row.Height = headerHeight;
            counts.Bounds = new Rectangle(Math.Max(0, header.ClientSize.Width - cardWidth), 0, cardWidth, cardHeight);
            var innerWidth = Math.Max(0, counts.ClientSize.Width - inset * 2);
            for (var i = 0; i < blocks.Length; i++)
            {
                var left = inset + innerWidth * i / blocks.Length;
                var right = inset + innerWidth * (i + 1) / blocks.Length;
                blocks[i].Bounds = new Rectangle(left, inset, right - left, blockHeight);
            }
            var titleHeight = NativePageStyle.TextHeight(header, title.Font, verticalPadding: UiMetrics.ControlGap);
            var leftWidth = Math.Max(0, header.ClientSize.Width - counts.Width - s(UiMetrics.ContentGap));
            var showStatus = !string.IsNullOrWhiteSpace(statusLabel.Text);
            var statusHeight = NativePageStyle.TextHeight(header, statusLabel.Font, verticalPadding: 0);
            var top = Math.Max(0, (cardHeight - titleHeight - statusHeight) / 2);
            title.Bounds = new Rectangle(0, top, leftWidth, titleHeight);
            statusRow.Bounds = new Rectangle(0, top + titleHeight, leftWidth, statusHeight);
            statusRow.Visible = showStatus;
        }
        header.Resize += (_, _) => Place();
        statusLabel.TextChanged += (_, _) => { mediaToolTips.SetToolTip(statusLabel, statusLabel.Text); Place(); };
        statusLabel.FontChanged += (_, _) => Place();
        Place();
        return header;
    }

    private static Control CountBlock(Label value, string caption)
    {
        var block = new Panel { Width = 112, Height = 64, BackColor = Theme.Surface, Margin = Padding.Empty };
        var text = NativePageStyle.Label(caption, NativePageStyle.Small, Theme.Muted);
        text.AutoSize = false;
        text.AutoEllipsis = false;
        value.TextAlign = ContentAlignment.BottomLeft;
        block.Controls.Add(value); block.Controls.Add(text);
        void Place()
        {
            var labelHeight = NativePageStyle.TextHeight(block, text.Font, verticalPadding: 4);
            var valueHeight = NativePageStyle.TextHeight(block, value.Font, verticalPadding: 4);
            block.Height = valueHeight + labelHeight;
            value.Bounds = new Rectangle(0, 0, block.ClientSize.Width, valueHeight);
            text.Bounds = new Rectangle(0, valueHeight, block.ClientSize.Width, labelHeight);
        }
        block.DpiChangedAfterParent += (_, _) => Place();
        block.Resize += (_, _) => Place();
        Place();
        return block;
    }

    private Control BuildNavigation(RowStyle row)
    {
        var bar = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Background, Margin = Padding.Empty };
        storeTab.Size = new Size(UiMetrics.Px(this, 180), UiMetrics.Px(this, UiMetrics.ButtonHeight));
        catalogTab.Size = new Size(UiMetrics.Px(this, 168), UiMetrics.Px(this, UiMetrics.ButtonHeight));
        loginButton.Size = new Size(UiMetrics.Px(this, 162), UiMetrics.Px(this, UiMetrics.ButtonHeight));
        logoutButton.Size = new Size(UiMetrics.Px(this, 122), UiMetrics.Px(this, UiMetrics.ButtonHeight));
        refreshStoreButton.Size = new Size(UiMetrics.Px(this, 204), UiMetrics.Px(this, UiMetrics.ButtonHeight));
        refreshCatalogButton.Size = new Size(UiMetrics.Px(this, 150), UiMetrics.Px(this, UiMetrics.ButtonHeight));
        var tabs = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, FlowDirection = FlowDirection.LeftToRight, BackColor = Theme.Background, Margin = Padding.Empty, Padding = Padding.Empty };
        storeTab.Margin = Padding.Empty; catalogTab.Margin = new Padding(UiMetrics.Px(this, UiMetrics.ControlGap), 0, 0, 0);
        tabs.Controls.Add(storeTab); tabs.Controls.Add(catalogTab);
        var actions = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, FlowDirection = FlowDirection.LeftToRight, BackColor = Theme.Background, Margin = Padding.Empty, Padding = Padding.Empty };
        accountLabel.AutoSize = true;
        accountLabel.AutoEllipsis = false;
        accountLabel.UseMnemonic = false;
        actions.Controls.Add(accountLabel); actions.Controls.Add(refreshStoreButton); actions.Controls.Add(refreshCatalogButton);
        actions.Controls.Add(loginButton); actions.Controls.Add(logoutButton);
        bar.Controls.Add(tabs); bar.Controls.Add(actions);

        var placing = false;
        var placeAgain = false;
        void Place()
        {
            if (placing) { placeAgain = true; return; }
            if (bar.ClientSize.Width <= 1) return;
            placing = true;
            try
            {
                do
                {
                    placeAgain = false;
                    var gap = UiMetrics.Px(bar, UiMetrics.ControlGap);
                    // Use the same physical gap between the tabs and beneath them.
                    if (catalogTab.Margin.Left != gap) catalogTab.Margin = new Padding(gap, 0, 0, 0);
                    var first = true;
                    foreach (Control action in actions.Controls)
                    {
                        var margin = new Padding(action.Visible && !first ? gap : 0, 0, 0, 0);
                        if (action.Margin != margin) action.Margin = margin;
                        if (action.Visible) first = false;
                    }
                    actions.PerformLayout();
                    tabs.PerformLayout();
                    var stacked = tabs.Width + gap + actions.Width > bar.ClientSize.Width;
                    var contentHeight = stacked ? tabs.Height + gap + actions.Height : Math.Max(tabs.Height, actions.Height);
                    var height = contentHeight + gap;
                    tabs.Location = new Point(0, stacked ? actions.Height + gap : (contentHeight - tabs.Height) / 2);
                    actions.Location = new Point(Math.Max(0, bar.ClientSize.Width - actions.Width),
                        stacked ? 0 : (contentHeight - actions.Height) / 2);
                    if (row.Height != height) row.Height = height;
                    if (bar.Height != height) bar.Parent?.PerformLayout();
                } while (placeAgain);
            }
            finally { placing = false; }
        }

        bar.SizeChanged += (_, _) => Place();
        bar.VisibleChanged += (_, _) => { if (bar.Visible) Place(); };
        tabs.SizeChanged += (_, _) => Place();
        actions.SizeChanged += (_, _) => Place();
        foreach (Control action in actions.Controls) action.VisibleChanged += (_, _) => Place();
        return bar;
    }

    private Control BuildPages()
    {
        BuildStorePage();
        BuildCatalogPage();
        var pages = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Background, Margin = Padding.Empty };
        pages.Controls.Add(storePage); pages.Controls.Add(catalogPage);
        return pages;
    }

    private void BuildStorePage()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Theme.Background, Margin = Padding.Empty };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        var tabsRow = new RowStyle(SizeType.Absolute, UiMetrics.Px(this, 50));
        layout.RowStyles.Add(tabsRow);
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var tabs = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Theme.Background, Margin = Padding.Empty, Padding = Padding.Empty };
        dailySectionTab.Size = new Size(UiMetrics.Px(this, 132), UiMetrics.Px(this, UiMetrics.ButtonHeight));
        nightSectionTab.Size = new Size(UiMetrics.Px(this, 176), UiMetrics.Px(this, UiMetrics.ButtonHeight));
        bundleSectionTab.Size = new Size(UiMetrics.Px(this, 150), UiMetrics.Px(this, UiMetrics.ButtonHeight));
        accessorySectionTab.Size = new Size(UiMetrics.Px(this, 142), UiMetrics.Px(this, UiMetrics.ButtonHeight));
        dailySectionTab.Margin = new Padding(0, 0, UiMetrics.Px(this, UiMetrics.ControlGap), 0);
        nightSectionTab.Margin = new Padding(0, 0, UiMetrics.Px(this, UiMetrics.ControlGap), 0);
        bundleSectionTab.Margin = new Padding(0, 0, UiMetrics.Px(this, UiMetrics.ControlGap), 0);
        accessorySectionTab.Margin = Padding.Empty;
        tabs.Controls.Add(dailySectionTab); tabs.Controls.Add(nightSectionTab); tabs.Controls.Add(bundleSectionTab); tabs.Controls.Add(accessorySectionTab);
        void SizeTabs()
        {
            var height = tabs.Controls.Cast<Control>().Where(control => control.Visible)
                .Select(control => control.Height + control.Margin.Vertical).DefaultIfEmpty(dailySectionTab.Height).Max() + UiMetrics.Px(tabs, UiMetrics.ControlGap);
            if (tabsRow.Height != height) tabsRow.Height = height;
        }
        tabs.SizeChanged += (_, _) => SizeTabs();
        foreach (Control tab in tabs.Controls)
        {
            tab.SizeChanged += (_, _) => SizeTabs();
            tab.VisibleChanged += (_, _) => SizeTabs();
        }
        layout.Controls.Add(tabs, 0, 0);

        var daily = CreateSection("DAILY ROTATION", dailyTimerLabel, out var dailyBody);
        dailyOffersHost.Controls.Add(dailyOffers);
        dailyOffersHost.Controls.Add(dailySkeleton);
        dailySkeleton.BringToFront();
        dailyBody.Controls.Add(dailyOffersHost);
        var night = CreateSection("NIGHT MARKET", nightTimerLabel, out var nightBody);
        nightBody.Controls.Add(nightOffers);
        night.Visible = false;
        var bundle = CreateSection("FEATURED BUNDLES", bundleTimerLabel, out var bundleBody);
        bundleBody.Controls.Add(bundleCards);
        bundle.Visible = false;
        var accessories = CreateSection("ACCESSORY OFFERS", accessoryTimerLabel, out var accessoryBody);
        accessoryBody.Controls.Add(accessoryCards);
        accessories.Visible = false;
        daily.Tag = "daily"; night.Tag = "night"; bundle.Tag = "bundle"; accessories.Tag = "accessories";
        storeSections.Controls.Add(daily); storeSections.Controls.Add(night); storeSections.Controls.Add(bundle); storeSections.Controls.Add(accessories);
        layout.Controls.Add(storeSections, 0, 1);
        storePage.Controls.Add(layout);
        dailySectionTab.Click += (_, _) => ShowStoreSection("daily");
        nightSectionTab.Click += (_, _) => ShowStoreSection("night");
        bundleSectionTab.Click += (_, _) => ShowStoreSection("bundle");
        accessorySectionTab.Click += (_, _) => ShowStoreSection("accessories");
    }

    private Panel CreateSection(string title, Label timer, out Panel body)
    {
        var card = NativePageStyle.Card();
        card.Dock = DockStyle.Fill;
        card.Margin = Padding.Empty;
        card.Padding = new Padding(UiMetrics.Px(this, UiMetrics.ContentGap));
        var header = new Panel { Dock = DockStyle.Top, Height = UiMetrics.Px(this, 32), BackColor = Theme.Surface };
        var name = NativePageStyle.Label(title, NativePageStyle.SmallBold, Theme.Muted); name.Dock = DockStyle.Fill;
        var timerCaption = NativePageStyle.Label("次回更新  ", NativePageStyle.Small, Theme.Muted); timerCaption.Dock = DockStyle.Right; timerCaption.Width = UiMetrics.Px(this, 90); timerCaption.TextAlign = ContentAlignment.MiddleRight;
        timer.Dock = DockStyle.Right; timer.Width = UiMetrics.Px(this, 90); timer.TextAlign = ContentAlignment.MiddleRight;
        header.Controls.Add(timerCaption); header.Controls.Add(timer); header.Controls.Add(name);
        body = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface, Padding = new Padding(0, 8, 0, 0) };
        card.Controls.Add(body); card.Controls.Add(header);
        return card;
    }

    private void BuildCatalogPage()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Theme.Background, Margin = Padding.Empty };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, UiMetrics.Px(this, UiMetrics.ButtonHeight + UiMetrics.ControlGap))); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Theme.Background,
            Padding = new Padding(0, 0, 0, UiMetrics.Px(this, UiMetrics.ControlGap)), Margin = Padding.Empty };
        searchBox.Width = UiMetrics.Px(this, 270); searchBox.Height = UiMetrics.Px(this, UiMetrics.ButtonHeight); searchBox.Margin = new Padding(0, 0, UiMetrics.Px(this, UiMetrics.ControlGap), 0);
        categorySelect.Width = UiMetrics.Px(this, 230); categorySelect.Height = UiMetrics.Px(this, UiMetrics.ButtonHeight); categorySelect.Margin = new Padding(0, 0, UiMetrics.Px(this, UiMetrics.ControlGap), 0);
        weaponSelect.Width = UiMetrics.Px(this, 210); weaponSelect.Height = UiMetrics.Px(this, UiMetrics.ButtonHeight); weaponSelect.Margin = Padding.Empty;
        toolbar.Controls.Add(searchBox); toolbar.Controls.Add(categorySelect); toolbar.Controls.Add(weaponSelect);
        layout.Controls.Add(toolbar, 0, 0);

        var split = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Theme.Background, Margin = Padding.Empty };
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 0)); split.ColumnStyles.Add(new ColumnStyle(SizeType.Absolute, 0));
        var listCard = NativePageStyle.Card();
        listCard.Margin = new Padding(0, 0, UiMetrics.Px(this, UiMetrics.ContentGap), 0);
        listCard.Padding = new Padding(UiMetrics.Px(this, UiMetrics.ContentGap));
        catalogGrid.Padding = new Padding(UiMetrics.Px(this, UiMetrics.TightGap));
        var pageFooter = new Panel { Dock = DockStyle.Bottom, Height = UiMetrics.Px(this, 45), BackColor = Theme.Surface };
        previousCatalogPage.Size = new Size(UiMetrics.Px(this, 92), UiMetrics.Px(this, UiMetrics.CompactButtonHeight));
        nextCatalogPage.Size = previousCatalogPage.Size;
        previousCatalogPage.Location = new Point(0, UiMetrics.Px(this, UiMetrics.TightGap));
        nextCatalogPage.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        catalogPageLabel.TextAlign = ContentAlignment.MiddleCenter;
        catalogPageLabel.Dock = DockStyle.None;
        void PlacePageFooter()
        {
            var gap = UiMetrics.Px(this, UiMetrics.ControlGap);
            previousCatalogPage.Size = nextCatalogPage.Size = new Size(UiMetrics.Px(this, 92), UiMetrics.Px(this, UiMetrics.CompactButtonHeight));
            previousCatalogPage.Location = new Point(0, UiMetrics.Px(this, UiMetrics.TightGap));
            nextCatalogPage.Location = new Point(Math.Max(0, pageFooter.ClientSize.Width - nextCatalogPage.Width), UiMetrics.Px(this, UiMetrics.TightGap));
            catalogPageLabel.Bounds = new Rectangle(previousCatalogPage.Right + gap, 0,
                Math.Max(0, nextCatalogPage.Left - previousCatalogPage.Right - gap * 2), pageFooter.ClientSize.Height);
        }
        pageFooter.Resize += (_, _) => PlacePageFooter();
        PlacePageFooter();
        pageFooter.Controls.Add(catalogPageLabel); pageFooter.Controls.Add(previousCatalogPage); pageFooter.Controls.Add(nextCatalogPage);
        catalogGridHost.Controls.Add(catalogGrid);
        catalogGridHost.Controls.Add(catalogSkeleton);
        catalogSkeleton.BringToFront();
        listCard.Controls.Add(catalogGridHost);
        listCard.Controls.Add(pageFooter);
        split.Controls.Add(listCard, 0, 0);
        previewPanel = BuildPreviewPanel();
        split.Controls.Add(previewPanel, 1, 0);
        void ArrangeCatalogSplit()
        {
            if (split.ClientSize.Width <= 0) return;
            var previewWidth = Math.Clamp((int)Math.Round(split.ClientSize.Width * .36), UiMetrics.Px(this, 380), UiMetrics.Px(this, 560));
            var listCellWidth = Math.Max(0, split.ClientSize.Width - previewWidth);
            var listColumn = split.ColumnStyles[0];
            var previewColumn = split.ColumnStyles[1];
            if (listColumn.Width != listCellWidth) { listColumn.SizeType = SizeType.Absolute; listColumn.Width = listCellWidth; }
            if (previewColumn.Width != previewWidth) { previewColumn.SizeType = SizeType.Absolute; previewColumn.Width = previewWidth; }
        }
        split.SizeChanged += (_, _) => ArrangeCatalogSplit();
        split.Layout += (_, _) => ArrangeCatalogSplit();
        ArrangeCatalogSplit();
        layout.Controls.Add(split, 0, 1);
        catalogPage.Controls.Add(layout);
        catalogGrid.SizeChanged += (_, _) =>
        {
            if (!catalogPage.Visible || switchingCatalogPage) return;
            if (catalogLoaded) UpdateCatalogPageSize();
            else RenderCatalogGrid();
        };
        catalogGrid.MouseWheel += CatalogMouseWheel;
        catalogGridHost.MouseWheel += CatalogMouseWheel;
        previousCatalogPage.Click += (_, _) => { if (catalogPageIndex > 0) { catalogPageIndex--; RenderCatalogGrid(); } };
        nextCatalogPage.Click += (_, _) => { catalogPageIndex++; RenderCatalogGrid(); };
    }

    private Control BuildPreviewPanel()
    {
        var panel = NativePageStyle.Card();
        panel.Margin = Padding.Empty;
        panel.Padding = new Padding(UiMetrics.Px(this, UiMetrics.CardInset));
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 8, BackColor = Theme.Surface, Margin = Padding.Empty, Padding = Padding.Empty };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, UiMetrics.Px(this, 70)));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, UiMetrics.Px(this, 45)));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, UiMetrics.Px(this, 25)));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, UiMetrics.Px(this, 35)));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, UiMetrics.Px(this, 25)));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, UiMetrics.Px(this, 35)));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, UiMetrics.Px(this, 30)));
        var header = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface, Margin = Padding.Empty };
        var mediaBar = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface, Margin = Padding.Empty };
        catalogTitle.AutoSize = false;
        catalogMeta.AutoSize = false;
        catalogTitle.AutoEllipsis = true;
        catalogMeta.AutoEllipsis = true;
        playVideoButton.Margin = playbackButton.Margin = muteVideoButton.Margin = stopVideoButton.Margin = Padding.Empty;
        mediaBar.Controls.Add(playVideoButton); mediaBar.Controls.Add(playbackButton);
        mediaBar.Controls.Add(muteVideoButton); mediaBar.Controls.Add(stopVideoButton);
        header.Controls.Add(catalogTitle); header.Controls.Add(catalogMeta);
        var labelFont = SharedFonts.Get("Yu Gothic UI", 11.5f, FontStyle.Bold);
        var iconFont = SharedFonts.Get("Segoe MDL2 Assets", 14f);
        const TextFormatFlags measureFlags = TextFormatFlags.SingleLine | TextFormatFlags.NoPadding;
        void ApplyMediaButton(ThemedButton button, bool iconOnly, string glyph, string text, string accessibleName, string tip)
        {
            button.Font = iconOnly ? iconFont : labelFont;
            button.Text = iconOnly ? glyph : text;
            button.AccessibleName = accessibleName;
            button.AccessibleDescription = tip;
            mediaToolTips.SetToolTip(button, tip);
        }
        void PlaceHeader()
        {
            var s = new Func<int, int>(value => UiMetrics.Px(header, value));
            catalogTitle.Bounds = new Rectangle(0, 0, header.ClientSize.Width, s(40));
            catalogMeta.Bounds = new Rectangle(s(UiMetrics.TightGap), s(43), header.ClientSize.Width - s(UiMetrics.TightGap), s(22));
        }
        void PlaceMediaActions()
        {
            var width = mediaBar.ClientSize.Width;
            if (width <= 0) return;
            var labelWidth = new[] { "▶ 再生", "⏸ 一時停止", "音声をオン", "ミュート", "画像に戻る", "キャンセル" }
                .Max(text => TextRenderer.MeasureText(text, labelFont, Size.Empty, measureFlags).Width)
                + UiMetrics.Px(mediaBar, UiMetrics.ControlGap) * 2;
            var s = new Func<int, int>(value => UiMetrics.Px(mediaBar, value));
            var iconWidth = s(35);
            var buttonHeight = s(UiMetrics.CompactButtonHeight);
            var gap = s(UiMetrics.ControlGap);
            var groupGap = s(UiMetrics.PageInset);
            var stage0 = labelWidth * 3 + gap + groupGap;
            var stage1 = labelWidth * 2 + iconWidth + gap + groupGap;
            var stage2 = labelWidth + iconWidth * 2 + gap + groupGap;
            var iconStage = width >= stage0 ? 0 : width >= stage1 ? 1 : width >= stage2 ? 2 : 3;
            var playbackIcon = iconStage >= 2;
            var audioIcon = iconStage >= 1;
            var stopIcon = iconStage >= 3;
            var playAction = "選択中スキンの動画を再生";
            var playbackAction = videoPaused ? "動画を再生" : "動画を一時停止";
            var audioAction = mediaPlayer.IsMuted ? "動画の音声をオン" : "動画をミュート";
            var cancelingVideo = videoOpening || videoLoad is { IsCancellationRequested: false };
            var stopAction = cancelingVideo ? "動画の読み込みをキャンセル" : "プレビュー画像に戻る";
            ApplyMediaButton(playVideoButton, playbackIcon, "\uE768", "▶ 再生", playAction, playAction);
            ApplyMediaButton(playbackButton, playbackIcon, videoPaused ? "\uE768" : "\uE769", videoPaused ? "▶ 再生" : "⏸ 一時停止", playbackAction, playbackAction);
            ApplyMediaButton(muteVideoButton, audioIcon, mediaPlayer.IsMuted ? "\uE767" : "\uE74F", mediaPlayer.IsMuted ? "音声をオン" : "ミュート", audioAction, audioAction);
            ApplyMediaButton(stopVideoButton, stopIcon, cancelingVideo ? "\uE711" : "\uE91B", cancelingVideo ? "キャンセル" : "画像に戻る", stopAction, stopAction);
            playVideoButton.Enabled = !string.IsNullOrWhiteSpace(playVideoButton.Tag as string);

            var playWidth = playbackIcon ? iconWidth : labelWidth;
            var audioWidth = audioIcon ? iconWidth : labelWidth;
            var stopWidth = stopIcon ? iconWidth : labelWidth;
            const int left = 0;
            var y = Math.Max(0, (mediaBar.ClientSize.Height - buttonHeight) / 2);
            playVideoButton.Bounds = new Rectangle(left, y, playWidth, buttonHeight);
            playbackButton.Bounds = playVideoButton.Bounds;
            muteVideoButton.Bounds = new Rectangle(left + playWidth + gap, y, audioWidth, buttonHeight);
            var stopLeft = Math.Max(left + playWidth + gap + audioWidth + groupGap, width - stopWidth);
            stopVideoButton.Bounds = new Rectangle(stopLeft, y, stopWidth, buttonHeight);
        }
        header.Resize += (_, _) => PlaceHeader();
        mediaBar.Resize += (_, _) => PlaceMediaActions();
        playVideoButton.VisibleChanged += (_, _) => PlaceMediaActions();
        playbackButton.VisibleChanged += (_, _) => PlaceMediaActions();
        muteVideoButton.VisibleChanged += (_, _) => PlaceMediaActions();
        stopVideoButton.VisibleChanged += (_, _) => PlaceMediaActions();
        header.DpiChangedAfterParent += (_, _) => PlaceHeader();
        mediaBar.DpiChangedAfterParent += (_, _) => PlaceMediaActions();
        resizeMediaActions = PlaceMediaActions;
        PlaceHeader();
        PlaceMediaActions();
        visualHost.Dock = DockStyle.Fill;
        visualHost.Controls.Add(previewImage); visualHost.Controls.Add(videoHost); visualHost.Controls.Add(videoLoadingOverlay);
        var chromaHeading = NativePageStyle.Label("COLOR VARIANTS", NativePageStyle.SmallBold, Theme.Muted); chromaHeading.Dock = DockStyle.Fill;
        chromaOptions.Dock = DockStyle.Fill; chromaOptions.MinimumSize = new Size(0, UiMetrics.Px(this, UiMetrics.CompactButtonHeight)); chromaOptions.AutoSize = false;
        var levelHeading = NativePageStyle.Label("UPGRADE LEVELS", NativePageStyle.SmallBold, Theme.Muted); levelHeading.Dock = DockStyle.Fill;
        levelOptions.Dock = DockStyle.Fill; levelOptions.MinimumSize = new Size(0, UiMetrics.Px(this, UiMetrics.CompactButtonHeight)); levelOptions.AutoSize = false;
        previewMessage.Dock = DockStyle.Fill;
        layout.Controls.Add(header, 0, 0);
        layout.Controls.Add(mediaBar, 0, 1);
        layout.Controls.Add(visualHost, 0, 2);
        layout.Controls.Add(chromaHeading, 0, 3);
        layout.Controls.Add(chromaOptions, 0, 4);
        layout.Controls.Add(levelHeading, 0, 5);
        layout.Controls.Add(levelOptions, 0, 6);
        layout.Controls.Add(previewMessage, 0, 7);
        panel.Controls.Add(layout);
        void ResizePreview()
        {
            ResizeOptionButtons(chromaOptions);
            ResizeOptionButtons(levelOptions);
            layout.RowStyles[4].Height = OptionPanelHeight(chromaOptions);
            layout.RowStyles[6].Height = OptionPanelHeight(levelOptions);
            layout.RowStyles[7].Height = string.IsNullOrEmpty(previewMessage.Text)
                ? 0 : NativePageStyle.TextHeight(panel, previewMessage.Font, verticalPadding: UiMetrics.TightGap);
            layout.PerformLayout();
        }
        resizePreviewLayout = ResizePreview;
        panel.Resize += (_, _) => ResizePreview();
        previewMessage.TextChanged += (_, _) => ResizePreview();
        chromaOptions.SizeChanged += (_, _) => ResizePreview();
        levelOptions.SizeChanged += (_, _) => ResizePreview();
        ResizePreview();
        return panel;
    }

    private int OptionPanelHeight(FlowLayoutPanel host)
    {
        var buttons = host.Controls.OfType<ThemedButton>().ToArray();
        if (buttons.Length == 0)
            return Math.Max(UiMetrics.Px(this, UiMetrics.CompactButtonHeight), host.Controls.OfType<Label>()
                .Select(label => NativePageStyle.TextHeight(this, label.Font, verticalPadding: UiMetrics.ControlGap))
                .DefaultIfEmpty(UiMetrics.Px(this, UiMetrics.CompactButtonHeight)).Max());
        var available = Math.Max(1, host.ClientSize.Width);
        var columns = OptionColumns(host, available);
        var rowCount = (buttons.Length + columns - 1) / columns;
        var rowHeight = buttons.Max(button => button.Height + button.Margin.Vertical);
        return rowCount * rowHeight + UiMetrics.Px(this, UiMetrics.TightGap);
    }

    // Compact option grid: all four chromas or all five levels fit on one row at typical panel widths.
    private int OptionColumns(FlowLayoutPanel host, int available)
    {
        var maximum = ReferenceEquals(host, levelOptions) ? 5 : 4;
        if (available >= UiMetrics.Px(this, 300)) return maximum;
        if (available >= UiMetrics.Px(this, 220)) return Math.Min(maximum, 3);
        return available >= UiMetrics.Px(this, 140) ? 2 : 1;
    }

    private Size OptionButtonSize(FlowLayoutPanel host, int available)
    {
        var columns = OptionColumns(host, available);
        var margin = UiMetrics.Px(this, UiMetrics.TightGap) * 2;
        var width = Math.Clamp((available - UiMetrics.Px(this, UiMetrics.ControlGap)) / columns - margin, UiMetrics.Px(this, 52), UiMetrics.Px(this, 148));
        return new Size(width, UiMetrics.Px(this, UiMetrics.CompactButtonHeight));
    }

    private void WireEvents()
    {
        storeTab.Click += (_, _) => ShowStorePage();
        catalogTab.Click += (_, _) => ShowCatalogPage();
        refreshStoreButton.Click += async (_, _) => await LoadStorefrontAsync(force: true);
        refreshCatalogButton.Click += async (_, _) => await LoadCatalogAsync(refresh: true);
        loginButton.Click += async (_, _) =>
        {
            if (shellAuthentication)
            {
                SetStatus("ブラウザーが開いたらRiotにログインしてください。", false);
                LoginRequested?.Invoke(this, EventArgs.Empty);
            }
            else await SignInAsync();
        };
        logoutButton.Click += (_, _) =>
        {
            if (shellAuthentication) LogoutRequested?.Invoke(this, EventArgs.Empty);
            else { riot.Logout(); ResetSession(); }
        };
        searchBox.TextChanged += (_, _) => { if (!changingFilters) { catalogPageIndex = 0; RenderCatalogGrid(); } };
        categorySelect.SelectedIndexChanged += (_, _) => { if (!changingFilters) { catalogPageIndex = 0; RenderCatalogGrid(); } };
        weaponSelect.SelectedIndexChanged += (_, _) => { if (!changingFilters) { catalogPageIndex = 0; RenderCatalogGrid(); } };
        playVideoButton.Click += async (_, _) => await PlayPreviewVideoAsync(lifetime.Token);
        playbackButton.Click += (_, _) => ToggleVideoPlayback();
        muteVideoButton.Click += (_, _) => ToggleVideoMute();
        stopVideoButton.Click += (_, _) => ShowPreviewImage();
        VisibleChanged += (_, _) =>
        {
            if (Visible)
            {
                LayoutStoreGrids();
                UpdateCountdowns();
                countdownTimer.Start();
            }
            else
            {
                countdownTimer.Stop();
                if (!Disposing && !IsDisposed) ShowPreviewImage();
            }
        };
    }

    // The grids lay themselves out on resize; this forces a pass after content or visibility changes
    // (e.g. a section or the whole page becoming visible after being hidden at a stale size).
    private void LayoutStoreGrids()
    {
        storeSections.PerformLayout();
        dailyOffers.PerformLayout(); nightOffers.PerformLayout(); bundleCards.PerformLayout(); accessoryCards.PerformLayout();
    }

    private bool switchingCatalogPage;

    private void ShowStoreSection(string key)
    {
        if (ActiveStoreSection() == key) return;
        var next = storeSections.Controls.Cast<Control>().FirstOrDefault(section => Equals(section.Tag, key));
        if (next is null) return;
        using var redraw = PageRedrawScope.Begin(storeSections);
        next.BringToFront();
        next.Show();
        foreach (Control section in storeSections.Controls)
            if (section != next) section.Hide();
        dailySectionTab.Selected = key == "daily";
        nightSectionTab.Selected = key == "night";
        bundleSectionTab.Selected = key == "bundle";
        accessorySectionTab.Selected = key == "accessories";
        LayoutStoreGrids();
    }

    private Task LoadCatalogAsync(bool refresh)
    {
        if (IsDisposed) return Task.CompletedTask;
        if (loadingCatalog) return catalogLoadTask ?? Task.CompletedTask;
        loadingCatalog = true;
        refreshCatalogButton.Enabled = false;
        if (!catalogLoaded) RenderCatalogGrid();
        catalogLoadTask = LoadCatalogCoreAsync(refresh);
        return catalogLoadTask;
    }

    private async Task LoadCatalogCoreAsync(bool refresh)
    {
        try
        {
            catalogSnapshot = await catalog.LoadAsync(refresh, lifetime.Token);
            allSkins = catalogSnapshot.Weapons.SelectMany(w => w.Skins).ToArray();
            catalogTextMetricsReady = false;
            catalogCardTitleHeight = catalogCardSubtitleHeight = 0;
            catalogMetricsGeneration++;
            await MeasureCatalogTextHeightsAsync();
            if (IsDisposed || Disposing || lifetime.IsCancellationRequested) return;
            RebuildRewardIndex();
            UpdateCatalogCounts();
            RebuildFilterLists();
            catalogLoaded = true;
            if (catalogTab.Selected) RenderCatalogGrid();
            RenderStorefront();
            SetStatus("", false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            catalogLoadMessage = "武器カタログを読み込めませんでした。ネット接続を確認して再試行してください。";
            NativePageStyle.LogPageError("StoreCatalog", ex);
            if (!disposingStore && !IsDisposed && !Disposing) SetStatus(catalogLoadMessage, true);
        }
        finally
        {
            loadingCatalog = false;
            if (!disposingStore && !IsDisposed && !Disposing)
            {
                refreshCatalogButton.Enabled = true;
                if (!catalogLoaded) RenderCatalogGrid();
            }
        }
    }

    private async Task LoadStorefrontAsync(bool force = false)
    {
        if (!riot.HasAccountSession)
        {
            storefront = null;
            RenderStorefront();
            UpdateAccountState();
            return;
        }
        if (loadingStore || IsDisposed) return;
        var generation = storeGeneration;
        using var operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        activeStoreLoad = operation;
        loadingStore = true;
        refreshStoreButton.Enabled = false;
        var progressMessage = force ? "今日のショップを更新しています…" : "今日のショップを取得しています…";
        SetStatus(progressMessage, false);
        if (storefront is null) RenderStorefront();
        try
        {
            using var response = await riot.GetStorefrontResponse(operation.Token);
            var next = StorefrontParser.Parse(response.RootElement);
            if (generation != storeGeneration || IsDisposed) return;
            storefront = next;
            storefrontFetchedAt = DateTimeOffset.UtcNow;
            RenderStorefront();
            SetStatus("", false);
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested) { }
        catch (OperationCanceledException)
        {
            if (generation == storeGeneration && !IsDisposed && !Disposing) SetStatus("今日のショップの取得がタイムアウトしました。再度更新してください。", true);
        }
        catch (ApiException ex) when (ex.Status == 429)
        {
            if (generation == storeGeneration && !IsDisposed && !Disposing) SetStatus("ショップの更新が集中しています。少し待ってから再試行してください。", true);
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException or InvalidDataException or JsonException or InvalidOperationException)
        {
            NativePageStyle.LogPageError("Storefront", ex);
            if (generation == storeGeneration && !IsDisposed && !Disposing) SetStatus("今日のショップを取得できませんでした。VALORANTのアカウント接続を確認してください。", true);
        }
        catch (Exception ex)
        {
            NativePageStyle.LogPageError("Storefront", ex);
            if (generation == storeGeneration && !IsDisposed && !Disposing) SetStatus("今日のショップを表示できませんでした。再度更新してください。", true);
        }
        finally
        {
            if (ReferenceEquals(activeStoreLoad, operation)) activeStoreLoad = null;
            if (generation == storeGeneration && !disposingStore && !IsDisposed && !Disposing)
            {
                loadingStore = false;
                refreshStoreButton.Enabled = true;
                EndProgress(progressMessage);
                UpdateAccountState();
                if (storefront is null) RenderStorefront();
            }
        }
    }

    private async Task SignInAsync()
    {
        if (signingIn || IsDisposed) return;
        signingIn = true; loginButton.Enabled = false;
        SetStatus(SignInStatus, false);
        try
        {
            var owner = FindForm();
            if (owner is null) return;
            loginWindow ??= new LoginWindow();
            var auth = await loginWindow.Authenticate(owner, silentLogin: false, lifetime.Token);
            if (auth is null) return;
            await riot.Login(auth, "auto", lifetime.Token);
            UpdateAccountState();
            await LoadStorefrontAsync();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (OperationCanceledException) { SetStatus("ログインがキャンセルされました。", true); }
        catch (ApiException ex) when (ex.Status == 429) { SetStatus("Riot側の応答が混み合っています。少し待ってから再試行してください。", true); }
        catch (Exception ex) when (ex is ApiException or HttpRequestException or InvalidDataException or JsonException or InvalidOperationException)
        { SetStatus("Riotアカウントへ接続できませんでした。地域を確認して再試行してください。", true); }
        finally { signingIn = false; if (!IsDisposed) { loginButton.Enabled = true; UpdateAccountState(); EndProgress(SignInStatus); } }
    }

    private void UpdateAccountState()
    {
        accountLabel.Text = riot.HasAccountSession
            ? shellAuthentication ? $"接続地域 · {riot.RegionLabel}" : $"{riot.AccountName}\n{riot.RegionLabel}"
            : shellAuthentication ? "" : "Riotアカウント未接続";
        // Inside the app shell the header already shows the account and logout, so this page only adds a
        // direct login while signed out (the shell runs the login) instead of pointing at the header.
        accountLabel.Visible = !shellAuthentication || riot.HasAccountSession;
        loginButton.Visible = !riot.HasAccountSession;
        loginButton.Primary = !riot.HasAccountSession;
        logoutButton.Visible = !shellAuthentication && riot.HasAccountSession;
        refreshStoreButton.Enabled = riot.HasAccountSession && !loadingStore;
    }

    private void RenderStorefront()
    {
        if (IsDisposed || Disposing) return;
        UpdateCountdowns();
        using var redraw = PageRedrawScope.Begin(storePage);
        var grids = new[] { dailyOffers, nightOffers, bundleCards, accessoryCards };
        foreach (var grid in grids) grid.SuspendLayout();
        try
        {
        DisposeFlowChildren(dailyOffers); DisposeFlowChildren(nightOffers); DisposeFlowChildren(bundleCards); DisposeFlowChildren(accessoryCards);
        bundleDetails.Clear();
        // Stale card images are cancelled when their cards are replaced.
        storeImageLoads?.Cancel(); storeImageLoads?.Dispose();
        storeImageLoads = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        if (storefront is null)
        {
            SetSectionVisibility("night", false); SetSectionVisibility("bundle", false); SetSectionVisibility("accessories", false);
            dailySkeleton.Loading = loadingStore && riot.HasAccountSession;
            if (dailySkeleton.Loading) return;
            dailyOffers.Controls.Add(CreateEmptyMessage(riot.HasAccountSession
                ? "今日のショップを取得できませんでした。更新して再試行してください。"
                : shellAuthentication ? "Riotアカウントにログインすると、今日のショップを表示します。" : "Riotアカウントに接続すると、今日のショップを表示します。", 500, 110));
            return;
        }

        foreach (var offer in storefront.DailySkins.Offers) dailyOffers.Controls.Add(CreateOfferCard(offer, night: false));
        if (storefront.DailySkins.Offers.Count == 0) dailyOffers.Controls.Add(CreateEmptyMessage("今日の武器オファーはありません。", 240, 110));
        var night = storefront.NightMarket;
        SetSectionVisibility("night", night is not null);
        if (night is not null)
        {
            foreach (var offer in night.Offers) nightOffers.Controls.Add(CreateOfferCard(offer, night: true));
            if (night.Offers.Count == 0) nightOffers.Controls.Add(CreateEmptyMessage("ナイトマーケットのオファーはありません。", 240, 110));
        }
        SetSectionVisibility("bundle", storefront.FeaturedBundles.Bundles.Count > 0);
        foreach (var bundle in storefront.FeaturedBundles.Bundles) bundleCards.Controls.Add(CreateBundleCard(bundle));
        SetSectionVisibility("accessories", storefront.Accessories.Offers.Count > 0);
        foreach (var accessory in storefront.Accessories.Offers) accessoryCards.Controls.Add(CreateAccessoryCard(accessory));
        LayoutStoreGrids();
        dailySkeleton.Loading = false;
        }
        finally { foreach (var grid in grids) grid.ResumeLayout(true); }
    }

    private Control CreateOfferCard(StorefrontOffer offer, bool night)
    {
        var found = FindSkinForReward(offer.ItemId);
        var skin = found?.Skin;
        var card = CreateStoreCard();
        card.Cursor = Cursors.Hand;
        var titleText = skin?.Name ?? ItemTypeName(offer.ItemTypeId);
        var title = NativePageStyle.Label(titleText, NativePageStyle.BodyBold);
        title.AutoSize = false; title.AutoEllipsis = false; title.Dock = DockStyle.Top;
        var discountPercent = night ? offer.DiscountPercent : null;
        title.Padding = new Padding(0, 0, discountPercent is not null ? UiMetrics.Px(this, 58) : 0, 0);
        // The badge lives inside the title label's reserved right padding, so it stays in the title row.
        Label? discountLabel = null;
        if (discountPercent is { } discount)
        {
            discountLabel = NativePageStyle.Label($"−{discount:0}%", NativePageStyle.SmallBold, Theme.Red);
            discountLabel.AutoEllipsis = false;
            discountLabel.AutoSize = false;
            discountLabel.TextAlign = ContentAlignment.TopRight;
            discountLabel.Cursor = Cursors.Hand;
            title.Controls.Add(discountLabel);
            var badge = discountLabel;
            void PlaceBadge()
            {
                var width = UiMetrics.Px(this, 54);
                var height = Math.Min(Math.Max(1, title.ClientSize.Height), NativePageStyle.TextHeight(this, badge.Font, verticalPadding: 4));
                badge.Bounds = new Rectangle(Math.Max(0, title.ClientSize.Width - width), 0, width, height);
            }
            title.SizeChanged += (_, _) => PlaceBadge();
            PlaceBadge();
        }
        FitLabelHeightToCard(card, title);
        var offerImageCandidates = found is { } offerMatch
            ? ArtCandidates(offerMatch.Skin, 0, offerMatch.Level)
            : Enumerable.Empty<string?>();
        var image = new SharpImageBox
        {
            Dock = DockStyle.Fill,
            Fit = ImageFit.Contain,
            RoundedCorners = true,
            Cursor = Cursors.Hand,
            PlaceholderText = "画像なし"
        };
        var weapon = NativePageStyle.Label(skin?.Weapon ?? "詳細データなし", NativePageStyle.Reading, Theme.Muted); weapon.AutoSize = false; weapon.AutoEllipsis = false; weapon.Dock = DockStyle.Bottom; weapon.Height = NativePageStyle.TextHeight(this, weapon.Font, verticalPadding: 4);
        var price = NativePageStyle.Label(OfferPriceText(offer, night), NativePageStyle.SmallBold, Theme.Text); price.AutoSize = false; price.Dock = DockStyle.Bottom; price.Height = NativePageStyle.TextHeight(this, price.Font, verticalPadding: 4);
        card.Controls.Add(image); card.Controls.Add(price); card.Controls.Add(weapon); card.Controls.Add(title);
        void Select(object? _, EventArgs __) => SelectOffer(offer.ItemId);
        card.Click += Select; title.Click += Select; image.Click += Select; weapon.Click += Select; price.Click += Select;
        if (discountLabel is not null) discountLabel.Click += Select;
        _ = LoadStoreImageAsync(image, offerImageCandidates, StoreImageToken);
        return card;
    }

    private Control CreateBundleCard(StorefrontBundle bundle)
    {
        var card = CreateStoreCard();
        var name = catalogSnapshot?.Bundles.TryGetValue(bundle.DataAssetId, out var info) == true ? info.Name : "注目バンドル";
        var bannerUrls = catalogSnapshot?.Bundles.TryGetValue(bundle.DataAssetId, out info) == true ? info.Images : Array.Empty<string>();
        var title = NativePageStyle.Label(name, NativePageStyle.BodyBold); title.AutoSize = false; title.AutoEllipsis = false; title.Dock = DockStyle.Top;
        FitLabelHeightToCard(card, title);
        // Banner art fills the card's image area edge to edge (cropped evenly, never stretched).
        var image = new SharpImageBox { Dock = DockStyle.Fill, Fit = ImageFit.Cover, PlaceholderText = "画像なし", RoundedCorners = true };
        var items = bundle.Items.Where(item => item.ItemTypeId.Equals(SkinLevelType, StringComparison.OrdinalIgnoreCase)).ToArray();
        var detail = NativePageStyle.Label(BundleDetailText(bundle), NativePageStyle.Small, Theme.Muted);
        detail.AutoSize = false; detail.AutoEllipsis = false; detail.Dock = DockStyle.Bottom;
        FitLabelHeightToCard(card, detail);
        card.Controls.Add(image); card.Controls.Add(detail); card.Controls.Add(title);
        bundleDetails.Add((bundle, detail));
        _ = LoadStoreImageAsync(image, bannerUrls, StoreImageToken);
        return card;
    }

    // Uses Remaining() so the time counts down from when the storefront was fetched, like the section timers.
    private string BundleDetailText(StorefrontBundle bundle)
    {
        var skinCount = bundle.Items.Count(item => item.ItemTypeId.Equals(SkinLevelType, StringComparison.OrdinalIgnoreCase));
        return $"{skinCount} 個の武器スキン  ·  {Money(bundle.TotalDiscountedCost ?? bundle.TotalBaseCost)}\n終了まで {Remaining(bundle.RemainingSeconds)}";
    }

    private Control CreateAccessoryCard(StorefrontOffer offer)
    {
        StoreItemCard? item = null;
        if (catalogSnapshot is { } snapshot && snapshot.Accessories.TryGetValue(offer.ItemId, out var matchedItem)) item = matchedItem;
        var name = item?.Name ?? $"アクセサリー · {offer.ItemId[..Math.Min(8, offer.ItemId.Length)]}";
        var card = CreateStoreCard();
        var image = new SharpImageBox { Dock = DockStyle.Fill, Fit = ImageFit.Contain, PlaceholderText = "画像なし", RoundedCorners = true };
        var accessoryText = $"{name}\n{item?.Type ?? ItemTypeName(offer.ItemTypeId, true)}  ·  {Money(offer.Cost)}";
        var text = NativePageStyle.Label(accessoryText, NativePageStyle.Reading, Theme.Text); text.AutoSize = false; text.AutoEllipsis = false; text.Dock = DockStyle.Bottom;
        FitLabelHeightToCard(card, text);
        card.Controls.Add(image); card.Controls.Add(text);
        _ = LoadStoreImageAsync(image, item?.Images ?? Array.Empty<string>(), StoreImageToken);
        return card;
    }

    private CancellationToken StoreImageToken => storeImageLoads?.Token ?? lifetime.Token;

    private RoundedCard CreateStoreCard()
    {
        // Bounds are assigned by the owning ResponsiveCardGrid.
        var card = NativePageStyle.Card();
        card.Dock = DockStyle.None; card.Margin = Padding.Empty; card.Padding = new Padding(UiMetrics.Px(this, 9));
        return card;
    }

    // Keeps a docked label tall enough for its wrapped text at the card's current width.
    private void FitLabelHeightToCard(Control card, Label label)
    {
        void Fit()
        {
            var width = card.ClientSize.Width - card.Padding.Horizontal - label.Padding.Horizontal;
            if (width <= 0) return;
            var height = NativePageStyle.TextBlockHeight(this, label.Text, label.Font, width, verticalPadding: 4);
            if (label.Height != height) label.Height = height;
        }
        card.SizeChanged += (_, _) => Fit();
        Fit();
    }

    private void RenderCatalogGrid()
    {
        if (IsDisposed) return;
        using var redraw = PageRedrawScope.Begin(catalogGridHost);
        if (!catalogLoaded)
        {
            catalogSkeleton.Loading = loadingCatalog;
            if (loadingCatalog)
            {
                foreach (var loadingLabel in catalogGrid.Controls.OfType<Label>().ToArray())
                {
                    catalogGrid.Controls.Remove(loadingLabel);
                    loadingLabel.Dispose();
                }
                return;
            }
            var message = catalogGrid.Controls.OfType<Label>().FirstOrDefault();
            if (message is null)
            {
                message = NativePageStyle.Label(catalogLoadMessage, NativePageStyle.Reading, Theme.Muted);
                message.AutoSize = false;
                message.TextAlign = ContentAlignment.MiddleCenter;
                catalogGrid.Controls.Add(message);
            }
            message.Text = catalogLoadMessage;
            message.Size = new Size(
                Math.Max(UiMetrics.Px(this, 200), catalogGrid.ClientSize.Width - catalogGrid.Padding.Horizontal - UiMetrics.Px(this, 10)),
                Math.Max(UiMetrics.Px(this, 70), catalogGrid.ClientSize.Height - catalogGrid.Padding.Vertical - UiMetrics.Px(this, 10)));
            return;
        }
        var filtered = FilteredSkins().ToArray();
        UpdateCatalogPageSize(renderAfterChange: false, filtered);
        var selected = filtered.FirstOrDefault(s => s.Id.Equals(selectedSkin?.Id, StringComparison.OrdinalIgnoreCase));
        if (selected is null) { selectedSkin = filtered.FirstOrDefault(); selectedChroma = selectedLevel = 0; }
        else selectedSkin = selected;
        if (catalogEnsureSelectedVisible && selectedSkin is not null)
        {
            var selectedIndex = Array.FindIndex(filtered, s => s.Id.Equals(selectedSkin.Id, StringComparison.OrdinalIgnoreCase));
            if (selectedIndex >= 0) catalogPageIndex = selectedIndex / Math.Max(1, catalogPageSize);
            catalogEnsureSelectedVisible = false;
        }
        var pageCount = Math.Max(1, (int)Math.Ceiling(filtered.Length / (double)Math.Max(1, catalogPageSize)));
        catalogPageIndex = Math.Clamp(catalogPageIndex, 0, pageCount - 1);
        var page = filtered.Skip(catalogPageIndex * catalogPageSize).Take(catalogPageSize).ToArray();
        var cards = catalogGrid.Controls.Cast<Control>().ToArray();
        catalogGrid.SuspendLayout();
        try
        {
            foreach (var card in cards)
                if (card.Tag is not SkinCard skin || !page.Contains(skin, ReferenceEqualityComparer.Instance))
                    card.Dispose();
            for (var i = 0; i < page.Length; i++)
            {
                var skin = page[i];
                var card = cards.FirstOrDefault(c => ReferenceEquals(c.Tag, skin));
                if (card is null)
                {
                    card = CreateSkinCard(skin);
                    SizeCatalogCard(card);
                    catalogGrid.Controls.Add(card);
                }
                SizeCatalogCard(card);
                if (catalogGrid.Controls.GetChildIndex(card) != i)
                    catalogGrid.Controls.SetChildIndex(card, i);
            }
        }
        finally { catalogGrid.ResumeLayout(); }
        UpdateCatalogSelection();
        previousCatalogPage.Enabled = catalogPageIndex > 0;
        nextCatalogPage.Enabled = catalogPageIndex + 1 < pageCount;
        catalogPageLabel.Text = $"{catalogPageIndex + 1} / {pageCount}  ·  {filtered.Length:N0} 件";
        catalogSkeleton.Loading = false;
    }

    private void UpdateCatalogPageSize(bool renderAfterChange = true, IReadOnlyList<SkinCard>? filtered = null)
    {
        if (catalogGrid.ClientSize.Width <= 0 || catalogGrid.ClientSize.Height <= 0) return;
        var availableWidth = Math.Max(0, catalogGrid.ClientSize.Width - catalogGrid.Padding.Horizontal);
        var availableHeight = Math.Max(0, catalogGrid.ClientSize.Height - catalogGrid.Padding.Vertical);
        var cardMargin = UiMetrics.Px(this, UiMetrics.TightGap);
        var cardGap = cardMargin * 2;
        var cardsWidth = Math.Max(0, availableWidth - cardMargin * 2);
        var cardWidth = UiMetrics.Px(this, 220);
        var columns = Math.Clamp((cardsWidth + cardGap) / Math.Max(1, cardWidth + cardGap), 1, 6);
        var nextCardWidth = Math.Max(1, (cardsWidth - cardGap * (columns - 1)) / columns);
        var widthChanged = catalogCardWidth != nextCardWidth;
        catalogCardWidth = nextCardWidth;
        var oldImageHeight = catalogCardImageHeight;
        var oldTitleHeight = catalogCardTitleHeight;
        var oldSubtitleHeight = catalogCardSubtitleHeight;
        var cardHeight = CatalogCardFlowHeight();
        var rows = Math.Max(1, availableHeight / Math.Max(1, cardHeight));
        var nextSize = columns * rows;
        var cardSizeChanged = oldImageHeight != catalogCardImageHeight || oldTitleHeight != catalogCardTitleHeight || oldSubtitleHeight != catalogCardSubtitleHeight;
        if (nextSize == catalogPageSize && !cardSizeChanged)
        {
            if (widthChanged)
            {
                using var redraw = PageRedrawScope.Begin(catalogGridHost);
                catalogGrid.SuspendLayout();
                try
                {
                    foreach (Control card in catalogGrid.Controls)
                        if (card.Tag is SkinCard) SizeCatalogCard(card);
                }
                finally { catalogGrid.ResumeLayout(true); }
            }
            return;
        }
        var previousFirstVisible = catalogPageIndex * catalogPageSize;
        var selectedIndexBeforeResize = -1;
        if (selectedSkin is { } current)
        {
            var skins = filtered ?? FilteredSkins().ToArray();
            for (var i = 0; i < skins.Count; i++)
                if (skins[i].Id.Equals(current.Id, StringComparison.OrdinalIgnoreCase)) { selectedIndexBeforeResize = i; break; }
        }
        var selectedWasVisible = selectedIndexBeforeResize >= previousFirstVisible && selectedIndexBeforeResize < previousFirstVisible + catalogPageSize;
        catalogPageSize = nextSize;
        if ((catalogEnsureSelectedVisible || selectedWasVisible) && selectedIndexBeforeResize >= 0)
        {
            catalogPageIndex = selectedIndexBeforeResize / nextSize;
            catalogEnsureSelectedVisible = false;
        }
        else catalogPageIndex = previousFirstVisible / nextSize;
        if (renderAfterChange && catalogLoaded) RenderCatalogGrid();
    }

    private Control CreateSkinCard(SkinCard skin)
    {
        var subtitleText = $"{skin.Weapon}\n{skin.Tier}";
        var card = new Panel { Tag = skin, BackColor = Theme.Surface, Margin = new Padding(UiMetrics.Px(this, UiMetrics.TightGap)), BorderStyle = BorderStyle.FixedSingle, Cursor = Cursors.Hand };
        var tierColor = TierColor(skin.TierColor);
        var tierBar = new Panel { Name = CatalogTierBarName, Dock = DockStyle.Top, Height = CatalogTierBarHeight(), BackColor = tierColor, Margin = Padding.Empty, Cursor = Cursors.Hand };
        var image = new SharpImageBox { Name = CatalogImageName, Dock = DockStyle.Top, BackColor = Theme.Elevated, Cursor = Cursors.Hand, PlaceholderText = "画像なし" };
        var title = NativePageStyle.Label(skin.Name, NativePageStyle.SmallBold, Theme.Text); title.Name = CatalogTitleName; title.AutoSize = false; title.AutoEllipsis = true; title.Dock = DockStyle.Top; title.Padding = new Padding(UiMetrics.Px(this, UiMetrics.TightGap), 0, UiMetrics.Px(this, UiMetrics.TightGap), 0); title.Cursor = Cursors.Hand;
        var subtitle = NativePageStyle.Label(subtitleText, NativePageStyle.Small, Theme.Muted); subtitle.Name = CatalogSubtitleName; subtitle.AutoSize = false; subtitle.AutoEllipsis = true; subtitle.Dock = DockStyle.Top; subtitle.Padding = new Padding(UiMetrics.Px(this, UiMetrics.TightGap), 0, UiMetrics.Px(this, UiMetrics.TightGap), 0); subtitle.Cursor = Cursors.Hand;
        // Top-docked children stack in reverse add order: tier bar, image, title, subtitle.
        card.Controls.Add(subtitle); card.Controls.Add(title); card.Controls.Add(image); card.Controls.Add(tierBar);
        void Select(object? _, EventArgs __) { selectedSkin = skin; selectedChroma = selectedLevel = 0; UpdateCatalogSelection(); }
        card.Click += Select; image.Click += Select; title.Click += Select; subtitle.Click += Select; tierBar.Click += Select;
        foreach (Control part in new Control[] { card, image, title, subtitle, tierBar })
            part.MouseWheel += CatalogMouseWheel;
        var cancellation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        card.Disposed += (_, _) =>
        {
            cancellation.Cancel();
            cancellation.Dispose();
        };
        _ = LoadStoreImageAsync(image, ArtCandidates(skin, 0, 0), cancellation.Token);
        return card;
    }

    private void SizeCatalogCard(Control card)
    {
        var imageHeight = Math.Max(UiMetrics.Px(this, 58), catalogCardImageHeight);
        var titleHeight = catalogCardTitleHeight > 0 ? catalogCardTitleHeight : NativePageStyle.TextHeight(this, NativePageStyle.SmallBold, lines: 2, verticalPadding: UiMetrics.TightGap);
        var subtitleHeight = catalogCardSubtitleHeight > 0 ? catalogCardSubtitleHeight : NativePageStyle.TextHeight(this, NativePageStyle.Small, lines: 2, verticalPadding: UiMetrics.TightGap);
        var barHeight = CatalogTierBarHeight();
        if (card.Controls[CatalogSubtitleName] is { } subtitle) subtitle.Height = subtitleHeight;
        if (card.Controls[CatalogTitleName] is { } title) title.Height = titleHeight;
        if (card.Controls[CatalogImageName] is { } image) image.Height = imageHeight;
        if (card.Controls[CatalogTierBarName] is { } bar) bar.Height = barHeight;
        card.Size = new Size(Math.Max(1, catalogCardWidth), barHeight + imageHeight + titleHeight + subtitleHeight + UiMetrics.Px(this, UiMetrics.TightGap));
    }

    private const string CatalogTierBarName = "tierBar";
    private const string CatalogImageName = "image";
    private const string CatalogTitleName = "title";
    private const string CatalogSubtitleName = "subtitle";

    private int CatalogTierBarHeight() => Math.Max(2, UiMetrics.Px(this, 3));

    private static Color TierColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return Theme.Muted;
        try
        {
            var color = ColorTranslator.FromHtml(value.Trim());
            return color.IsEmpty ? Theme.Muted : Color.FromArgb(255, color);
        }
        catch (Exception ex) when (ex is ArgumentException or FormatException) { return Theme.Muted; }
    }

    // One wheel notch turns one catalog page; the grid never scrolls, so the wheel has no other use here.
    private void CatalogMouseWheel(object? sender, MouseEventArgs e)
    {
        if (!catalogLoaded || e.Delta == 0) return;
        if (e is HandledMouseEventArgs handled) handled.Handled = true;
        if (e.Delta > 0)
        {
            if (!previousCatalogPage.Enabled || catalogPageIndex <= 0) return;
            catalogPageIndex--;
        }
        else
        {
            if (!nextCatalogPage.Enabled) return;
            catalogPageIndex++;
        }
        // Re-render after the wheel message returns: the new page disposes the card that raised it.
        if (IsHandleCreated) BeginInvoke(() => { if (!IsDisposed) RenderCatalogGrid(); });
        else RenderCatalogGrid();
    }

    protected override void OnDpiChangedAfterParent(EventArgs e)
    {
        base.OnDpiChangedAfterParent(e);
        catalogMetricsGeneration++;
        catalogMetricsStop?.Cancel();
        catalogTextMetricsReady = false;
        catalogCardTitleHeight = catalogCardSubtitleHeight = 0;
        if (catalogLoaded)
        {
            _ = MeasureCatalogTextHeightsAsync();
            UpdateCatalogPageSize();
        }
    }

    private void UpdateCatalogSelection()
    {
        foreach (Control card in catalogGrid.Controls)
            card.BackColor = card.Tag is SkinCard skin && skin.Id.Equals(selectedSkin?.Id, StringComparison.OrdinalIgnoreCase)
                ? Theme.CatalogHighlight : Theme.Surface;
        RenderPreview();
    }

    private int CatalogCardFlowHeight()
    {
        var minimumImageHeight = UiMetrics.Px(this, 58);
        var titleHeight = catalogCardTitleHeight > 0 ? catalogCardTitleHeight : NativePageStyle.TextHeight(this, NativePageStyle.SmallBold, lines: 2, verticalPadding: UiMetrics.TightGap);
        var subtitleHeight = catalogCardSubtitleHeight > 0 ? catalogCardSubtitleHeight : NativePageStyle.TextHeight(this, NativePageStyle.Small, lines: 2, verticalPadding: UiMetrics.TightGap);
        var marginHeight = UiMetrics.Px(this, UiMetrics.ControlGap);
        var chromeHeight = CatalogTierBarHeight() + titleHeight + subtitleHeight + UiMetrics.Px(this, UiMetrics.TightGap);
        var minimumCardHeight = minimumImageHeight + chromeHeight;
        var availableHeight = Math.Max(1, catalogGrid.ClientSize.Height - catalogGrid.Padding.Vertical);
        var rows = Math.Max(1, availableHeight / Math.Max(1, minimumCardHeight + marginHeight));
        var targetCardHeight = Math.Max(minimumCardHeight, availableHeight / rows - marginHeight);
        catalogCardImageHeight = Math.Max(minimumImageHeight, targetCardHeight - chromeHeight);
        return catalogCardImageHeight + chromeHeight + marginHeight;
    }

    private async Task MeasureCatalogTextHeightsAsync()
    {
        if (catalogTextMetricsReady || allSkins.Count == 0 || IsDisposed || Disposing) return;
        // Text rows are capped at two lines (longer names ellipsize) so one unusually long skin name
        // cannot shrink the image area of every card in the catalog. Measure off the UI thread.
        catalogMetricsStop?.Cancel();
        var operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        catalogMetricsStop = operation;
        var generation = ++catalogMetricsGeneration;
        var skins = allSkins;
        var dpi = DeviceDpi;
        var textWidth = Math.Max(1, UiMetrics.Px(this, 210) - UiMetrics.Px(this, 20));
        var verticalPadding = UiMetrics.Px(this, UiMetrics.TightGap);
        var titleCap = NativePageStyle.TextHeight(this, NativePageStyle.SmallBold, lines: 2, verticalPadding: UiMetrics.TightGap);
        var subtitleCap = NativePageStyle.TextHeight(this, NativePageStyle.Small, lines: 2, verticalPadding: UiMetrics.TightGap);
        using var titleFont = (Font)NativePageStyle.SmallBold.Clone();
        using var subtitleFont = (Font)NativePageStyle.Small.Clone();
        try
        {
            var measured = await Task.Run(() =>
            {
                var titleHeight = 0;
                var subtitleHeight = 0;
                foreach (var skin in skins)
                {
                    operation.Token.ThrowIfCancellationRequested();
                    titleHeight = Math.Max(titleHeight, MeasureTextBlockHeight(skin.Name, titleFont, textWidth, verticalPadding));
                    subtitleHeight = Math.Max(subtitleHeight, MeasureTextBlockHeight($"{skin.Weapon}\n{skin.Tier}", subtitleFont, textWidth, verticalPadding));
                    if (titleHeight >= titleCap && subtitleHeight >= subtitleCap) break;
                }
                return (Title: Math.Min(titleCap, titleHeight), Subtitle: Math.Min(subtitleCap, subtitleHeight));
            }, operation.Token);
            if (generation != catalogMetricsGeneration || dpi != DeviceDpi || !ReferenceEquals(skins, allSkins) || IsDisposed || Disposing) return;
            catalogCardTitleHeight = measured.Title;
            catalogCardSubtitleHeight = measured.Subtitle;
            catalogTextMetricsReady = true;
            if (catalogLoaded && catalogPage.Visible) UpdateCatalogPageSize();
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested) { }
        finally
        {
            if (ReferenceEquals(catalogMetricsStop, operation)) catalogMetricsStop = null;
            operation.Dispose();
        }
    }

    private static int MeasureTextBlockHeight(string text, Font font, int width, int verticalPadding)
    {
        var measured = TextRenderer.MeasureText(text, font, new Size(Math.Max(1, width), 100_000),
            TextFormatFlags.WordBreak | TextFormatFlags.NoPadding);
        return Math.Max(1, measured.Height) + verticalPadding;
    }

    private void RenderPreview()
    {
        var skin = selectedSkin;
        var chromas = skin?.Chromas ?? [];
        var levels = skin?.Levels ?? [];
        selectedChroma = Math.Clamp(selectedChroma, 0, Math.Max(0, chromas.Count - 1));
        selectedLevel = Math.Clamp(selectedLevel, 0, Math.Max(0, levels.Count - 1));
        var nextPreview = (skin, selectedChroma, selectedLevel);
        if (displayedPreview == nextPreview) return;
        using var redraw = PageRedrawScope.Begin(previewPanel);
        displayedPreview = nextPreview;

        previewGeneration++;
        var generation = previewGeneration;
        previewImageLoad?.Cancel();
        previewImageLoad?.Dispose();
        previewImageLoad = null;
        ShowPreviewImage();
        previewImage.Loading = false;
        previewImage.Image = null;
        if (skin is null)
        {
            previewImage.PlaceholderText = "";
            catalogTitle.Text = "スキンを選ぶと、画像・動画・カラーバリエーションを表示します。";
            catalogMeta.Text = ""; previewMessage.Text = "条件に一致するスキンがありません。";
            playVideoButton.Tag = null;
            playVideoButton.Visible = true;
            playVideoButton.Enabled = false;
            DisposeFlowChildren(chromaOptions); DisposeFlowChildren(levelOptions);
            resizeMediaActions?.Invoke();
            return;
        }

        catalogTitle.Text = skin.Name;
        previewImage.PlaceholderText = "画像なし";
        catalogMeta.Text = $"{skin.Weapon}  ·  {skin.Tier}  ·  {CategoryLabel(CategoryKey(skin.Category))}";
        previewMessage.Text = "";
        var imageUrls = ArtCandidates(skin, selectedChroma, selectedLevel);
        previewImageLoad = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        _ = LoadPreviewImageAsync(imageUrls, generation, previewImageLoad.Token);
        BuildOptionButtons(chromaOptions, chromas.Select((item, index) => VariantLabel(skin.Name, item.Name, "カラー", index)).ToArray(),
            selectedChroma, index => { selectedChroma = index; RenderPreview(); }, chromas.Select(item => item.Swatch).ToArray(), generation, previewImageLoad.Token);
        BuildOptionButtons(levelOptions, levels.Select((item, index) => VariantLabel(skin.Name, item.Name, "レベル", index)).ToArray(), selectedLevel, index => { selectedLevel = index; RenderPreview(); });
        resizePreviewLayout?.Invoke();
        var video = SelectedVideo(skin, selectedChroma, selectedLevel);
        playVideoButton.Tag = video;
        playVideoButton.Visible = true;
        playVideoButton.Enabled = !string.IsNullOrWhiteSpace(video);
        stopVideoButton.Visible = videoHost.Visible;
        resizeMediaActions?.Invoke();
    }

    private void BuildOptionButtons(FlowLayoutPanel host, IReadOnlyList<string> labels, int selected, Action<int> select,
        IReadOnlyList<string?>? swatches = null, int generation = 0, CancellationToken cancellationToken = default)
    {
        using var redraw = PageRedrawScope.Begin(host);
        host.SuspendLayout();
        try
        {
        foreach (var oldButton in host.Controls.OfType<ThemedButton>())
        {
            oldButton.LeadingImage = null;
            if (oldButton.Tag is Image oldImage) oldImage.Dispose();
            oldButton.Tag = null;
        }
        DisposeFlowChildren(host);
        ResizeOptionButtons(host);
        if (labels.Count == 0)
        {
            host.Controls.Add(NativePageStyle.Label("情報がありません。", NativePageStyle.Small, Theme.Muted));
            return;
        }
        var available = Math.Max(UiMetrics.Px(this, 100), host.ClientSize.Width > 0 ? host.ClientSize.Width : host.Parent?.ClientSize.Width ?? UiMetrics.Px(this, 360));
        var buttonSize = OptionButtonSize(host, available);
        for (var i = 0; i < labels.Count; i++)
        {
            var index = i;
            var button = new ThemedButton
            {
                Text = labels[i].Length == 0 ? $"{i + 1}" : labels[i], Name = labels[i].Length == 0 ? $"{i + 1}" : labels[i],
                Size = buttonSize, Compact = true, Variant = ButtonVariant.Chip, Selected = i == selected,
                Margin = new Padding(UiMetrics.Px(this, UiMetrics.TightGap)),
                AccessibleName = ReferenceEquals(host, levelOptions) ? $"レベル {i + 1} を選択" : $"{labels[i]} を選択",
                AccessibleDescription = i == selected ? "選択中" : ReferenceEquals(host, levelOptions) ? "アップグレードレベル" : "カラーバリエーション"
            };
            button.Click += (_, _) => select(index);
            button.Disposed += (_, _) =>
            {
                button.LeadingImage = null;
                if (button.Tag is Image image) image.Dispose();
                button.Tag = null;
            };
            host.Controls.Add(button);
            if (swatches is not null && i < swatches.Count && !string.IsNullOrWhiteSpace(swatches[i]))
                _ = LoadChromaSwatchAsync(button, swatches[i]!, generation, cancellationToken);
        }
        }
        finally { host.ResumeLayout(true); }
    }

    private async Task LoadChromaSwatchAsync(ThemedButton button, string url, int generation, CancellationToken cancellationToken)
    {
        Image? image;
        try { image = await NativeImageCache.LoadOriginalAsync(url, cancellationToken); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { return; }
        catch (Exception ex) when (ex is HttpRequestException or IOException or InvalidOperationException or TaskCanceledException) { return; }
        if (image is null) return;
        if (IsDisposed || button.IsDisposed || cancellationToken.IsCancellationRequested || generation != previewGeneration)
        {
            image.Dispose();
            return;
        }
        using (PageRedrawScope.Begin(previewPanel))
        {
            chromaOptions.SuspendLayout();
            try
            {
                button.Tag = image;
                button.LeadingImage = image;
                ResizeOptionButtons(chromaOptions);
                resizePreviewLayout?.Invoke();
            }
            finally { chromaOptions.ResumeLayout(true); }
        }
    }

    private void ResizeOptionButtons(FlowLayoutPanel host)
    {
        var available = host.ClientSize.Width > 0 ? host.ClientSize.Width : host.Parent?.ClientSize.Width ?? 0;
        if (available <= 0) return;
        var size = OptionButtonSize(host, available);
        foreach (Control child in host.Controls)
            if (child is ThemedButton button)
            {
                if (button.Size != size) button.Size = size;
                button.Margin = new Padding(UiMetrics.Px(this, UiMetrics.TightGap));
                var label = string.IsNullOrEmpty(button.Name) ? button.AccessibleName ?? button.Text : button.Name;
                var hasSwatch = ReferenceEquals(host, chromaOptions) && button.LeadingImage is { Width: > 0 };
                var textWidth = TextRenderer.MeasureText(label, button.Font, Size.Empty, TextFormatFlags.SingleLine | TextFormatFlags.NoPadding).Width;
                var requiredWidth = textWidth + UiMetrics.Px(this, UiMetrics.ControlGap) * 2
                    + (hasSwatch ? UiMetrics.Px(this, 22 + UiMetrics.TightGap) : 0);
                if (button.Width >= requiredWidth)
                    button.Text = label;
                else if (hasSwatch)
                    button.Text = string.Empty;
                else if (ReferenceEquals(host, chromaOptions))
                    button.Text = button.Name.StartsWith("カラー ", StringComparison.Ordinal)
                        ? $"色 {button.Name[4..]}"
                        : button.Name.Length > 2 ? button.Name[..2] : button.Name;
                else
                    button.Text = label;
            }
    }

    private static string VariantLabel(string skinName, string variantName, string fallback, int index)
    {
        // Levels are always numbered the same way so a row never mixes "レベル 1" with "Lv.2".
        if (fallback == "レベル") return $"Lv.{index + 1}";
        var label = variantName.Trim();
        if (label.StartsWith(skinName, StringComparison.CurrentCultureIgnoreCase))
            label = label[skinName.Length..].Trim(' ', '-', '–', '—', ':', '：', '·');
        if (label.Length == 0 || label.Equals(skinName, StringComparison.CurrentCultureIgnoreCase) || label.Length > 6)
            return $"{fallback} {index + 1}";
        return label;
    }

    private void SelectOffer(string itemId)
    {
        var found = FindSkinForReward(itemId);
        if (found is null)
        {
            SetStatus("このオファーの武器スキンはカタログにありません。", true);
            return;
        }
        using var redraw = PageRedrawScope.Begin(storePage.Parent!);
        selectedSkin = found.Value.Skin;
        selectedLevel = found.Value.Level;
        selectedChroma = 0;
        var filtersChanged = searchBox.Text.Length > 0 || categorySelect.SelectedIndex != 0 || weaponSelect.SelectedIndex != 0;
        changingFilters = true;
        searchBox.Clear();
        categorySelect.SelectedIndex = 0;
        weaponSelect.SelectedIndex = 0;
        changingFilters = false;
        if (!catalogTab.Selected)
        {
            catalogEnsureSelectedVisible = true;
            ShowCatalogPage();
            return;
        }
        var selectedIndex = Array.FindIndex(FilteredSkins().ToArray(), skin => skin.Id.Equals(selectedSkin.Id, StringComparison.OrdinalIgnoreCase));
        var selectedPage = selectedIndex / Math.Max(1, catalogPageSize);
        if (filtersChanged || selectedIndex < 0 || selectedPage != catalogPageIndex ||
            !catalogGrid.Controls.Cast<Control>().Any(card => card.Tag is SkinCard skin && skin.Id.Equals(selectedSkin.Id, StringComparison.OrdinalIgnoreCase)))
        {
            catalogEnsureSelectedVisible = true;
            RenderCatalogGrid();
        }
        else UpdateCatalogSelection();
    }

    private (SkinCard Skin, int Level)? FindSkinForReward(string itemId)
    {
        if (rewardIndex.TryGetValue(itemId, out var found)) return found;
        return null;
    }

    private void RebuildRewardIndex()
    {
        rewardIndex = new Dictionary<string, (SkinCard Skin, int Level)>(StringComparer.OrdinalIgnoreCase);
        foreach (var skin in allSkins)
        {
            rewardIndex.TryAdd(skin.Id, (skin, 0));
            for (var i = 0; i < skin.Levels.Count; i++) rewardIndex.TryAdd(skin.Levels[i].Id, (skin, i));
        }
    }

    private void RebuildFilterLists()
    {
        changingFilters = true;
        try
        {
            var category = categorySelect.SelectedItem?.ToString();
            var weapon = weaponSelect.SelectedItem?.ToString();
            categorySelect.SetItems(["すべてのカテゴリー", .. allSkins.Select(s => CategoryKey(s.Category)).Where(s => s.Length > 0).Distinct(StringComparer.OrdinalIgnoreCase).OrderBy(s => s, StringComparer.CurrentCulture).Select(CategoryLabel)], category ?? "すべてのカテゴリー");
            weaponSelect.SetItems(["すべての武器", .. allSkins.Select(s => s.Weapon).Where(s => s.Length > 0).Distinct(StringComparer.CurrentCulture).OrderBy(s => s, StringComparer.CurrentCulture)], weapon ?? "すべての武器");
        }
        finally { changingFilters = false; }
    }

    private IEnumerable<SkinCard> FilteredSkins()
    {
        var search = searchBox.Text?.Trim() ?? string.Empty;
        var category = categorySelect.SelectedItem?.ToString();
        var weapon = weaponSelect.SelectedItem?.ToString();
        return allSkins.Where(s =>
            (search.Length == 0 || (s.Name + " " + s.Weapon).Contains(search, StringComparison.CurrentCultureIgnoreCase)) &&
            (category is null or "すべてのカテゴリー" || CategoryLabel(CategoryKey(s.Category)).Equals(category, StringComparison.CurrentCultureIgnoreCase)) &&
            (weapon is null or "すべての武器" || s.Weapon.Equals(weapon, StringComparison.CurrentCultureIgnoreCase)));
    }

    private void UpdateCatalogCounts()
    {
        if (catalogSnapshot is not { } snapshot) return;
        skinCountLabel.Text = snapshot.SkinCount.ToString("N0");
        chromaCountLabel.Text = snapshot.ChromaCount.ToString("N0");
        videoCountLabel.Text = snapshot.VideoCount.ToString("N0");
    }

    private void ShowStorePage()
    {
        if (storeTab.Selected) return;
        using var redraw = PageRedrawScope.Begin(storePage.Parent!);
        storePage.Visible = true; catalogPage.Visible = false;
        storeTab.Selected = true; catalogTab.Selected = false;
        ShowPreviewImage();
        PreparePageLayout();
    }

    private void ShowCatalogPage()
    {
        if (catalogTab.Selected) return;
        using var redraw = PageRedrawScope.Begin(storePage.Parent!);
        switchingCatalogPage = true;
        try
        {
        ShowPreviewImage();
        storeTab.Selected = false; catalogTab.Selected = true;
        catalogPage.Visible = true; storePage.Visible = false;
        PreparePageLayout();
        void LayoutTree(Control control)
        {
            control.PerformLayout();
            foreach (Control child in control.Controls)
                if (child.Visible) LayoutTree(child);
        }
        LayoutTree(catalogPage);
        RenderCatalogGrid();
        }
        finally { switchingCatalogPage = false; }
        if (!catalogLoaded) _ = LoadCatalogAsync(refresh: false);
    }

    private async Task<bool> PlayPreviewVideoAsync(CancellationToken externalToken)
    {
        if (playVideoButton.Tag is not string url || string.IsNullOrWhiteSpace(url))
        {
            SetStatus("このスキンの動画はありません。", true);
            return false;
        }

        // Repeated clicks must not restart the same MediaElement and leave the
        // still image layered over a video that has already begun opening.
        if (videoOpening) return false;
        if (videoHost.Visible)
        {
            mediaPlayer.Play();
            videoPaused = false;
            UpdateVideoControls();
            if (!previewImage.Visible) playbackOpened?.TrySetResult(true);
            return true;
        }
        if (videoLoad is { IsCancellationRequested: false }) return false;

        videoLoad?.Cancel();
        var operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, externalToken);
        videoLoad = operation;
        videoLoadingOverlay.Loading = true;
        videoLoadingOverlay.LoadingMessage = "動画を取得しています…";
        previewImage.Visible = false;
        videoLoadingOverlay.Visible = true;
        videoLoadingOverlay.BringToFront();
        playVideoButton.Visible = false;
        stopVideoButton.Text = "キャンセル";
        stopVideoButton.Invalidate();
        stopVideoButton.Visible = true;
        try
        {
            var downloadProgress = new Progress<(long Received, long? Total)>(progress =>
            {
                if (IsDisposed || !ReferenceEquals(videoLoad, operation) || operation.IsCancellationRequested) return;
                var receivedMb = progress.Received / (1024d * 1024d);
                videoLoadingOverlay.LoadingMessage = progress.Total is > 0
                    ? $"動画を取得しています… {receivedMb:0.0}/{progress.Total.Value / (1024d * 1024d):0.0} MB"
                    : $"動画を取得しています… {receivedMb:0.0} MB";
            });
            var localFile = await NativeVideoCache.GetLocalFileAsync(url, operation.Token, downloadProgress);
            if (string.IsNullOrWhiteSpace(localFile) || IsDisposed || operation.IsCancellationRequested)
            {
                if (!operation.IsCancellationRequested)
                {
                    ShowPreviewImage();
                    SetStatus("動画を取得できませんでした。ネット接続を確認してください。", true);
                }
                return false;
            }

            var uri = new Uri(localFile, UriKind.Absolute);
            videoLoadingOverlay.LoadingMessage = "動画を読み込んでいます…";
            mediaPlayer.Stop();
            videoOpening = true;
            videoOpenTimer.Stop();
            videoOpenTimer.Start();
            // The ElementHost HWND must exist for WPF to open the media, but a full-size host would
            // cover the loading overlay (WinForms/WPF airspace). Park it at 1x1 off-screen until MediaOpened.
            ParkVideoHost();
            videoLoadingOverlay.BringToFront();
            mediaPlayer.Source = uri;
            mediaPlayer.Play();
            return true;
        }
        catch (OperationCanceledException) when (operation.IsCancellationRequested)
        {
            if (videoLoadingOverlay.Visible && ReferenceEquals(videoLoad, operation) && !IsDisposed && !Disposing)
                ShowPreviewImage();
            return false;
        }
        catch (Exception ex)
        {
            NativePageStyle.LogPageError("StoreVideo", ex);
            if (ReferenceEquals(videoLoad, operation) && !IsDisposed && !Disposing)
            {
                ShowPreviewImage();
                SetStatus("動画を開けませんでした。", true);
            }
            return false;
        }
        finally
        {
            if (ReferenceEquals(videoLoad, operation)) videoLoad = null;
            operation.Dispose();
        }
    }

    private void ShowPreviewImage()
    {
        StopMedia();
        videoPaused = false;
        videoLoadingOverlay.Loading = false;
        videoLoadingOverlay.Visible = false;
        previewImage.Visible = true;
        previewImage.BringToFront();
        RestoreVideoHost();
        stopVideoButton.Text = "画像へ";
        stopVideoButton.Invalidate();
        stopVideoButton.Visible = false;
        playbackButton.Visible = false;
        muteVideoButton.Visible = false;
        playVideoButton.Visible = true;
        playVideoButton.Enabled = !string.IsNullOrWhiteSpace(playVideoButton.Tag as string);
        resizeMediaActions?.Invoke();
    }

    private void ToggleVideoPlayback()
    {
        if (!videoHost.Visible || videoOpening) return;
        if (videoPaused) mediaPlayer.Play();
        else mediaPlayer.Pause();
        videoPaused = !videoPaused;
        UpdateVideoControls();
    }

    private void ToggleVideoMute()
    {
        if (!videoHost.Visible) return;
        mediaPlayer.IsMuted = !mediaPlayer.IsMuted;
        UpdateVideoControls();
    }

    private void UpdateVideoControls()
    {
        playbackButton.Text = videoPaused ? "再生" : "一時停止";
        playbackButton.AccessibleName = videoPaused ? "動画を再生" : "動画を一時停止";
        muteVideoButton.Text = "音声";
        muteVideoButton.AccessibleName = mediaPlayer.IsMuted ? "動画の音声をオン" : "動画をミュート";
        resizeMediaActions?.Invoke();
    }

    private void StopMedia()
    {
        videoOpenTimer.Stop();
        videoOpening = false;
        videoLoadingOverlay.Loading = false;
        videoLoadingOverlay.Visible = false;
        videoLoad?.Cancel();
        playbackOpened?.TrySetResult(false);
        try { mediaPlayer.Stop(); mediaPlayer.Close(); mediaPlayer.Source = null; }
        catch (InvalidOperationException) { }
        RestoreVideoHost();
    }

    private void ParkVideoHost()
    {
        videoHost.Dock = DockStyle.None;
        videoHost.Size = new Size(1, 1);
        videoHost.Location = new Point(-10, -10);
        videoHost.Visible = true;
    }

    // Returns the host to its normal full-size slot, hidden unless the caller shows it.
    private void RestoreVideoHost(bool visible = false)
    {
        if (videoHost.IsDisposed) return;
        videoHost.Dock = DockStyle.Fill;
        videoHost.Visible = visible;
    }

    private void ResizeVideoSurface()
    {
        var scale = Math.Max(1, videoHost.DeviceDpi) / 96d;
        var width = Math.Max(1, videoHost.ClientSize.Width / scale);
        var height = Math.Max(1, videoHost.ClientSize.Height / scale);
        videoSurface.Width = width;
        videoSurface.Height = height;
        mediaPlayer.Width = width;
        mediaPlayer.Height = height;
    }

    private async Task LoadPreviewImageAsync(IEnumerable<string?> urls, int generation, CancellationToken cancellationToken)
    {
        var candidates = urls.Where(url => !string.IsNullOrWhiteSpace(url)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (previewImage.Image is null) previewImage.Loading = candidates.Length > 0;
        try
        {
            foreach (var url in candidates)
            {
                Image? image;
                try { image = await NativeImageCache.LoadOriginalAsync(url, cancellationToken); }
                catch (OperationCanceledException) { return; }
                if (IsDisposed || cancellationToken.IsCancellationRequested || generation != previewGeneration) { image?.Dispose(); return; }
                if (image is null) continue;
                previewImage.Image = image;
                return;
            }
        }
        finally
        {
            if (!IsDisposed && generation == previewGeneration) previewImage.Loading = false;
        }
    }

    // Store cards keep the full-resolution original; SharpImageBox scales it once per display size.
    private async Task LoadStoreImageAsync(SharpImageBox target, IEnumerable<string?> urls, CancellationToken cancellationToken)
    {
        var candidates = urls.Where(url => !string.IsNullOrWhiteSpace(url)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        if (target.Image is null) target.Loading = candidates.Length > 0;
        try
        {
            foreach (var url in candidates)
            {
                Image? image;
                try { image = await NativeImageCache.LoadOriginalAsync(url, cancellationToken); }
                catch (OperationCanceledException) { return; }
                if (image is null) continue;
                if (IsDisposed || target.IsDisposed || cancellationToken.IsCancellationRequested) { image.Dispose(); return; }
                target.Image = image;
                return;
            }
        }
        finally
        {
            if (!target.IsDisposed) target.Loading = false;
        }
    }

    private static IEnumerable<string?> ArtCandidates(SkinCard skin, int chroma, int level)
    {
        var selectedChroma = chroma >= 0 && chroma < skin.Chromas.Count ? skin.Chromas[chroma] : null;
        var selectedLevel = level >= 0 && level < skin.Levels.Count ? skin.Levels[level] : null;
        return new string?[]
            {
                selectedChroma?.FullRender,
                selectedLevel?.Icon,
                skin.Image,
                selectedChroma?.Icon,
                skin.WeaponImage
            }
            .Where(url => !string.IsNullOrWhiteSpace(url))
            .Distinct(StringComparer.OrdinalIgnoreCase);
    }
    private string? SelectedVideo(SkinCard skin, int chroma, int level) =>
        (chroma >= 0 && chroma < skin.Chromas.Count ? skin.Chromas[chroma].Video : null) ??
        (level >= 0 && level < skin.Levels.Count ? skin.Levels[level].Video : null);

    private void SetStatus(string message, bool error)
    {
        if (IsDisposed || Disposing) return;
        var progress = IsProgressMessage(message);
        if (progress)
        {
            if (activeProgressStatuses.Count == 0) { settledStatus = ""; settledStatusError = false; }
            if (!activeProgressStatuses.Contains(message)) activeProgressStatuses.Add(message);
            SetSpinner(true);
            SetStatusDisplay(message, error: false);
            return;
        }
        if (message.Length > 0 || !settledStatusError)
        {
            settledStatus = message;
            settledStatusError = error;
        }
        SetSpinner(activeProgressStatuses.Count > 0);
        SetStatusDisplay(activeProgressStatuses.Count > 0 ? activeProgressStatuses[^1] : settledStatus, activeProgressStatuses.Count == 0 && settledStatusError);
    }

    private void SetSpinner(bool active)
    {
        if (statusSpinner.IsDisposed) return;
        statusSpinner.Active = active;
        statusSpinner.Visible = active;
    }

    // Ends a progress status that finished without reporting a result (cancel, closed login window, etc.).
    private void EndProgress(string message)
    {
        if (!disposingStore && !IsDisposed && !Disposing && activeProgressStatuses.Remove(message))
        {
            SetSpinner(activeProgressStatuses.Count > 0);
            SetStatusDisplay(activeProgressStatuses.Count > 0 ? activeProgressStatuses[^1] : settledStatus,
                activeProgressStatuses.Count == 0 && settledStatusError);
        }
    }

    private void SetStatusDisplay(string message, bool error)
    {
        // Disposing: the Disposed handler runs after child controls are already torn down.
        if (IsDisposed || Disposing || statusLabel.IsDisposed) return;
        statusLabel.Text = message;
        statusLabel.AccessibleDescription = message;
        statusLabel.ForeColor = error ? Theme.Red : Theme.Muted;
    }

    private static bool IsProgressMessage(string message) =>
        message.Contains("読み込んでいます", StringComparison.Ordinal) ||
        message.Contains("読み込み中", StringComparison.Ordinal) ||
        message.Contains("更新しています", StringComparison.Ordinal) ||
        message.Contains("取得しています", StringComparison.Ordinal) ||
        message.Contains("準備しています", StringComparison.Ordinal) ||
        message.Contains("接続しています", StringComparison.Ordinal) ||
        message.Contains("画像を表示しています", StringComparison.Ordinal);

    private void UpdateCountdowns()
    {
        if (storefront is null || storefrontFetchedAt == default)
        {
            dailyTimerLabel.Text = accessoryTimerLabel.Text = bundleTimerLabel.Text = nightTimerLabel.Text = "—";
            return;
        }
        dailyTimerLabel.Text = Remaining(storefront.DailySkins.RemainingSeconds);
        accessoryTimerLabel.Text = Remaining(storefront.Accessories.RemainingSeconds);
        bundleTimerLabel.Text = Remaining(storefront.FeaturedBundles.RemainingSeconds);
        nightTimerLabel.Text = storefront.NightMarket is { } market ? Remaining(market.RemainingSeconds) : "—";
        foreach (var (bundle, detail) in bundleDetails)
        {
            if (detail.IsDisposed) continue;
            var text = BundleDetailText(bundle);
            if (detail.Text != text) detail.Text = text;
        }
    }

    private string Remaining(long seconds)
    {
        var remaining = RemainingSeconds(seconds);
        return $"{remaining / 3600:00}:{remaining % 3600 / 60:00}:{remaining % 60:00}";
    }

    private long RemainingSeconds(long seconds)
    {
        var elapsed = Math.Max(0, (long)(DateTimeOffset.UtcNow - storefrontFetchedAt).TotalSeconds);
        return Math.Max(0, seconds - elapsed);
    }

    private void SetSectionVisibility(string key, bool visible)
    {
        var section = storeSections.Controls.Cast<Control>().FirstOrDefault(c => Equals(c.Tag, key));
        if (!visible && ActiveStoreSection() == key) ShowStoreSection("daily");
        if (section is not null) section.Visible = visible && Equals(ActiveStoreSection(), key);
        switch (key)
        {
            case "night": nightSectionTab.Visible = visible; break;
            case "bundle": bundleSectionTab.Visible = visible; break;
            case "accessories": accessorySectionTab.Visible = visible; break;
        }
    }

    private string ActiveStoreSection() => dailySectionTab.Selected ? "daily" : nightSectionTab.Selected ? "night" : bundleSectionTab.Selected ? "bundle" : accessorySectionTab.Selected ? "accessories" : "daily";

    private static string CategoryKey(string value) => value.Split("::", StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
    private static string CategoryLabel(string category) => category switch
    {
        "Sidearm" => "ハンドガン", "SMG" => "サブマシンガン", "Shotgun" => "ショットガン", "Rifle" => "ライフル",
        "Sniper" => "スナイパー", "Heavy" => "ヘビー", "Melee" => "近接武器", _ => category
    };
    private static string ItemTypeName(string typeId, bool accessory = false) => typeId.ToLowerInvariant() switch
    {
        BuddyType => "ガンバディー", SprayType => "スプレー", PlayerCardType => "プレイヤーカード",
        "de7caa6b-adf7-4588-bbd1-143831e786c6" => "プレイヤータイトル", _ => accessory ? "アクセサリー" : "武器スキン"
    };
    private static string Money(IReadOnlyDictionary<string, long>? cost)
    {
        if (cost is null || cost.Count == 0) return "価格不明";
        var first = cost.First();
        return $"{first.Value:N0} { (CurrencyNames.TryGetValue(first.Key, out var name) ? name : "クレジット") }";
    }
    private static string OfferPriceText(StorefrontOffer offer, bool night)
    {
        if (!night || offer.DiscountedCost is null) return Money(offer.Cost);
        var discounted = Money(offer.DiscountedCost);
        var regular = Money(offer.Cost);
        return discounted == regular ? discounted : $"{discounted}  (通常 {regular})";
    }
    private static ResponsiveCardGrid OfferGrid(double preferredAspect) => new() { Dock = DockStyle.Fill, BackColor = Theme.Surface, PreferredAspect = preferredAspect };

    private Control CreateEmptyMessage(string text, int width, int height)
    {
        var label = NativePageStyle.Label(text, NativePageStyle.Body, Theme.Muted);
        label.TextAlign = ContentAlignment.MiddleCenter; label.Size = new Size(UiMetrics.Px(this, width), UiMetrics.Px(this, height)); label.Margin = new Padding(UiMetrics.Px(this, 5));
        return label;
    }
    private static void DisposeFlowChildren(Control parent)
    {
        foreach (Control child in parent.Controls.Cast<Control>().ToArray()) { parent.Controls.Remove(child); child.Dispose(); }
    }
}
