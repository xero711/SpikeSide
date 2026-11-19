using System.Net.Http;
using System.Text.Json;
using System.Windows.Forms.Integration;
using MediaElement = System.Windows.Controls.MediaElement;
using MediaState = System.Windows.Controls.MediaState;
using Stretch = System.Windows.Media.Stretch;

namespace ValoAutoPick;

public sealed class StorePreviewControl : UserControl
{
    private const string VideoPrepareStatus = "動画を準備しています…";
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
    private readonly ThemedButton storeTab = new() { Text = "今日のSTORE", Size = new(180, 40), Compact = true, Primary = true };
    private readonly ThemedButton catalogTab = new() { Text = "武器カタログ", Size = new(168, 40), Compact = true };
    private readonly ThemedButton dailySectionTab = new() { Text = "デイリー", Size = new(132, 38), Compact = true, Primary = true };
    private readonly ThemedButton nightSectionTab = new() { Text = "ナイトマーケット", Size = new(176, 38), Compact = true, Visible = false };
    private readonly ThemedButton bundleSectionTab = new() { Text = "注目バンドル", Size = new(150, 38), Compact = true, Visible = false };
    private readonly ThemedButton accessorySectionTab = new() { Text = "アクセサリー", Size = new(142, 38), Compact = true, Visible = false };
    private readonly ThemedButton previousCatalogPage = new() { Text = "前へ", Size = new(92, 34), Compact = true };
    private readonly ThemedButton nextCatalogPage = new() { Text = "次へ", Size = new(92, 34), Compact = true };
    private readonly Label catalogPageLabel = NativePageStyle.Label("1 / 1", NativePageStyle.Small, Theme.Muted);
    private readonly ThemedButton loginButton = new() { Text = "Riotにログイン", Size = new(162, 40), Compact = true };
    private readonly ThemedButton logoutButton = new() { Text = "ログアウト", Size = new(122, 40), Compact = true };
    private readonly ThemedButton refreshStoreButton = new() { Text = "今日のショップを更新", Size = new(204, 40), Compact = true };
    private readonly ThemedButton refreshCatalogButton = new() { Text = "カタログ更新", Size = new(150, 40), Compact = true };
    private readonly ThemedButton playVideoButton = new() { Text = "▶ 動画を再生", Size = new(156, 38), Compact = true };
    private readonly ThemedButton stopVideoButton = new() { Text = "画像に戻る", Size = new(132, 38), Compact = true };
    private readonly Label accountLabel = NativePageStyle.Label("Riotアカウント未接続", NativePageStyle.Small, Theme.Muted);
    private readonly Label skinCountLabel = NativePageStyle.Label("—", NativePageStyle.Metric, Theme.Text);
    private readonly Label chromaCountLabel = NativePageStyle.Label("—", NativePageStyle.Metric, Theme.Text);
    private readonly Label videoCountLabel = NativePageStyle.Label("—", NativePageStyle.Metric, Theme.Text);
    private readonly Label statusLabel = NativePageStyle.Label("Riotアカウント未接続", NativePageStyle.Small, Theme.Muted);
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
    private readonly FlowLayoutPanel catalogGrid = new() { Dock = DockStyle.Fill, AutoScroll = false, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, BackColor = Theme.Background, Margin = Padding.Empty, Padding = new Padding(2) };
    private readonly Panel catalogGridHost = new() { Dock = DockStyle.Fill, BackColor = Theme.Surface, Margin = Padding.Empty };
    private readonly SkeletonScreen catalogSkeleton = new(SkeletonLayout.CatalogCards) { Dock = DockStyle.Fill, BackColor = Theme.Surface, Visible = false };
    private readonly FlowLayoutPanel chromaOptions = new() { AutoSize = true, WrapContents = true, FlowDirection = FlowDirection.LeftToRight, BackColor = Theme.Surface, Margin = Padding.Empty };
    private readonly FlowLayoutPanel levelOptions = new() { AutoSize = true, WrapContents = true, FlowDirection = FlowDirection.LeftToRight, BackColor = Theme.Surface, Margin = Padding.Empty };
    private readonly Panel storePage = new() { Dock = DockStyle.Fill, BackColor = Theme.Background };
    private readonly Panel catalogPage = new() { Dock = DockStyle.Fill, BackColor = Theme.Background, Visible = false };
    private readonly Panel visualHost = new() { Dock = DockStyle.Fill, BackColor = Color.FromArgb(10, 13, 19), Margin = Padding.Empty };
    private readonly SharpImageBox previewImage = new() { Dock = DockStyle.Fill, BackColor = Color.FromArgb(10, 13, 19) };
    private readonly ElementHost videoHost = new() { Dock = DockStyle.Fill, BackColor = Color.FromArgb(10, 13, 19), Visible = false };
    private readonly SkeletonScreen videoLoadingOverlay = new(SkeletonLayout.MediaPreview) { Dock = DockStyle.Fill, BackColor = Color.FromArgb(10, 13, 19), Visible = false };
    private readonly System.Windows.Forms.Timer videoOpenTimer = new() { Interval = 20_000 };
    private readonly System.Windows.Controls.Grid videoSurface = new()
    {
        Background = System.Windows.Media.Brushes.Black,
        HorizontalAlignment = System.Windows.HorizontalAlignment.Stretch,
        VerticalAlignment = System.Windows.VerticalAlignment.Stretch
    };
    private readonly MediaElement mediaPlayer = new() { LoadedBehavior = MediaState.Manual, UnloadedBehavior = MediaState.Stop, Stretch = Stretch.Uniform, IsMuted = true, Volume = 0 };
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
    private (SkinCard? Skin, int Chroma, int Level)? displayedPreview;
    private CancellationTokenSource? previewImageLoad;
    private int storeGeneration;
    private CancellationTokenSource? activeStoreLoad;
    private CancellationTokenSource? videoLoad;
    private bool videoOpening;
    private TaskCompletionSource<bool>? playbackOpened;
    private Action? resizePreviewLayout;
    private bool catalogLoaded;
    private bool catalogTextMetricsReady;
    private bool loadingCatalog;
    private Task? catalogLoadTask;
    private string catalogLoadMessage = "武器カタログを開くと一覧を表示します。";
    private bool loadingStore;
    private bool signingIn;
    private bool changingFilters;
    private bool loopVideo = true;
    private string settledStatus = "Riotアカウント未接続";
    private bool settledStatusError;
    private string activeProgressStatus = "";

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
        mediaPlayer.MediaFailed += (_, e) =>
        {
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
        mediaPlayer.MediaOpened += (_, _) =>
        {
            if (!videoOpening) return;
            videoOpenTimer.Stop();
            videoOpening = false;
            videoLoadingOverlay.Loading = false;
            ResizeVideoSurface();
            videoSurface.UpdateLayout();
            PreviewVideoResult = $"MediaOpened:{mediaPlayer.NaturalVideoWidth}x{mediaPlayer.NaturalVideoHeight}:{mediaPlayer.ActualWidth:0}x{mediaPlayer.ActualHeight:0}:{videoHost.ClientSize.Width}x{videoHost.ClientSize.Height}";
            videoLoadingOverlay.Visible = false;
            previewImage.Visible = false;
            videoHost.Visible = true;
            videoHost.BringToFront();
            stopVideoButton.Text = "画像に戻る";
            stopVideoButton.Invalidate();
            stopVideoButton.Visible = true;
            playbackOpened?.TrySetResult(true);
            SetStatus("", false);
        };
        mediaPlayer.MediaEnded += (_, _) =>
        {
            if (!loopVideo || !videoHost.Visible) return;
            mediaPlayer.Position = TimeSpan.Zero;
            mediaPlayer.Play();
        };
        BuildInterface();
        WireEvents();
        UpdateAccountState();
        RenderStorefront();
        RenderCatalogGrid();
        countdownTimer.Tick += (_, _) => UpdateCountdowns();
        countdownTimer.Start();
        Disposed += (_, _) =>
        {
            lifetime.Cancel();
            activeStoreLoad?.Cancel();
            videoLoad?.Cancel();
            StopMedia();
            videoOpenTimer.Dispose();
            countdownTimer.Stop(); countdownTimer.Dispose();
            catalog.Dispose(); loginWindow?.Dispose();
            if (ownsRiot) riot.Dispose();
            previewImageLoad?.Cancel();
            previewImageLoad?.Dispose();
            storeImageLoads?.Cancel(); storeImageLoads?.Dispose(); storeImageLoads = null;
            lifetime.Dispose();
        };
    }

    public async Task ActivatePageAsync()
    {
        if (IsDisposed) return;
        try
        {
            if (!catalogLoaded) await LoadCatalogAsync(refresh: false);
            if (riot.HasAccountSession && (storefront is null || RemainingSeconds(storefront.DailySkins.RemainingSeconds) == 0))
                await LoadStorefrontAsync();
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            NativePageStyle.LogPageError("StoreActivation", ex);
            SetStatus("STOREを読み込めませんでした。カタログ更新を押して再試行してください。", true);
        }
    }

    internal void ReportActivationError(string message) => SetStatus(message, true);

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
            else EndProgress(VideoPrepareStatus);
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
        SetStatus("", false);
    }

    private void BuildInterface()
    {
        var root = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 4, BackColor = Theme.Background, Padding = new Padding(12, 8, 12, 8), Margin = Padding.Empty };
        root.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 78));
        var navigationRow = new RowStyle(SizeType.Absolute, 42);
        root.RowStyles.Add(navigationRow);
        root.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        root.RowStyles.Add(new RowStyle(SizeType.Absolute, 21));
        root.Controls.Add(BuildHeader(), 0, 0);
        root.Controls.Add(BuildNavigation(navigationRow), 0, 1);
        root.Controls.Add(BuildPages(), 0, 2);
        var statusRow = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Background, Margin = Padding.Empty };
        statusLabel.Dock = DockStyle.Fill;
        statusRow.Controls.Add(statusLabel);
        root.Controls.Add(statusRow, 0, 3);
        Controls.Add(root);

    }

    private Control BuildHeader()
    {
        var header = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Background, Margin = Padding.Empty };
        var title = NativePageStyle.Label("STORE  /  今日のラインナップ", NativePageStyle.Heading);
        var counts = new FlowLayoutPanel { AutoSize = true, WrapContents = false, FlowDirection = FlowDirection.LeftToRight, Anchor = AnchorStyles.Top | AnchorStyles.Right, BackColor = Theme.Background, Margin = Padding.Empty };
        counts.Controls.Add(CountBlock(skinCountLabel, "武器スキン"));
        counts.Controls.Add(CountBlock(chromaCountLabel, "カラー"));
        counts.Controls.Add(CountBlock(videoCountLabel, "動画"));
        header.Controls.Add(title); header.Controls.Add(counts);
        void Place()
        {
            var s = new Func<int, int>(value => UiMetrics.Px(header, value));
            title.Bounds = new Rectangle(0, s(16), Math.Max(0, header.ClientSize.Width - counts.Width - s(20)), NativePageStyle.TextHeight(header, title.Font, verticalPadding: 8));
            counts.Location = new Point(Math.Max(0, header.ClientSize.Width - counts.Width), s(10));
        }
        header.Resize += (_, _) => Place();
        Place();
        return header;
    }

    private static Control CountBlock(Label value, string caption)
    {
        var block = new Panel { Width = 112, Height = 64, BackColor = Theme.Background, Margin = new Padding(4, 0, 4, 0) };
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
        Place();
        return block;
    }

    private Control BuildNavigation(RowStyle row)
    {
        var bar = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Background, Margin = Padding.Empty };
        var tabs = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, FlowDirection = FlowDirection.LeftToRight, BackColor = Theme.Background, Margin = Padding.Empty, Padding = Padding.Empty };
        storeTab.Margin = Padding.Empty; catalogTab.Margin = new Padding(7, 0, 0, 0);
        tabs.Controls.Add(storeTab); tabs.Controls.Add(catalogTab);
        var actions = new FlowLayoutPanel { AutoSize = true, AutoSizeMode = AutoSizeMode.GrowAndShrink, WrapContents = false, FlowDirection = FlowDirection.LeftToRight, BackColor = Theme.Background, Margin = Padding.Empty, Padding = Padding.Empty };
        accountLabel.AutoSize = true;
        accountLabel.AutoEllipsis = false;
        accountLabel.UseMnemonic = false;
        accountLabel.Margin = new Padding(0, 0, 12, 0);
        refreshStoreButton.Margin = new Padding(0, 0, 6, 0);
        refreshCatalogButton.Margin = new Padding(0, 0, 6, 0);
        loginButton.Margin = new Padding(0, 0, 6, 0);
        logoutButton.Margin = Padding.Empty;
        actions.Controls.Add(accountLabel); actions.Controls.Add(refreshStoreButton); actions.Controls.Add(refreshCatalogButton);
        actions.Controls.Add(loginButton); actions.Controls.Add(logoutButton);
        bar.Controls.Add(tabs); bar.Controls.Add(actions);

        var relayoutQueued = false;
        void Place()
        {
            if (bar.ClientSize.Width <= 1) return;
            var gap = UiMetrics.Px(bar, 8);
            var stacked = tabs.Width + gap + actions.Width > bar.ClientSize.Width;
            var contentHeight = stacked ? tabs.Height + gap + actions.Height : Math.Max(tabs.Height, actions.Height);
            var height = contentHeight + UiMetrics.Px(bar, 12);
            tabs.Location = new Point(0, stacked ? 0 : (contentHeight - tabs.Height) / 2);
            actions.Location = new Point(Math.Max(0, bar.ClientSize.Width - actions.Width),
                stacked ? tabs.Height + gap : (contentHeight - actions.Height) / 2);
            if (row.Height != height) row.Height = height;
            // A resize during the table's own layout needs a follow-up pass to apply the new row height.
            if (bar.Height != height && bar.IsHandleCreated && !relayoutQueued)
            {
                relayoutQueued = true;
                bar.BeginInvoke(() =>
                {
                    relayoutQueued = false;
                    if (!bar.IsDisposed) bar.Parent?.PerformLayout();
                });
            }
        }

        bar.SizeChanged += (_, _) => Place();
        tabs.SizeChanged += (_, _) => Place();
        actions.SizeChanged += (_, _) => Place();
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
        var tabsRow = new RowStyle(SizeType.Absolute, 52);
        layout.RowStyles.Add(tabsRow);
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var tabs = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Theme.Background, Margin = Padding.Empty, Padding = Padding.Empty };
        dailySectionTab.Margin = new Padding(0, 0, 6, 0);
        nightSectionTab.Margin = new Padding(0, 0, 6, 0);
        bundleSectionTab.Margin = new Padding(0, 0, 6, 0);
        accessorySectionTab.Margin = Padding.Empty;
        tabs.Controls.Add(dailySectionTab); tabs.Controls.Add(nightSectionTab); tabs.Controls.Add(bundleSectionTab); tabs.Controls.Add(accessorySectionTab);
        void SizeTabs()
        {
            var height = tabs.Controls.Cast<Control>().Where(control => control.Visible)
                .Select(control => control.Height + control.Margin.Vertical).DefaultIfEmpty(dailySectionTab.Height).Max() + UiMetrics.Px(tabs, 12);
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
        card.Dock = DockStyle.Fill; card.Margin = new Padding(3); card.Padding = new Padding(12, 8, 12, 8);
        var header = new Panel { Dock = DockStyle.Top, Height = 32, BackColor = Theme.Surface };
        var name = NativePageStyle.Label(title, NativePageStyle.SmallBold, Theme.Muted); name.Dock = DockStyle.Left; name.Width = 360;
        var timerCaption = NativePageStyle.Label("次回更新  ", NativePageStyle.Small, Theme.Muted); timerCaption.Dock = DockStyle.Right; timerCaption.Width = 90; timerCaption.TextAlign = ContentAlignment.MiddleRight;
        timer.Dock = DockStyle.Right; timer.Width = 90; timer.TextAlign = ContentAlignment.MiddleRight;
        header.Controls.Add(timerCaption); header.Controls.Add(timer); header.Controls.Add(name);
        body = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface, Padding = new Padding(0, 8, 0, 0) };
        card.Controls.Add(body); card.Controls.Add(header);
        return card;
    }

    private void BuildCatalogPage()
    {
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 2, BackColor = Theme.Background, Margin = Padding.Empty };
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 54)); layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        var toolbar = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Theme.Background, Padding = new Padding(0, 3, 0, 5), Margin = Padding.Empty };
        searchBox.Width = 270; searchBox.Height = 38; searchBox.Margin = new Padding(0, 2, 8, 0);
        categorySelect.Width = 230; categorySelect.Height = 38; categorySelect.Margin = new Padding(0, 2, 8, 0);
        weaponSelect.Width = 210; weaponSelect.Height = 38; weaponSelect.Margin = new Padding(0, 2, 8, 0);
        toolbar.Controls.Add(searchBox); toolbar.Controls.Add(categorySelect); toolbar.Controls.Add(weaponSelect);
        layout.Controls.Add(toolbar, 0, 0);

        var split = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 2, RowCount = 1, BackColor = Theme.Background, Margin = Padding.Empty };
        split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 64)); split.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 36));
        var listCard = NativePageStyle.Card(); listCard.Margin = new Padding(3); listCard.Padding = new Padding(8);
        var pageFooter = new Panel { Dock = DockStyle.Bottom, Height = 40, BackColor = Theme.Surface };
        previousCatalogPage.Size = new Size(92, 34);
        nextCatalogPage.Size = new Size(92, 34);
        previousCatalogPage.Location = new Point(0, 2);
        nextCatalogPage.Anchor = AnchorStyles.Top | AnchorStyles.Right;
        catalogPageLabel.TextAlign = ContentAlignment.MiddleCenter;
        catalogPageLabel.Dock = DockStyle.None;
        void PlacePageFooter()
        {
            previousCatalogPage.Size = nextCatalogPage.Size = new Size(UiMetrics.Px(this, 92), UiMetrics.Px(this, 34));
            previousCatalogPage.Location = new Point(0, UiMetrics.Px(this, 2));
            nextCatalogPage.Location = new Point(Math.Max(0, pageFooter.ClientSize.Width - nextCatalogPage.Width), UiMetrics.Px(this, 2));
            catalogPageLabel.Bounds = new Rectangle(previousCatalogPage.Right + UiMetrics.Px(this, 6), 0,
                Math.Max(0, nextCatalogPage.Left - previousCatalogPage.Right - UiMetrics.Px(this, 12)), pageFooter.ClientSize.Height);
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
        split.Controls.Add(BuildPreviewPanel(), 1, 0);
        layout.Controls.Add(split, 0, 1);
        catalogPage.Controls.Add(layout);
        catalogGrid.SizeChanged += (_, _) =>
        {
            if (!catalogPage.Visible) return;
            if (catalogLoaded) UpdateCatalogPageSize();
            else RenderCatalogGrid();
        };
        previousCatalogPage.Click += (_, _) => { if (catalogPageIndex > 0) { catalogPageIndex--; RenderCatalogGrid(); } };
        nextCatalogPage.Click += (_, _) => { catalogPageIndex++; RenderCatalogGrid(); };
    }

    private Control BuildPreviewPanel()
    {
        var panel = NativePageStyle.Card(); panel.Margin = new Padding(3); panel.Padding = new Padding(12);
        var layout = new TableLayoutPanel { Dock = DockStyle.Fill, ColumnCount = 1, RowCount = 8, BackColor = Theme.Surface, Margin = Padding.Empty, Padding = Padding.Empty };
        layout.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 70));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 42));
        layout.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 24));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 34));
        layout.RowStyles.Add(new RowStyle(SizeType.Absolute, 26));
        var header = new Panel { Dock = DockStyle.Fill, BackColor = Theme.Surface, Margin = Padding.Empty };
        catalogTitle.AutoSize = false;
        catalogMeta.AutoSize = false;
        catalogTitle.AutoEllipsis = false;
        catalogMeta.AutoEllipsis = false;
        header.Controls.Add(catalogTitle); header.Controls.Add(catalogMeta);
        header.Resize += (_, _) => { catalogTitle.Bounds = new Rectangle(0, 0, Math.Max(UiMetrics.Px(header, 100), header.ClientSize.Width), UiMetrics.Px(header, 44)); catalogMeta.Bounds = new Rectangle(UiMetrics.Px(header, 1), UiMetrics.Px(header, 45), Math.Max(UiMetrics.Px(header, 100), header.ClientSize.Width), UiMetrics.Px(header, 22)); };
        var mediaActions = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = false, BackColor = Theme.Surface, Margin = Padding.Empty };
        playVideoButton.Margin = new Padding(0, 0, 6, 0); stopVideoButton.Margin = Padding.Empty;
        mediaActions.Controls.Add(playVideoButton); mediaActions.Controls.Add(stopVideoButton);
        visualHost.Dock = DockStyle.Fill;
        visualHost.Controls.Add(previewImage); visualHost.Controls.Add(videoHost); visualHost.Controls.Add(videoLoadingOverlay);
        var chromaHeading = NativePageStyle.Label("COLOR VARIANTS", NativePageStyle.SmallBold, Theme.Muted); chromaHeading.Dock = DockStyle.Fill;
        chromaOptions.Dock = DockStyle.Fill; chromaOptions.MinimumSize = new Size(0, 34); chromaOptions.AutoSize = false;
        var levelHeading = NativePageStyle.Label("UPGRADE LEVELS", NativePageStyle.SmallBold, Theme.Muted); levelHeading.Dock = DockStyle.Fill;
        levelOptions.Dock = DockStyle.Fill; levelOptions.MinimumSize = new Size(0, 34); levelOptions.AutoSize = false;
        previewMessage.Dock = DockStyle.Fill;
        layout.Controls.Add(header, 0, 0);
        layout.Controls.Add(mediaActions, 0, 1);
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
                ? 0 : NativePageStyle.TextHeight(panel, previewMessage.Font, verticalPadding: 6);
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
            return Math.Max(UiMetrics.Px(this, 34), host.Controls.OfType<Label>()
                .Select(label => NativePageStyle.TextHeight(this, label.Font, verticalPadding: 8))
                .DefaultIfEmpty(UiMetrics.Px(this, 34)).Max());
        var available = Math.Max(1, host.ClientSize.Width);
        var columns = available >= UiMetrics.Px(this, 330) ? 3 : available >= UiMetrics.Px(this, 220) ? 2 : 1;
        var rowCount = (buttons.Length + columns - 1) / columns;
        var rowHeight = buttons.Max(button => button.Height + button.Margin.Vertical);
        return rowCount * rowHeight + UiMetrics.Px(this, 2);
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
                SetStatus("SpikeSide上部のRiotログインから接続してください。", false);
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
        stopVideoButton.Click += (_, _) => ShowPreviewImage();
        VisibleChanged += (_, _) =>
        {
            if (Visible) LayoutStoreGrids();
            else if (!Disposing && !IsDisposed) ShowPreviewImage();
        };
    }

    // The grids lay themselves out on resize; this forces a pass after content or visibility changes
    // (e.g. a section or the whole page becoming visible after being hidden at a stale size).
    private void LayoutStoreGrids()
    {
        storeSections.PerformLayout();
        dailyOffers.PerformLayout(); nightOffers.PerformLayout(); bundleCards.PerformLayout(); accessoryCards.PerformLayout();
    }

    private void ShowStoreSection(string key)
    {
        if (ActiveStoreSection() == key) return;
        var next = storeSections.Controls.Cast<Control>().FirstOrDefault(section => Equals(section.Tag, key));
        if (next is null) return;
        next.BringToFront();
        next.Show();
        foreach (Control section in storeSections.Controls)
            if (section != next) section.Hide();
        dailySectionTab.Primary = key == "daily";
        nightSectionTab.Primary = key == "night";
        bundleSectionTab.Primary = key == "bundle";
        accessorySectionTab.Primary = key == "accessories";
        LayoutStoreGrids();
    }

    private Task LoadCatalogAsync(bool refresh)
    {
        if (IsDisposed) return Task.CompletedTask;
        if (loadingCatalog) return catalogLoadTask ?? Task.CompletedTask;
        loadingCatalog = true;
        catalogLoadMessage = "武器カタログを読み込んでいます…";
        refreshCatalogButton.Enabled = false;
        SetStatus(refresh ? "カタログを更新しています…" : "カタログを読み込んでいます…", false);
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
            catalogLoaded = true;
            catalogTextMetricsReady = false;
            MeasureCatalogTextHeights();
            RebuildRewardIndex();
            UpdateCatalogCounts();
            RebuildFilterLists();
            if (catalogTab.Primary) RenderCatalogGrid();
            RenderStorefront();
            SetStatus("", false);
        }
        catch (OperationCanceledException) when (lifetime.IsCancellationRequested) { }
        catch (Exception ex)
        {
            catalogLoadMessage = "武器カタログを読み込めませんでした。ネット接続を確認して再試行してください。";
            NativePageStyle.LogPageError("StoreCatalog", ex);
            SetStatus(catalogLoadMessage, true);
        }
        finally
        {
            loadingCatalog = false;
            refreshCatalogButton.Enabled = true;
            if (!catalogLoaded) RenderCatalogGrid();
        }
    }

    private async Task LoadStorefrontAsync(bool force = false)
    {
        if (!riot.HasAccountSession)
        {
            storefront = null;
            RenderStorefront();
            SetStatus("先にRiotアカウントへ接続してください。", true);
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
            if (generation == storeGeneration) SetStatus("今日のショップの取得がタイムアウトしました。再度更新してください。", true);
        }
        catch (ApiException ex) when (ex.Status == 429)
        {
            if (generation == storeGeneration) SetStatus("ショップの更新が集中しています。少し待ってから再試行してください。", true);
        }
        catch (Exception ex) when (ex is ApiException or HttpRequestException or InvalidDataException or JsonException or InvalidOperationException)
        {
            NativePageStyle.LogPageError("Storefront", ex);
            if (generation == storeGeneration) SetStatus("今日のショップを取得できませんでした。VALORANTのアカウント接続を確認してください。", true);
        }
        catch (Exception ex) when (generation == storeGeneration)
        {
            NativePageStyle.LogPageError("Storefront", ex);
            SetStatus("今日のショップを表示できませんでした。再度更新してください。", true);
        }
        finally
        {
            if (ReferenceEquals(activeStoreLoad, operation)) activeStoreLoad = null;
            if (generation == storeGeneration)
            {
                loadingStore = false;
                refreshStoreButton.Enabled = true;
                EndProgress(progressMessage);
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
            ? $"{riot.AccountName}\n{riot.RegionLabel}"
            : shellAuthentication ? "SpikeSideのログインから接続" : "Riotアカウント未接続";
        loginButton.Visible = !shellAuthentication && !riot.HasAccountSession;
        logoutButton.Visible = !shellAuthentication && riot.HasAccountSession;
        refreshStoreButton.Enabled = riot.HasAccountSession && !loadingStore;
    }

    private void RenderStorefront()
    {
        if (IsDisposed || Disposing) return;
        DisposeFlowChildren(dailyOffers); DisposeFlowChildren(nightOffers); DisposeFlowChildren(bundleCards); DisposeFlowChildren(accessoryCards);
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
                : shellAuthentication ? "SpikeSide上部のRiotログインから接続してください。" : "Riotアカウントに接続すると、今日のショップを表示します。", 500, 110));
            return;
        }

        dailySkeleton.Loading = false;

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
        title.Padding = new Padding(0, 0, night ? UiMetrics.Px(this, 58) : 0, 0);
        FitLabelHeightToCard(card, title);
        var offerImageCandidates = found is { } offerMatch
            ? ArtCandidates(offerMatch.Skin, 0, offerMatch.Level)
            : Enumerable.Empty<string?>();
        var image = new SharpImageBox
        {
            Dock = DockStyle.Fill,
            Fit = ImageFit.Contain,
            Cursor = Cursors.Hand,
            PlaceholderText = "画像なし"
        };
        var weapon = NativePageStyle.Label(skin?.Weapon ?? "詳細データなし", NativePageStyle.Reading, Theme.Muted); weapon.AutoSize = false; weapon.AutoEllipsis = false; weapon.Dock = DockStyle.Bottom; weapon.Height = NativePageStyle.TextHeight(this, weapon.Font, verticalPadding: 4);
        var price = NativePageStyle.Label(Money(night ? offer.DiscountedCost ?? offer.Cost : offer.Cost), NativePageStyle.SmallBold, Theme.Text); price.AutoSize = false; price.Dock = DockStyle.Bottom; price.Height = NativePageStyle.TextHeight(this, price.Font, verticalPadding: 4);
        card.Controls.Add(image); card.Controls.Add(price); card.Controls.Add(weapon); card.Controls.Add(title);
        if (night && offer.DiscountPercent is { } discount)
        {
            var discountLabel = NativePageStyle.Label($"−{discount:0}%", NativePageStyle.SmallBold, Theme.Red); discountLabel.Dock = DockStyle.Right; discountLabel.Width = UiMetrics.Px(this, 54);
            card.Controls.Add(discountLabel); discountLabel.BringToFront();
        }
        void Select(object? _, EventArgs __) => SelectOffer(offer.ItemId);
        card.Click += Select; title.Click += Select; image.Click += Select; weapon.Click += Select; price.Click += Select;
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
        var image = new SharpImageBox { Dock = DockStyle.Fill, Fit = ImageFit.Cover, PlaceholderText = "画像なし" };
        var items = bundle.Items.Where(item => item.ItemTypeId.Equals(SkinLevelType, StringComparison.OrdinalIgnoreCase)).ToArray();
        var detail = NativePageStyle.Label($"{items.Length} 個の武器スキン  ·  {Money(bundle.TotalDiscountedCost ?? bundle.TotalBaseCost)}\n終了まで {FormatRemaining(bundle.RemainingSeconds)}", NativePageStyle.Small, Theme.Muted);
        detail.AutoSize = false; detail.AutoEllipsis = false; detail.Dock = DockStyle.Bottom;
        FitLabelHeightToCard(card, detail);
        card.Controls.Add(image); card.Controls.Add(detail); card.Controls.Add(title);
        _ = LoadStoreImageAsync(image, bannerUrls, StoreImageToken);
        return card;
    }

    private Control CreateAccessoryCard(StorefrontOffer offer)
    {
        StoreItemCard? item = null;
        if (catalogSnapshot is { } snapshot && snapshot.Accessories.TryGetValue(offer.ItemId, out var matchedItem)) item = matchedItem;
        var name = item?.Name ?? $"アクセサリー · {offer.ItemId[..Math.Min(8, offer.ItemId.Length)]}";
        var card = CreateStoreCard();
        var image = new SharpImageBox { Dock = DockStyle.Fill, Fit = ImageFit.Contain, PlaceholderText = "画像なし" };
        var accessoryText = $"{name}\n{item?.Type ?? ItemTypeName(offer.ItemTypeId)}  ·  {Money(offer.Cost)}";
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
        catalogSkeleton.Loading = false;
        UpdateCatalogPageSize(renderAfterChange: false);
        var filtered = FilteredSkins().ToArray();
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
    }

    private void UpdateCatalogPageSize(bool renderAfterChange = true)
    {
        if (catalogGrid.ClientSize.Width <= 0 || catalogGrid.ClientSize.Height <= 0) return;
        var availableWidth = Math.Max(0, catalogGrid.ClientSize.Width - catalogGrid.Padding.Horizontal);
        var availableHeight = Math.Max(0, catalogGrid.ClientSize.Height - catalogGrid.Padding.Vertical);
        var cardWidth = UiMetrics.Px(this, 218);
        var columns = Math.Max(1, availableWidth / Math.Max(1, cardWidth));
        var nextCardWidth = Math.Max(1, availableWidth / columns - UiMetrics.Px(this, 8));
        var widthChanged = catalogCardWidth != nextCardWidth;
        catalogCardWidth = nextCardWidth;
        var oldImageHeight = catalogCardImageHeight;
        var oldTitleHeight = catalogCardTitleHeight;
        var oldSubtitleHeight = catalogCardSubtitleHeight;
        var cardHeight = CatalogCardFlowHeight(FilteredSkins().ToArray());
        var rows = Math.Max(1, availableHeight / Math.Max(1, cardHeight));
        var nextSize = columns * rows;
        var cardSizeChanged = oldImageHeight != catalogCardImageHeight || oldTitleHeight != catalogCardTitleHeight || oldSubtitleHeight != catalogCardSubtitleHeight;
        if (nextSize == catalogPageSize && !cardSizeChanged)
        {
            if (widthChanged)
                foreach (Control card in catalogGrid.Controls)
                    if (card.Tag is SkinCard) card.Width = catalogCardWidth;
            return;
        }
        var previousFirstVisible = catalogPageIndex * catalogPageSize;
        var selectedIndexBeforeResize = selectedSkin is null ? -1 : FilteredSkins().ToList().FindIndex(s => s.Id.Equals(selectedSkin.Id, StringComparison.OrdinalIgnoreCase));
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
        var card = new Panel { Tag = skin, BackColor = Theme.Surface, Margin = new Padding(UiMetrics.Px(this, 4)), BorderStyle = BorderStyle.FixedSingle, Cursor = Cursors.Hand };
        var image = new SharpImageBox { Dock = DockStyle.Top, BackColor = Theme.Elevated, Cursor = Cursors.Hand, PlaceholderText = "画像なし" };
        var title = NativePageStyle.Label(skin.Name, NativePageStyle.SmallBold, Theme.Text); title.AutoSize = false; title.AutoEllipsis = false; title.Dock = DockStyle.Top; title.Padding = new Padding(UiMetrics.Px(this, 4), 0, UiMetrics.Px(this, 4), 0); title.Cursor = Cursors.Hand;
        var subtitle = NativePageStyle.Label(subtitleText, NativePageStyle.Small, Theme.Muted); subtitle.AutoSize = false; subtitle.AutoEllipsis = false; subtitle.Dock = DockStyle.Top; subtitle.Padding = new Padding(UiMetrics.Px(this, 4), 0, UiMetrics.Px(this, 4), 0); subtitle.Cursor = Cursors.Hand;
        card.Controls.Add(subtitle); card.Controls.Add(title); card.Controls.Add(image);
        void Select(object? _, EventArgs __) { selectedSkin = skin; selectedChroma = selectedLevel = 0; UpdateCatalogSelection(); }
        card.Click += Select; image.Click += Select; title.Click += Select; subtitle.Click += Select;
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
        var titleHeight = Math.Max(1, catalogCardTitleHeight);
        var subtitleHeight = Math.Max(1, catalogCardSubtitleHeight);
        card.Controls[0].Height = subtitleHeight;
        card.Controls[1].Height = titleHeight;
        card.Controls[2].Height = imageHeight;
        card.Size = new Size(Math.Max(1, catalogCardWidth), imageHeight + titleHeight + subtitleHeight + UiMetrics.Px(this, 4));
    }

    private void UpdateCatalogSelection()
    {
        foreach (Control card in catalogGrid.Controls)
            card.BackColor = card.Tag is SkinCard skin && skin.Id.Equals(selectedSkin?.Id, StringComparison.OrdinalIgnoreCase)
                ? Color.FromArgb(51, 34, 42) : Theme.Surface;
        RenderPreview();
    }

    private int CatalogCardFlowHeight(IReadOnlyList<SkinCard> skins)
    {
        var minimumImageHeight = UiMetrics.Px(this, 58);
        MeasureCatalogTextHeights();
        var titleHeight = catalogCardTitleHeight > 0 ? catalogCardTitleHeight : NativePageStyle.TextHeight(this, NativePageStyle.SmallBold, lines: 3, verticalPadding: 4);
        var subtitleHeight = catalogCardSubtitleHeight > 0 ? catalogCardSubtitleHeight : NativePageStyle.TextHeight(this, NativePageStyle.Small, lines: 3, verticalPadding: 4);
        var marginHeight = UiMetrics.Px(this, 8);
        var minimumCardHeight = minimumImageHeight + titleHeight + subtitleHeight + UiMetrics.Px(this, 4);
        var availableHeight = Math.Max(1, catalogGrid.ClientSize.Height - catalogGrid.Padding.Vertical);
        var rows = Math.Max(1, availableHeight / Math.Max(1, minimumCardHeight + marginHeight));
        var targetCardHeight = Math.Max(minimumCardHeight, availableHeight / rows - marginHeight);
        catalogCardImageHeight = Math.Max(minimumImageHeight, targetCardHeight - titleHeight - subtitleHeight - UiMetrics.Px(this, 4));
        return catalogCardImageHeight + titleHeight + subtitleHeight + UiMetrics.Px(this, 4) + marginHeight;
    }

    private void MeasureCatalogTextHeights()
    {
        if (catalogTextMetricsReady) return;
        var textWidth = Math.Max(1, UiMetrics.Px(this, 210) - UiMetrics.Px(this, 20));
        catalogCardTitleHeight = allSkins.Count == 0
            ? NativePageStyle.TextHeight(this, NativePageStyle.SmallBold, lines: 3, verticalPadding: 4)
            : allSkins.Max(skin => NativePageStyle.TextBlockHeight(this, skin.Name, NativePageStyle.SmallBold, textWidth, verticalPadding: 4));
        catalogCardSubtitleHeight = allSkins.Count == 0
            ? NativePageStyle.TextHeight(this, NativePageStyle.Small, lines: 3, verticalPadding: 4)
            : allSkins.Max(skin => NativePageStyle.TextBlockHeight(this, $"{skin.Weapon}\n{skin.Tier}", NativePageStyle.Small, textWidth, verticalPadding: 4));
        catalogTextMetricsReady = allSkins.Count > 0;
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
            playVideoButton.Visible = false;
            DisposeFlowChildren(chromaOptions); DisposeFlowChildren(levelOptions);
            return;
        }

        catalogTitle.Text = skin.Name;
        previewImage.PlaceholderText = "画像なし";
        catalogMeta.Text = $"{skin.Weapon}  ·  {skin.Tier}  ·  {CategoryLabel(CategoryKey(skin.Category))}";
        previewMessage.Text = "";
        var imageUrls = ArtCandidates(skin, selectedChroma, selectedLevel);
        previewImageLoad = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token);
        _ = LoadPreviewImageAsync(imageUrls, generation, previewImageLoad.Token);
        BuildOptionButtons(chromaOptions, chromas.Select((item, index) => VariantLabel(skin.Name, item.Name, "カラー", index)).ToArray(), selectedChroma, index => { selectedChroma = index; RenderPreview(); });
        BuildOptionButtons(levelOptions, levels.Select((item, index) => VariantLabel(skin.Name, item.Name, "レベル", index)).ToArray(), selectedLevel, index => { selectedLevel = index; RenderPreview(); });
        resizePreviewLayout?.Invoke();
        var video = SelectedVideo(skin, selectedChroma, selectedLevel);
        playVideoButton.Visible = !string.IsNullOrWhiteSpace(video);
        stopVideoButton.Visible = videoHost.Visible;
        playVideoButton.Tag = video;
    }

    private void BuildOptionButtons(FlowLayoutPanel host, IReadOnlyList<string> labels, int selected, Action<int> select)
    {
        DisposeFlowChildren(host);
        ResizeOptionButtons(host);
        if (labels.Count == 0)
        {
            host.Controls.Add(NativePageStyle.Label("情報がありません。", NativePageStyle.Small, Theme.Muted));
            return;
        }
        for (var i = 0; i < labels.Count; i++)
        {
            var index = i;
            var available = Math.Max(UiMetrics.Px(this, 100), host.ClientSize.Width > 0 ? host.ClientSize.Width : host.Parent?.ClientSize.Width ?? UiMetrics.Px(this, 360));
            var columns = available >= UiMetrics.Px(this, 330) ? 3 : available >= UiMetrics.Px(this, 220) ? 2 : 1;
            var buttonWidth = Math.Min(UiMetrics.Px(this, 148), Math.Max(UiMetrics.Px(this, 82), (available - UiMetrics.Px(this, 16)) / columns - UiMetrics.Px(this, 4)));
            var buttonHeight = available < UiMetrics.Px(this, 420) ? UiMetrics.Px(this, 30) : UiMetrics.Px(this, 36);
            var button = new ThemedButton { Text = labels[i].Length == 0 ? $"{i + 1}" : labels[i], Size = new Size(buttonWidth, buttonHeight), Compact = true, Primary = i == selected, Margin = new Padding(UiMetrics.Px(this, 2)) };
            button.Click += (_, _) => select(index);
            host.Controls.Add(button);
        }
    }

    private void ResizeOptionButtons(FlowLayoutPanel host)
    {
        var available = host.ClientSize.Width > 0 ? host.ClientSize.Width : host.Parent?.ClientSize.Width ?? 0;
        if (available <= 0) return;
        var columns = available >= UiMetrics.Px(this, 330) ? 3 : available >= UiMetrics.Px(this, 220) ? 2 : 1;
        var width = Math.Min(UiMetrics.Px(this, 148), Math.Max(UiMetrics.Px(this, 82), (available - UiMetrics.Px(this, 16)) / columns - UiMetrics.Px(this, 4)));
        var height = available < UiMetrics.Px(this, 420) ? UiMetrics.Px(this, 30) : UiMetrics.Px(this, 36);
        foreach (Control child in host.Controls)
            if (child is ThemedButton button)
            {
                button.Size = new Size(width, height);
                button.Margin = new Padding(UiMetrics.Px(this, 2));
            }
    }

    private static string VariantLabel(string skinName, string variantName, string fallback, int index)
    {
        var label = variantName.Trim();
        if (label.StartsWith(skinName, StringComparison.CurrentCultureIgnoreCase))
            label = label[skinName.Length..].Trim(' ', '-', '–', '—', ':', '：', '·');
        if (fallback == "レベル")
            label = label.Replace("Level", "レベル", StringComparison.OrdinalIgnoreCase);
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
        selectedSkin = found.Value.Skin;
        selectedLevel = found.Value.Level;
        selectedChroma = 0;
        var filtersChanged = searchBox.Text.Length > 0 || categorySelect.SelectedIndex != 0 || weaponSelect.SelectedIndex != 0;
        changingFilters = true;
        searchBox.Clear();
        categorySelect.SelectedIndex = 0;
        weaponSelect.SelectedIndex = 0;
        changingFilters = false;
        if (!catalogTab.Primary)
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
        if (storeTab.Primary) return;
        storePage.Visible = true; catalogPage.Visible = false;
        storeTab.Primary = true; catalogTab.Primary = false;
        ShowPreviewImage();
    }

    private void ShowCatalogPage()
    {
        if (catalogTab.Primary) return;
        ShowPreviewImage();
        storeTab.Primary = false; catalogTab.Primary = true;
        catalogPage.Visible = true; storePage.Visible = false;
        RenderCatalogGrid();
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
            if (!previewImage.Visible) playbackOpened?.TrySetResult(true);
            return true;
        }
        if (videoLoad is { IsCancellationRequested: false }) return false;

        videoLoad?.Cancel();
        videoLoad?.Dispose();
        var operation = CancellationTokenSource.CreateLinkedTokenSource(lifetime.Token, externalToken);
        videoLoad = operation;
        SetStatus(VideoPrepareStatus, false);
        videoLoadingOverlay.Loading = true;
        videoLoadingOverlay.LoadingMessage = "動画を取得しています…";
        previewImage.Visible = false;
        videoLoadingOverlay.Visible = true;
        videoLoadingOverlay.BringToFront();
        stopVideoButton.Text = "キャンセル";
        stopVideoButton.Invalidate();
        stopVideoButton.Visible = true;
        try
        {
            var localFile = await NativeVideoCache.GetLocalFileAsync(url, operation.Token);
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
            videoHost.Visible = true;
            videoHost.BringToFront();
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
        catch (Exception ex) when (ex is InvalidOperationException or UriFormatException or IOException)
        {
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
        videoLoadingOverlay.Loading = false;
        videoLoadingOverlay.Visible = false;
        previewImage.Visible = true;
        previewImage.BringToFront();
        videoHost.Visible = false;
        stopVideoButton.Text = "画像に戻る";
        stopVideoButton.Invalidate();
        stopVideoButton.Visible = false;
    }

    private void StopMedia()
    {
        videoOpenTimer.Stop();
        videoOpening = false;
        videoLoadingOverlay.Loading = false;
        videoLoadingOverlay.Visible = false;
        videoLoad?.Cancel();
        playbackOpened?.TrySetResult(false);
        try { mediaPlayer.Stop(); mediaPlayer.Source = null; }
        catch (InvalidOperationException) { }
        videoHost.Visible = false;
        // A cancelled or superseded video never reaches MediaOpened/MediaFailed, so release its wait state here.
        EndProgress(VideoPrepareStatus);
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
            activeProgressStatus = message;
            SetStatusDisplay("", error: false);
            return;
        }
        activeProgressStatus = "";
        if (!progress)
        {
            settledStatus = message;
            settledStatusError = error;
        }
        SetStatusDisplay(message, error);
    }

    // Ends a progress status that finished without reporting a result (cancel, closed login window, etc.).
    private void EndProgress(string message)
    {
        if (!IsDisposed && !Disposing && activeProgressStatus == message)
        {
            activeProgressStatus = "";
            SetStatus(settledStatus, settledStatusError);
        }
    }

    private void SetStatusDisplay(string message, bool error)
    {
        // Disposing: the Disposed handler runs after child controls are already torn down.
        if (IsDisposed || Disposing || statusLabel.IsDisposed) return;
        statusLabel.Text = message;
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
        if (storefront is null || storefrontFetchedAt == default) return;
        dailyTimerLabel.Text = Remaining(storefront.DailySkins.RemainingSeconds);
        accessoryTimerLabel.Text = Remaining(storefront.Accessories.RemainingSeconds);
        bundleTimerLabel.Text = Remaining(storefront.FeaturedBundles.RemainingSeconds);
        nightTimerLabel.Text = storefront.NightMarket is { } market ? Remaining(market.RemainingSeconds) : "—";
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

    private string ActiveStoreSection() => dailySectionTab.Primary ? "daily" : nightSectionTab.Primary ? "night" : bundleSectionTab.Primary ? "bundle" : accessorySectionTab.Primary ? "accessories" : "daily";

    private static string CategoryKey(string value) => value.Split("::", StringSplitOptions.RemoveEmptyEntries).LastOrDefault() ?? "";
    private static string CategoryLabel(string category) => category switch
    {
        "Sidearm" => "ハンドガン", "SMG" => "サブマシンガン", "Shotgun" => "ショットガン", "Rifle" => "ライフル",
        "Sniper" => "スナイパー", "Heavy" => "ヘビー", "Melee" => "近接武器", _ => category
    };
    private static string ItemTypeName(string typeId) => typeId.ToLowerInvariant() switch
    {
        BuddyType => "ガンバディ", SprayType => "スプレー", PlayerCardType => "プレイヤーカード", _ => "武器スキン"
    };
    private static string Money(IReadOnlyDictionary<string, long>? cost)
    {
        if (cost is null || cost.Count == 0) return "価格不明";
        var first = cost.First();
        return $"{first.Value:N0} { (CurrencyNames.TryGetValue(first.Key, out var name) ? name : "クレジット") }";
    }
    private static string FormatRemaining(long seconds) => $"{Math.Max(0, seconds) / 3600:00}:{Math.Max(0, seconds) % 3600 / 60:00}:{Math.Max(0, seconds) % 60:00}";
    private static ResponsiveCardGrid OfferGrid(double preferredAspect) => new() { Dock = DockStyle.Fill, BackColor = Theme.Surface, PreferredAspect = preferredAspect };

    /// <summary>A rotating accent that only animates while its wait state is visible.</summary>
    private sealed class WaitingIndicator : Control
    {
        private readonly System.Windows.Forms.Timer timer = new() { Interval = 32 };
        private readonly int ringSize;
        private readonly int textGap;
        private readonly int showDelay;
        private bool active;
        private long startedAt;

        [System.ComponentModel.DesignerSerializationVisibility(System.ComponentModel.DesignerSerializationVisibility.Hidden)]
        public bool Active
        {
            get => active;
            set
            {
                if (active == value) return;
                active = value;
                SyncTimer();
                Invalidate();
            }
        }

        // Logical px: ring diameter, optional inline text gap, and a short delay for tiny indicators.
        public WaitingIndicator(int ringSize = 14, int textGap = 6, int showDelay = 150)
        {
            this.ringSize = Math.Max(1, ringSize);
            this.textGap = Math.Max(0, textGap);
            this.showDelay = Math.Max(0, showDelay);
            Size = new Size(this.ringSize + this.textGap, Math.Max(18, this.ringSize));
            AccessibleName = "読み込み中";
            AccessibleRole = AccessibleRole.ProgressBar;
            BackColor = Theme.Background;
            Margin = Padding.Empty;
            SetStyle(ControlStyles.AllPaintingInWmPaint | ControlStyles.OptimizedDoubleBuffer | ControlStyles.UserPaint | ControlStyles.ResizeRedraw, true);
            timer.Tick += (_, _) =>
            {
                if (Environment.TickCount64 - startedAt >= this.showDelay)
                {
                    if (!System.Windows.SystemParameters.ClientAreaAnimation) timer.Stop();
                    Invalidate();
                }
            };
        }

        protected override void OnVisibleChanged(EventArgs e)
        {
            base.OnVisibleChanged(e);
            SyncTimer();
        }

        protected override void OnHandleCreated(EventArgs e)
        {
            base.OnHandleCreated(e);
            UpdateSizeForDpi();
            SyncTimer();
        }

        protected override void OnDpiChangedAfterParent(EventArgs e)
        {
            base.OnDpiChangedAfterParent(e);
            UpdateSizeForDpi();
        }

        private void UpdateSizeForDpi()
        {
            var size = new Size(UiMetrics.Px(this, ringSize + textGap), UiMetrics.Px(this, Math.Max(18, ringSize)));
            if (Size != size) Size = size;
        }

        protected override void OnHandleDestroyed(EventArgs e)
        {
            timer.Stop();
            base.OnHandleDestroyed(e);
        }

        protected override void OnPaint(PaintEventArgs e)
        {
            base.OnPaint(e);
            var elapsed = Environment.TickCount64 - startedAt - showDelay;
            if (!active || elapsed < 0 || ClientSize.Width <= 0 || ClientSize.Height <= 0) return;
            e.Graphics.SmoothingMode = System.Drawing.Drawing2D.SmoothingMode.AntiAlias;
            var stroke = Math.Max(1.4f, DeviceDpi / 96f * 1.8f * ringSize / 14f);
            // Always a true circle: the docked height (status row) differs from the width.
            var box = Math.Min(UiMetrics.Px(this, ringSize), Math.Min(ClientSize.Width - UiMetrics.Px(this, textGap), ClientSize.Height));
            var diameter = box - stroke - 1f;
            if (diameter <= 0) return;
            var bounds = new RectangleF(stroke / 2f + 0.5f, (ClientSize.Height - diameter) / 2f, diameter, diameter);
            using var track = new Pen(Theme.Border, stroke);
            using var arc = new Pen(Theme.Red, stroke) { StartCap = System.Drawing.Drawing2D.LineCap.Round, EndCap = System.Drawing.Drawing2D.LineCap.Round };
            e.Graphics.DrawEllipse(track, bounds);
            var angle = System.Windows.SystemParameters.ClientAreaAnimation ? (elapsed % 1000) * .36f : 0f;
            e.Graphics.DrawArc(arc, bounds, angle, 105f);
        }

        private void SyncTimer()
        {
            if (active && Visible && IsHandleCreated)
            {
                if (!timer.Enabled)
                {
                    startedAt = Environment.TickCount64;
                    timer.Start();
                }
            }
            else timer.Stop();
        }

        protected override void Dispose(bool disposing)
        {
            if (disposing) timer.Dispose();
            base.Dispose(disposing);
        }
    }

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
