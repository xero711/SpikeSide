using System.Diagnostics;
using System.Reflection;
using System.Runtime.InteropServices;
using ValoAutoPick;

internal sealed class TestWindow : DarkForm
{
    protected override bool ShowWithoutActivation => true;
}

internal sealed class RedrawProbe : Panel
{
    public int RedrawEnabledMessages { get; private set; }
    protected override void WndProc(ref Message message)
    {
        if (message.Msg == 0x000B && message.WParam != IntPtr.Zero) RedrawEnabledMessages++;
        base.WndProc(ref message);
    }
}

internal static class Program
{
    [DllImport("dwmapi.dll")]
    private static extern int DwmGetWindowAttribute(IntPtr hwnd, int attribute, out int value, int size);
    private static readonly MethodInfo MouseEnter = typeof(Control).GetMethod("OnMouseEnter", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo MouseLeave = typeof(Control).GetMethod("OnMouseLeave", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo MouseDown = typeof(Control).GetMethod("OnMouseDown", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo MouseClick = typeof(Control).GetMethod("OnMouseClick", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo MouseUp = typeof(Control).GetMethod("OnMouseUp", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo KeyDown = typeof(Control).GetMethod("OnKeyDown", BindingFlags.Instance | BindingFlags.NonPublic)!;
    private static readonly MethodInfo KeyUp = typeof(Control).GetMethod("OnKeyUp", BindingFlags.Instance | BindingFlags.NonPublic)!;

    [STAThread]
    private static void Main()
    {
        ApplicationConfiguration.Initialize();
        var queueName = typeof(MatchHistoryControl).GetMethod("QueueName", BindingFlags.NonPublic | BindingFlags.Static)!;
        Assert((string?)queueName.Invoke(null, ["ggteam"]) == "エスカレーション", "escalation queue uses its Japanese name");
        Assert((string?)queueName.Invoke(null, ["premier"]) == "プレミア", "premier queue uses its Japanese name");
        using var form = new TestWindow { Text = "UI animation checks", ClientSize = new Size(850, 720), Opacity = 0, ShowInTaskbar = false };
        var panel = new FlowLayoutPanel { Dock = DockStyle.Fill, WrapContents = false, FlowDirection = FlowDirection.TopDown,
            BackColor = Color.FromArgb(22, 29, 40) };
        var primary = new ThemedButton { Text = "実行", Primary = true };
        var secondary = new ThemedButton { Text = "設定" };
        var toggle = new SwitchToggle("自動で監視", false);
        var select = new ThemedSelect(["自動", "アジア太平洋（東京など）とても長い選択肢"]) { Size = new Size(220, 48) };
        var tile = new AgentTile(new Agent("1dbf2edd-4729-0984-3115-daa5eed44993", "ジェット", "デュエリスト"));
        var nativeType = typeof(DarkForm).Assembly.GetType("ValoAutoPick.AnimatedNativeButton")!;
        var native = (Button)Activator.CreateInstance(nativeType, true)!;
        native.Text = "設定を解除"; native.Size = new Size(180, 40);
        panel.Controls.AddRange([primary, secondary, toggle, select, tile, native]);
        form.Controls.Add(panel);
        form.Shown += async (_, _) =>
        {
            try
            {
                await Task.Delay(100);
                var assembly = typeof(MatchMapPanel).Assembly;
                var themeType = assembly.GetType("ValoAutoPick.Theme")!;
                var applyTheme = themeType.GetMethod("Apply")!;
                Color ThemeColor(string name) => (Color)themeType.GetProperty(name)!.GetValue(null)!;
                var role = themeType.GetMethod("Role")!;
                var opposing = new Label { ForeColor = ThemeColor("Opposing") };
                var duelist = new Label { ForeColor = (Color)role.Invoke(null, ["デュエリスト"])! };
                panel.Controls.Add(opposing); panel.Controls.Add(duelist);
                var beforeTheme = primary.ForeColor;
                applyTheme.Invoke(null, [new Control[] { form }, "light"]);
                Assert(primary.ForeColor != beforeTheme && toggle.ForeColor == primary.ForeColor, "theme change updates existing controls");
                Assert(opposing.ForeColor == ThemeColor("Opposing") && duelist.ForeColor == (Color)role.Invoke(null, ["デュエリスト"])!, "light theme preserves semantic team and role colors");
                if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) && DwmGetWindowAttribute(form.Handle, 20, out var lightDwm, sizeof(int)) == 0)
                    Assert(lightDwm == 0, "light theme updates native window appearance");
                applyTheme.Invoke(null, [new Control[] { form }, "dark"]);
                Assert(primary.ForeColor == beforeTheme && toggle.ForeColor == beforeTheme, "switching back restores existing controls");
                Assert(opposing.ForeColor == ThemeColor("Opposing") && duelist.ForeColor == (Color)role.Invoke(null, ["デュエリスト"])!, "dark theme restores semantic team and role colors");
                if (OperatingSystem.IsWindowsVersionAtLeast(10, 0, 22000) && DwmGetWindowAttribute(form.Handle, 20, out var darkDwm, sizeof(int)) == 0)
                    Assert(darkDwm == 1, "dark theme updates native window appearance");
                CheckMediaOverlayContrast(applyTheme);
                await CheckVideoProgressDisplay();
                var startupType = assembly.GetType("ValoAutoPick.StartupRegistration")!;
                var setStartup = startupType.GetMethod("SetEnabled", BindingFlags.Static | BindingFlags.NonPublic, [typeof(Microsoft.Win32.RegistryKey), typeof(string), typeof(bool), typeof(string)])!;
                var isStartup = startupType.GetMethod("IsEnabled", BindingFlags.Static | BindingFlags.NonPublic, [typeof(Microsoft.Win32.RegistryKey), typeof(string)])!;
                var testKeyName = @"Software\SpikeSide-StartupTest-" + Guid.NewGuid().ToString("N");
                try
                {
                    using var key = Microsoft.Win32.Registry.CurrentUser.CreateSubKey(testKeyName)!;
                    const string command = "\"C:\\Path With Spaces\\SpikeSide.exe\"";
                    Assert(!(bool)isStartup.Invoke(null, [key, "test"])!, "startup defaults off");
                    setStartup.Invoke(null, [key, "test", true, command]);
                    Assert((bool)isStartup.Invoke(null, [key, "test"])! && (string?)key.GetValue("test") == command, "startup registration stores the quoted command");
                    setStartup.Invoke(null, [key, "test", false, command]);
                    Assert(!(bool)isStartup.Invoke(null, [key, "test"])!, "startup registration can be removed");
                }
                finally { Microsoft.Win32.Registry.CurrentUser.DeleteSubKeyTree(testKeyName, false); }
                await CheckSettingsLifecycle();
                CheckRegionSelectorBounds();
                CheckFavoriteFilterSpacing();
                CheckLiveTracker();
                await CheckClosingWaitsForPendingWork();
                await CheckAtomicUiUpdates();
                CheckStoreNavigationSpacing();
                CheckRrChartScale();
                CheckMainStoreSpacing();
                using (var artworkResource = assembly.GetManifestResourceStream("ValoAutoPick.Assets.SpikeSide.png"))
                {
                    Assert(artworkResource != null, "high resolution logo is embedded");
                    using var artwork = new Bitmap(artworkResource!);
                    Assert(artwork.Width >= 512 && artwork.Height >= 512, "header logo uses high resolution source");
                    Assert(artwork.GetPixel(0, 0).A == 0, "header logo background is transparent");
                }
                foreach (var (key, _) in MapCatalog.Maps)
                {
                    using var asset = assembly.GetManifestResourceStream($"ValoAutoPick.Assets.Maps.{key}.jpg");
                    Assert(asset != null, $"map artwork embedded: {key}");
                    using var decoded = Image.FromStream(asset!);
                    Assert(decoded.Width > 0 && decoded.Height > 0, $"map artwork readable: {key}");
                }
                using (var map = new MatchMapPanel { Size = new Size(500, 250) })
                {
                    map.SetMatch("/Game/Maps/Ascent/Ascent", "ジェット");
                    Assert(map.AccessibleName?.Contains("アセント") == true, "matched map is presented");
                    using var rendered = new Bitmap(map.Width, map.Height);
                    map.DrawToBitmap(rendered, map.ClientRectangle);
                    map.SetMatch("", "");
                    Assert(map.AccessibleName?.Contains("待機中") == true, "map resets to waiting");
                }
                using (var iconResource = assembly.GetManifestResourceStream("ValoAutoPick.Assets.SpikeSide.ico"))
                using (var expected = new Icon(iconResource!))
                using (var expectedBitmap = expected.ToBitmap())
                using (var actualBitmap = form.Icon!.ToBitmap())
                using (var picker = new AgentPickerDialog(Array.Empty<Agent>(), null))
                using (var pickerBitmap = picker.Icon!.ToBitmap())
                {
                    Assert(expectedBitmap.GetPixel(0, 0).A == 0, "application icon has a transparent corner");
                    Assert(expectedBitmap.Size == pickerBitmap.Size && expectedBitmap.Size == actualBitmap.Size, "dialog icon size matches app icon");
                    for (int y = 0; y < expectedBitmap.Height; y++)
                        for (int x = 0; x < expectedBitmap.Width; x++)
                            if (expectedBitmap.GetPixel(x,y).ToArgb() != pickerBitmap.GetPixel(x,y).ToArgb() || expectedBitmap.GetPixel(x,y).ToArgb() != actualBitmap.GetPixel(x,y).ToArgb())
                                throw new InvalidOperationException("dialog icon differs from app icon");
                    Console.WriteLine("PASS agent picker uses app icon");
                }
                var imageType = assembly.GetType("ValoAutoPick.SharpImageBox")!;
                using (var imageBox = (Control)Activator.CreateInstance(imageType, true)!)
                {
                    imageBox.Size = new Size(120, 60);
                    imageBox.BackColor = Color.FromArgb(22, 29, 40);
                    form.Controls.Add(imageBox);
                    imageBox.CreateControl();
                    var sample = new Bitmap(120, 60, System.Drawing.Imaging.PixelFormat.Format32bppArgb);
                    using (var graphics = Graphics.FromImage(sample))
                    {
                        graphics.Clear(Color.Transparent);
                        using var brush = new SolidBrush(Color.FromArgb(180, 240, 30, 60));
                        graphics.FillRectangle(brush, 10, 10, 100, 40);
                    }
                    imageType.GetProperty("Image")!.SetValue(imageBox, sample);
                    using var first = new Bitmap(120, 60);
                    imageBox.DrawToBitmap(first, imageBox.ClientRectangle);
                    await Task.Delay(200);
                    using var later = new Bitmap(120, 60);
                    imageBox.DrawToBitmap(later, imageBox.ClientRectangle);
                    Assert(first.GetPixel(60, 30).ToArgb() != imageBox.BackColor.ToArgb(), "weapon image is visible on its first frame");
                    for (var y = 0; y < 60; y += 3)
                        for (var x = 0; x < 120; x += 3)
                            if (first.GetPixel(x, y).ToArgb() != later.GetPixel(x, y).ToArgb())
                                throw new InvalidOperationException("weapon image changed after its first frame");
                    Console.WriteLine("PASS weapon image remains stable after its first frame");
                    form.Controls.Remove(imageBox);
                }
                using (var main = new MainWindow())
                {
                    var apply = typeof(MainWindow).GetMethod("ApplyAccountLabels", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    var loginButton = (ThemedButton)typeof(MainWindow).GetField("login", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
                    var accountLabel = (Label)typeof(MainWindow).GetField("accountInfo", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
                    var hero = (HeroPanel)typeof(MainWindow).GetField("hero", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
                    var mapButton = (ThemedButton)typeof(MainWindow).GetField("mapSettings", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
                    Assert(!hero.ChooseEnabled, "agent selection is disabled before login");
                    Assert(!mapButton.Enabled, "map agent selection is disabled before login");
                    var riot = (RiotApi)typeof(MainWindow).GetField("api", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
                    var sessionField = typeof(RiotApi).GetField("session", BindingFlags.Instance | BindingFlags.NonPublic)!;
                    var emptySession = sessionField.GetValue(riot)!;
                    var session = emptySession.GetType().GetMethod("<Clone>$")!.Invoke(emptySession, null)!;
                    sessionField.SetValue(riot, session);
                    session.GetType().GetProperty("HasAccountSession")!.SetValue(session, true);
                    typeof(MainWindow).GetField("ownedAgents", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(main, new HashSet<string>());
                    typeof(MainWindow).GetMethod("UpdateConnectionUi", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, null);
                    Assert(hero.ChooseEnabled, "agent selection is enabled for a connected account");
                    Assert(mapButton.Enabled, "map agent selection is enabled when ownership is known");
                    session.GetType().GetProperty("HasAccountSession")!.SetValue(session, false);
                    typeof(MainWindow).GetMethod("UpdateConnectionUi", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, null);
                    Assert(!hero.ChooseEnabled, "agent selection is disabled after logout");
                    Assert(!mapButton.Enabled, "map agent selection is disabled after logout");
                    apply.Invoke(main, [true, "Player#1234"]);
                    Assert(loginButton.Text == "Player#1234" && accountLabel.Text == "接続済み", "connected account shows user name and connected status");
                    apply.Invoke(main, [false, ""]);
                    Assert(loginButton.Text == "Riotにログイン" && accountLabel.Text == "Riotアカウント: 未接続", "signed-out labels remain available");
                    var trayMenu = (ContextMenuStrip)typeof(MainWindow).GetField("trayMenu", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
                    var prefSize = trayMenu.GetPreferredSize(Size.Empty);
                    Assert(prefSize.Width == trayMenu.Width && trayMenu.Width >= 220, "tray menu reserves full width before opening so right side is never clipped");
                    foreach (ToolStripItem item in trayMenu.Items)
                        Assert(item.Width <= trayMenu.Width - 2, $"tray menu item {item.GetType().Name} fits inside menu border");
                    var pickToggle = trayMenu.Items.Cast<ToolStripItem>().FirstOrDefault(i => i.Text == "自動ピック監視");
                    var queueToggle = trayMenu.Items.Cast<ToolStripItem>().FirstOrDefault(i => i.Text == "自動インキュー");
                    Assert(pickToggle != null && queueToggle != null, "tray menu includes pick monitoring and auto-queue toggles");
                    ((NotifyIcon)typeof(MainWindow).GetField("tray", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!).Dispose();
                }
                CheckCareerMetricLayout();
                CheckBundleAndPlayerTitleCards();
                await CheckAutoStopOnValorantExit();
                using (var unavailable = new AgentTile(new Agent("add6443a-41bd-e414-f6ad-e58d267f4e95", "ジェット", "デュエリスト"), available: false))
                {
                    var clicks = 0; unavailable.AgentClicked += (_, _) => clicks++;
                    MouseClick.Invoke(unavailable, [new MouseEventArgs(MouseButtons.Left, 1, 5, 5, 0)]);
                    KeyDown.Invoke(unavailable, [new KeyEventArgs(Keys.Enter)]);
                    KeyDown.Invoke(unavailable, [new KeyEventArgs(Keys.Space)]);
                    Assert(clicks == 0 && !unavailable.TabStop, "unowned agent rejects mouse and keyboard selection");
                    using var rendered = new Bitmap(unavailable.Width, unavailable.Height);
                    unavailable.DrawToBitmap(rendered, unavailable.ClientRectangle);
                }
                using (var restrictedSelect = new ThemedSelect(["通常", "未所有", "所有"], [true, false, true]))
                {
                    restrictedSelect.SelectedIndex = 0;
                    KeyDown.Invoke(restrictedSelect, [new KeyEventArgs(Keys.Down)]);
                    Assert(restrictedSelect.SelectedIndex == 2, "map selector keyboard skips unowned agents");
                }
                var caption = form.Controls.Cast<Control>().Single(c => c.GetType().Name == "WindowCaption");
                var captionButtons = caption.Controls.Cast<Control>().ToArray();
                await CheckMotion("primary button", primary, c => new Point(c.Width / 2, Math.Max(2, c.Height / 6)));
                await CheckMotion("secondary button", secondary, c => new Point(c.Width / 2, Math.Max(2, c.Height / 6)));
                await CheckMotion("switch", toggle, c => new Point(c.Width - Px(c, 11), c.Height / 2));
                await CheckMotion("selection control", select, c => new Point(c.Width / 2, Math.Max(2, c.Height / 6)));
                typeof(ThemedSelect).GetMethod("ShowPicker", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(select, null);
                await Task.Delay(100);
                var menu = (ContextMenuStrip)typeof(ThemedSelect).GetField("picker", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(select)!;
                Assert(menu.Width == select.Width, $"selection menu matches control width ({menu.Width} == {select.Width})");
                menu.Items[1].PerformClick();
                Assert(select.SelectedIndex == 1, "selection menu still selects an item");
                await Task.Delay(100);
                select.SetItems(Enumerable.Range(1, 31).Select(i => $"Agent {i}"));
                typeof(ThemedSelect).GetMethod("ShowPicker", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(select, null);
                await Task.Delay(100);
                menu = (ContextMenuStrip)typeof(ThemedSelect).GetField("picker", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(select)!;
                var workingArea = Screen.FromControl(select).WorkingArea;
                Assert(workingArea.Contains(menu.Bounds), $"31-item picker fits inside the working area ({menu.Bounds})");
                Assert(menu.Width == select.Width, "long selection menu keeps control width");
                menu.Items[^1].Select();
                Application.DoEvents();
                Assert(menu.ClientRectangle.Contains(menu.Items[^1].Bounds), "last picker item can be reached by keyboard selection");
                menu.Close();
                await Task.Delay(100);
                select.SetItems(Enumerable.Range(1, 80).Select(i => $"Agent {i}"));
                typeof(ThemedSelect).GetMethod("ShowPicker", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(select, null);
                await Task.Delay(100);
                menu = (ContextMenuStrip)typeof(ThemedSelect).GetField("picker", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(select)!;
                menu.Items[0].Select();
                var navigate = typeof(ToolStripDropDown).GetMethod("ProcessDialogKey", BindingFlags.Instance | BindingFlags.NonPublic)!;
                for (var i = 1; i < menu.Items.Count; i++) navigate.Invoke(menu, [Keys.Down]);
                Application.DoEvents();
                Assert(workingArea.Contains(menu.Bounds) && menu.Items[^1].Selected && menu.ClientRectangle.Contains(menu.Items[^1].Bounds), $"overflowing picker scrolls to its last item ({menu.Items[^1].Bounds}, {menu.ClientRectangle})");
                menu.Close();
                await CheckMotion("agent tile", tile, c => new Point(Px(c, 8), c.Height / 2));
                await CheckMotion("relay button", native, c => new Point(c.Width / 2, Math.Max(2, c.Height / 6)));
                foreach (var button in captionButtons)
                    await CheckMotion("caption " + button.AccessibleName, button, _ => new Point(4, 4));

                primary.Enabled = false;
                var disabled = Pixel(primary, new Point(primary.Width / 2, primary.Height / 6));
                MouseEnter.Invoke(primary, [EventArgs.Empty]);
                MouseDown.Invoke(primary, [new MouseEventArgs(MouseButtons.Left, 1, 5, 5, 0)]);
                await Task.Delay(200);
                Assert(Distance(disabled, Pixel(primary, new Point(primary.Width / 2, primary.Height / 6))) == 0,
                    "disabled button does not animate");
                var clickCount = 0;
                secondary.Click += (_, _) => clickCount++;
                var keySample = new Point(secondary.Width / 2, secondary.Height / 6);
                var keyIdle = Pixel(secondary, keySample);
                KeyDown.Invoke(secondary, [new KeyEventArgs(Keys.Space)]);
                await Task.Delay(120);
                Assert(Distance(keyIdle, Pixel(secondary, keySample)) > 5, "keyboard press has feedback");
                KeyUp.Invoke(secondary, [new KeyEventArgs(Keys.Space)]);
                Assert(clickCount == 1, "keyboard activation still fires once");
                Console.WriteLine("UI motion checks passed");
            }
            catch (Exception error) { Console.Error.WriteLine(error); Environment.ExitCode = 1; }
            finally { form.Close(); }
        };
        Application.Run(form);
    }

    private static async Task CheckAtomicUiUpdates()
    {
        var assembly = typeof(MatchHistoryControl).Assembly;
        var scopeType = assembly.GetType("ValoAutoPick.PageRedrawScope")!;
        var begin = scopeType.GetMethod("Begin", BindingFlags.Static | BindingFlags.NonPublic)!;
        using var window = new TestWindow { ClientSize = new Size(1000, 720), ShowInTaskbar = false };
        using var parent = new RedrawProbe { Dock = DockStyle.Fill };
        using var child = new RedrawProbe { Dock = DockStyle.Fill };
        parent.Controls.Add(child); window.Controls.Add(parent); window.Show();
        var outer = (IDisposable)begin.Invoke(null, [parent])!;
        var inner = (IDisposable)begin.Invoke(null, [child])!;
        inner.Dispose();
        Assert(child.RedrawEnabledMessages == 0 && parent.RedrawEnabledMessages == 0,
            "nested child redraw does not resume before parent update completes");
        outer.Dispose();
        Assert(parent.RedrawEnabledMessages == 1 && child.RedrawEnabledMessages == 0,
            "outer redraw scope resumes the subtree once");
        var childFirst = (IDisposable)begin.Invoke(null, [child])!;
        var parentSecond = (IDisposable)begin.Invoke(null, [parent])!;
        parentSecond.Dispose();
        Assert(parent.RedrawEnabledMessages == 1, "parent does not redraw while a child scope is active");
        childFirst.Dispose();
        var beforeHidden = child.RedrawEnabledMessages;
        var hiddenScope = (IDisposable)begin.Invoke(null, [child])!;
        child.Visible = false;
        hiddenScope.Dispose();
        hiddenScope.Dispose();
        Assert(child.RedrawEnabledMessages == beforeHidden && !child.Visible, "hidden child is not re-enabled by redraw disposal");
        window.Controls.Remove(parent);

        using var api = new RiotApi();
        using var history = new MatchHistoryControl(api, Array.Empty<Agent>()) { Dock = DockStyle.Fill };
        window.Controls.Add(history);
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var skeleton = typeof(MatchHistoryControl).GetField("historySkeleton", flags)!.GetValue(history)!;
        using var cancellation = new CancellationTokenSource();
        var load = (Task)typeof(MatchHistoryControl).GetMethod("LoadArchiveAsync", flags)!
            .Invoke(history, ["ui-motion-" + Guid.NewGuid().ToString("N"), 0, cancellation])!;
        Assert((bool)skeleton.GetType().GetProperty("Loading", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(skeleton)!,
            "history loading skeleton is active before the first asynchronous yield");
        cancellation.Cancel();
        await load;
        typeof(MatchHistoryControl).GetMethod("ShowMainPage", flags)!.Invoke(history, [false]);
        var labels = (List<(Label Value, Label Caption)>)typeof(MatchHistoryControl).GetField("metricLabels", flags)!.GetValue(history)!;
        Assert(labels.All(pair => pair.Caption.Top >= pair.Value.Bottom && pair.Caption.Bottom <= pair.Value.Parent!.ClientSize.Height),
            "first career tab frame has positioned metric labels");
    }

    private static void CheckMediaOverlayContrast(MethodInfo applyTheme)
    {
        using var api = new RiotApi();
        using var store = new StorePreviewControl(api);
        using var window = new TestWindow { ClientSize = new Size(400, 260), ShowInTaskbar = false };
        var overlay = (Control)typeof(StorePreviewControl).GetField("videoLoadingOverlay", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(store)!;
        overlay.Dock = DockStyle.Fill;
        window.Controls.Add(overlay);
        window.Show();
        overlay.GetType().GetProperty("Loading", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(overlay, true);
        foreach (var theme in new[] { "light", "dark" })
        {
            applyTheme.Invoke(null, [new Control[] { window }, theme]);
            using var image = new Bitmap(overlay.Width, overlay.Height);
            overlay.DrawToBitmap(image, overlay.ClientRectangle);
            var hasLightText = false;
            for (var y = image.Height / 2; y < image.Height && !hasLightText; y++)
                for (var x = 0; x < image.Width; x++)
                {
                    var color = image.GetPixel(x, y);
                    if (color.R > 210 && color.G > 210 && color.B > 210) { hasLightText = true; break; }
                }
            Assert(hasLightText, $"{theme} theme media overlay has readable light text on dark background");
        }
        window.Controls.Remove(overlay);
    }

    private static async Task CheckVideoProgressDisplay()
    {
        using var api = new RiotApi();
        using var store = new StorePreviewControl(api);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var play = (Control)typeof(StorePreviewControl).GetField("playVideoButton", flags)!.GetValue(store)!;
        var status = (Label)typeof(StorePreviewControl).GetField("statusLabel", flags)!.GetValue(store)!;
        Assert(status.Text.Length == 0, "signed-out STORE has no redundant status text");
        var signedOutLoad = (Task)typeof(StorePreviewControl).GetMethod("LoadStorefrontAsync", flags)!.Invoke(store, [false])!;
        Assert(signedOutLoad.IsCompleted && status.Text.Length == 0, "signed-out refresh adds no status text");
        typeof(StorePreviewControl).GetMethod("SetStatus", flags)!.Invoke(store, ["以前の接続エラー", true]);
        store.ResetSession();
        Assert(status.Text.Length == 0, "signing out clears stale STORE errors");
        var overlay = (Control)typeof(StorePreviewControl).GetField("videoLoadingOverlay", flags)!.GetValue(store)!;
        var cache = typeof(StorePreviewControl).Assembly.GetType("ValoAutoPick.NativeVideoCache")!;
        var downloads = (SemaphoreSlim)cache.GetField("Downloads", BindingFlags.Static | BindingFlags.NonPublic)!.GetValue(null)!;
        // A video error must not stay on screen: returning to the image clears it, and so does its timer.
        typeof(StorePreviewControl).GetMethod("ShowVideoError", flags)!.Invoke(store, ["動画を取得できませんでした。ネット接続を確認してください。"]);
        Assert(status.Text.Contains("動画を取得できませんでした"), "video errors are shown");
        typeof(StorePreviewControl).GetMethod("ShowPreviewImage", flags)!.Invoke(store, []);
        Assert(status.Text.Length == 0, "returning to the image clears a video error");
        typeof(StorePreviewControl).GetMethod("ShowVideoError", flags)!.Invoke(store, ["動画を開けませんでした。"]);
        var errorTimer = (System.Windows.Forms.Timer)typeof(StorePreviewControl).GetField("videoErrorTimer", flags)!.GetValue(store)!;
        Assert(errorTimer.Enabled, "a video error starts its clear timer");
        typeof(StorePreviewControl).GetMethod("ClearVideoError", flags)!.Invoke(store, []);
        Assert(status.Text.Length == 0 && !errorTimer.Enabled, "the clear timer removes a video error");
        using var stop = new CancellationTokenSource();
        await downloads.WaitAsync();
        try
        {
            typeof(StorePreviewControl).GetMethod("SetStatus", flags)!.Invoke(store, ["", false]);
            play.Tag = "https://valorant.dyn.riotcdn.net/" + Guid.NewGuid().ToString("N") + ".mp4";
            var task = (Task<bool>)typeof(StorePreviewControl).GetMethod("PlayPreviewVideoAsync", flags)!.Invoke(store, [stop.Token])!;
            Assert(!task.IsCompleted, "video download can remain pending during the check");
            Assert((bool)overlay.GetType().GetProperty("Loading", flags)!.GetValue(overlay)!, "video preview still shows its loading indicator");
            Assert(status.Text.Length == 0, "video loading does not add a status above the catalog");
            stop.Cancel();
            await task;
        }
        finally { downloads.Release(); }
    }

    private static async Task CheckClosingWaitsForPendingWork()
    {
        using var window = new MainWindow(skipBackgroundWork: true) { ShowInTaskbar = false };
        window.Show();
        var pending = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        typeof(MainWindow).GetField("ownedLoad", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(window, pending.Task);
        window.Close();
        window.Close();
        Assert(!window.IsDisposed, "repeated close waits for outstanding account work");
        pending.SetResult();
        for (var i = 0; i < 40 && !window.IsDisposed; i++) await Task.Delay(25);
        Assert(window.IsDisposed, "window closes after pending work completes");
    }

    private static void CheckRegionSelectorBounds()
    {
        using var window = new MainWindow(skipBackgroundWork: true) { ShowInTaskbar = false };
        window.Show();
        var selector = (Control)typeof(MainWindow).GetField("region", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(window)!;
        foreach (var size in new[] { window.Size, window.MinimumSize })
        {
            window.Size = size;
            window.PerformLayout();
            var parent = selector.Parent!;
            Assert(parent.ClientRectangle.Contains(selector.Bounds), $"region selector fits within its row at {size}");
            Assert(selector.Height >= TextRenderer.MeasureText("自動（おすすめ）", selector.Font).Height, "region selector has enough height for its text");
        }
    }

    private static void CheckLiveTracker()
    {
        using var window = new TestWindow { ClientSize = new Size(950, 650), ShowInTaskbar = false, Opacity = 0 };
        var type = typeof(MainWindow).Assembly.GetType("ValoAutoPick.LiveMatchControl")!;
        using var live = (Control)Activator.CreateInstance(type, nonPublic: true)!;
        live.Dock = DockStyle.Fill; window.Controls.Add(live); window.Show(); Application.DoEvents();
        type.GetMethod("ShowPreview", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(live, null);
        var roster = (Control)type.GetField("roster", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(live)!;
        var rosterType = roster.GetType();
        List<object> Rows() => ((System.Collections.IEnumerable)rosterType.GetProperty("Rows")!.GetValue(roster)!).Cast<object>().ToList();
        string RowValue(object row, string name) => row.GetType().GetProperty(name)!.GetValue(row)?.ToString() ?? "";
        Rectangle RowBounds(object row) => (Rectangle)rosterType.GetMethod("RowBounds")!.Invoke(roster, [RowValue(row, "Slot")])!;
        void SelectRow(int index) { rosterType.GetMethod("SelectSlot")!.Invoke(roster, [RowValue(Rows()[index], "Slot")]); Application.DoEvents(); }
        string Arrangement() => rosterType.GetProperty("Arrangement")!.GetValue(roster)!.ToString()!;
        var details = (Label)type.GetField("details", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(live)!;
        Assert(Rows().Count == 2 && details.Text.Contains("Player One"), "live tracker renders a sample match and selected player");
        SelectRow(1);
        Assert(details.Text.Contains("Player Two") && details.Text.Contains("Sage"), "selecting a different player updates live details");
        Assert((string?)rosterType.GetProperty("SelectedSlot")!.GetValue(roster) == RowValue(Rows()[1], "Slot"), "the roster keeps the clicked player selected");
        Assert(Rows().All(row => RowBounds(row).Right <= roster.ClientSize.Width && RowBounds(row).Left >= 0), "live tracker rows fit at a compact window size");
        {
            var half = roster.ClientSize.Height / 2;
            var ally = RowBounds(Rows().Single(row => RowValue(row, "Side") == "Ally"));
            var enemy = RowBounds(Rows().Single(row => RowValue(row, "Side") == "Enemy"));
            Assert(ally.Bottom <= half && enemy.Top >= half, "allies are listed in the top half and opponents in the bottom half");
            Assert(ally.Height >= Px(live, 40) && ally.Height >= TextRenderer.MeasureText("国Ag", roster.Font).Height * 3 / 2,
                "live roster rows are large enough to read");
        }
        var summary = (Label)type.GetField("summary", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(live)!;
        var refresh = live.Controls.Find("liveRefresh", true).Single();
        foreach (var width in new[] { 950, 720, 1300 })
        {
            window.ClientSize = new Size(width, 650);
            window.PerformLayout(); live.PerformLayout();
            var buttonBottom = refresh.PointToScreen(new Point(0, refresh.Height)).Y;
            var summaryTop = summary.Parent!.PointToScreen(Point.Empty).Y;
            Assert(summaryTop - buttonBottom >= Px(live, 6), $"live refresh button leaves a gap above the summary at {width}px");
        }
        var modeName = type.GetMethod("ModeName", BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert((string)modeName.Invoke(null, ["/Game/GameModes/Bomb/BombGameMode.BombGameMode_C"])! == "",
            "shared bomb game mode asset path does not guess competitive vs unrated");
        Assert((string)modeName.Invoke(null, ["/Game/GameModes/QuickBomb/QuickBombGameMode.QuickBombGameMode_C"])! == "スパイクラッシュ",
            "specific game mode asset paths map to Japanese names");
        var liveState = type.GetField("state", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(live)!;
        var pregame = new MatchSnapshot("pregame", "character_select_active", [new Player("self", "agent-a", "locked"), new Player("ally", "", "")], "Ascent");
        liveState.GetType().GetMethod("ReplacePregame")!.Invoke(liveState, [pregame, "self", "Myself#JP", new[] { new Agent("agent-a", "ジェット", "デュエリスト") }]);
        liveState.GetType().GetMethod("ApplyNames")!.Invoke(liveState, [new Dictionary<string, (string GameName, string TagLine)>(StringComparer.OrdinalIgnoreCase) { ["ally"] = ("AllyName", "JP1") }]);
        liveState.GetType().GetMethod("ApplyRanks")!.Invoke(liveState, [new Dictionary<string, LiveRecentRank>(StringComparer.OrdinalIgnoreCase) { ["ally"] = new LiveRecentRank(20, 74) }]);
        type.GetMethod("RenderState", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(live, null);
        Assert(summary.Text.Contains("ピック中") && Rows().Count == 2 && Arrangement() == "Pregame" && (bool)rosterType.GetProperty("ShowsPendingOpponents")!.GetValue(roster)!,
            "live tracker shows the pregame stage with allies on top and opponents marked as not yet available");
        Assert(Rows().All(row => RowValue(row, "Side") == "Ally" && RowBounds(row).Bottom <= roster.ClientSize.Height / 2),
            "pregame lists only real allies, all in the top half, without invented opponents");
        Assert(Rows().Any(row => RowValue(row, "Name") == "Myself#JP" && RowValue(row, "Status") == "確定" && RowValue(row, "AgentId") == "agent-a" && RowValue(row, "Agent") == "ジェット") &&
            Rows().Any(row => RowValue(row, "Name") == "AllyName#JP1" && RowValue(row, "Agent") == "未選択" && RowValue(row, "Rank") == "ダイヤモンド 3" && RowValue(row, "RankTier") == "20"),
            "pregame shows resolved names, rank, agents and selection status");

        // A full 5v5 core game: five rows per half, all inside the list without scrolling; selection survives a re-render.
        const string selfId = "00000000-0000-0000-0000-000000000000";
        var players = Enumerable.Range(0, 10).Select(i => new LiveRosterPlayer(i == 0 ? selfId : $"00000000-0000-0000-0000-00000000000{i}", i < 5 ? "Blue" : "Red", "", false, false, null)).ToList();
        liveState.GetType().GetMethod("ReplaceRoster")!.Invoke(liveState, [new LiveRosterMatch("game-1", "Ascent", "competitive", players), selfId, "Myself#JP", Array.Empty<Agent>()]);
        liveState.GetType().GetMethod("ApplyRanks")!.Invoke(liveState, [new Dictionary<string, LiveRecentRank>(StringComparer.OrdinalIgnoreCase) { [players[6].Subject] = new LiveRecentRank(12, 30) }]);
        type.GetMethod("RenderState", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(live, null);
        {
            var half = roster.ClientSize.Height / 2;
            Assert(Rows().Count == 10 && Arrangement() == "Teams", "a 5v5 match lists all ten players");
            Assert(Rows().Where(row => RowValue(row, "Side") == "Ally").All(row => RowBounds(row).Bottom <= half) &&
                Rows().Where(row => RowValue(row, "Side") == "Enemy").All(row => RowBounds(row).Top >= half && RowBounds(row).Bottom <= roster.ClientSize.Height),
                "five allies fit the top half and five opponents fit the bottom half");
            Assert(Rows().Count(row => RowValue(row, "Side") == "Ally") == 5 && RowValue(Rows()[0], "IsLocal") == "True", "the local player leads the ally half");
            Assert(Rows().Any(row => RowValue(row, "RankTier") == "12" && RowValue(row, "Side") == "Enemy"), "a fetched rank tier reaches the roster row");
            var enemyIndex = Rows().FindIndex(row => RowValue(row, "Side") == "Enemy");
            SelectRow(enemyIndex);
            var chosen = RowValue(Rows()[enemyIndex], "Slot");
            type.GetMethod("RenderState", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(live, null);
            Assert((string?)rosterType.GetProperty("SelectedSlot")!.GetValue(roster) == chosen && details.Text.Contains("相手"), "a re-render keeps the selected player and details");
        }
        // Deathmatch and unknown teams: nobody is dropped.
        var ffa = Enumerable.Range(0, 14).Select(i => new LiveRosterPlayer(i == 0 ? selfId : $"00000000-0000-0000-0000-0000000000{i:00}", $"team-{i}", "", false, false, null)).ToList();
        liveState.GetType().GetMethod("ReplaceRoster")!.Invoke(liveState, [new LiveRosterMatch("game-2", "Ascent", "/Game/GameModes/Deathmatch/DeathmatchGameMode.DeathmatchGameMode_C", ffa), selfId, "Myself#JP", Array.Empty<Agent>()]);
        type.GetMethod("RenderState", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(live, null);
        Assert(Rows().Count == 14 && Arrangement() == "FreeForAll", "deathmatch lists all fourteen players in one group");
        var unknown = Enumerable.Range(0, 4).Select(i => new LiveRosterPlayer(i == 0 ? selfId : $"00000000-0000-0000-0000-00000000000{i}", "", "", false, false, null)).ToList();
        liveState.GetType().GetMethod("ReplaceRoster")!.Invoke(liveState, [new LiveRosterMatch("game-3", "Ascent", "", unknown), selfId, "Myself#JP", Array.Empty<Agent>()]);
        type.GetMethod("RenderState", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(live, null);
        Assert(Rows().Count == 4 && Rows().All(row => RowValue(row, "Side") == "Unknown"), "players without a known team are still listed");

        type.GetMethod("ResetSession", BindingFlags.Instance | BindingFlags.Public)!.Invoke(live, null);
        Assert(Rows().Count == 0 && !summary.Text.Contains("アセント") && !details.Text.Contains("Player Two"),
            "resetting live session clears stale match summary, roster, and selected player details");

        // A failed refresh holds a young roster (labelled, score hidden) but drops an old one together with the selection.
        const BindingFlags nonPublic = BindingFlags.Instance | BindingFlags.NonPublic;
        var lastSuccess = type.GetField("lastSuccess", nonPublic)!;
        var connection = (Label)type.GetField("connection", nonPublic)!.GetValue(live)!;
        var showPreview = type.GetMethod("ShowPreview", nonPublic)!;
        var keepOrDrop = type.GetMethod("KeepOrDropSnapshot", nonPublic)!;
        var expire = type.GetMethod("ExpireStaleSnapshot", nonPublic)!;
        var render = type.GetMethod("RenderState", nonPublic)!;
        showPreview.Invoke(live, null);
        SelectRow(1);
        lastSuccess.SetValue(live, (DateTimeOffset?)DateTimeOffset.UtcNow);
        keepOrDrop.Invoke(live, ["試合情報を取得できませんでした。再試行します"]); render.Invoke(live, null);
        Assert(Rows().Count == 2 && details.Text.Contains("Player Two"), "a failed refresh keeps a young roster and the selected player");
        Assert(summary.Text.Contains("再接続待ち") && !summary.Text.Contains("4 : 3"), "a held roster says it is waiting to reconnect and hides the score");
        Assert(connection.Text.Contains("時点の情報を表示中"), "a held roster states when it was last confirmed");
        Assert(!(bool)expire.Invoke(live, null)!, "a young roster is not expired by the render tick");
        lastSuccess.SetValue(live, (DateTimeOffset?)DateTimeOffset.UtcNow.AddMinutes(-10));
        Assert((bool)expire.Invoke(live, null)!, "a roster nobody refreshed for minutes expires, for example after a long 429 wait or a hidden tab");
        render.Invoke(live, null);
        Assert(Rows().Count == 0 && !details.Text.Contains("Player Two") && !summary.Text.Contains("アセント"), "an expired roster clears the list, the selection and the match summary");
        showPreview.Invoke(live, null);
        lastSuccess.SetValue(live, (DateTimeOffset?)DateTimeOffset.UtcNow.AddMinutes(-10));
        keepOrDrop.Invoke(live, ["試合情報を取得できませんでした。再試行します"]); render.Invoke(live, null);
        Assert(Rows().Count == 0 && !details.Text.Contains("Player One"), "a failed refresh drops a roster that is already too old");
        window.Close();
    }

    private static void CheckFavoriteFilterSpacing()
    {
        using var api = new RiotApi();
        using var window = new TestWindow { ClientSize = new Size(1300, 800), ShowInTaskbar = false };
        using var history = new MatchHistoryControl(api, Array.Empty<Agent>()) { Dock = DockStyle.Fill };
        window.Controls.Add(history);
        window.Show();
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var filter = (Control)typeof(MatchHistoryControl).GetField("favoritesFilter", flags)!.GetValue(history)!;
        var title = (Label)typeof(MatchHistoryControl).GetField("listTitle", flags)!.GetValue(history)!;
        var count = (Label)typeof(MatchHistoryControl).GetField("matchCount", flags)!.GetValue(history)!;
        var list = (Control)typeof(MatchHistoryControl).GetField("matchList", flags)!.GetValue(history)!;
        var content = (Control)typeof(MatchHistoryControl).GetField("historyContent", flags)!.GetValue(history)!;
        count.Text = "29 試合";
        content.Visible = true;
        foreach (var width in new[] { 1300, 950 })
        {
            window.ClientSize = new Size(width, 800);
            window.PerformLayout();
            var header = filter.Parent!;
            header.PerformLayout();
            var bottom = filter.PointToScreen(new Point(0, filter.Height)).Y;
            var listTop = list.PointToScreen(Point.Empty).Y;
            Assert(listTop - bottom >= Px(history, 3), $"favorite filter has a gap above the match list at {width}px");
            Assert(header.ClientRectangle.Contains(filter.Bounds), $"favorite filter fits inside its header at {width}px");
            Assert(title.Width >= TextRenderer.MeasureText(title.Text, title.Font, Size.Empty, TextFormatFlags.NoPadding).Width, $"match-history heading stays on one line at {width}px");
        }
    }

    private static void CheckMainStoreSpacing()
    {
        using var main = new MainWindow { ShowInTaskbar = false };
        main.Show();
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var method = typeof(MainWindow).GetMethod("ShowMainPage", flags)!;
        var page = Enum.Parse(method.GetParameters()[0].ParameterType, "Store");
        method.Invoke(main, [page]);
        var store = (StorePreviewControl)typeof(MainWindow).GetField("storePreview", flags)!.GetValue(main)!;
        var tabs = (Control)typeof(StorePreviewControl).GetField("catalogTab", flags)!.GetValue(store)!;
        var left = (Control)typeof(StorePreviewControl).GetField("storeTab", flags)!.GetValue(store)!;
        var daily = (Control)typeof(StorePreviewControl).GetField("dailySectionTab", flags)!.GetValue(store)!;
        var search = (Control)typeof(StorePreviewControl).GetField("searchBox", flags)!.GetValue(store)!;
        void CheckGap(Control content, string stage)
        {
            var vertical = content.PointToScreen(Point.Empty).Y - tabs.PointToScreen(new Point(0, tabs.Height)).Y;
            var horizontal = tabs.PointToScreen(Point.Empty).X - left.PointToScreen(new Point(left.Width, 0)).X;
            Assert(vertical == horizontal, $"main window {stage} spacing matches ({vertical} == {horizontal})");
        }
        CheckGap(daily, "initial store");
        Application.DoEvents();
        CheckGap(daily, "store after events");
        typeof(StorePreviewControl).GetMethod("ShowCatalogPage", flags)!.Invoke(store, null);
        CheckGap(search, "catalog");
        typeof(StorePreviewControl).GetMethod("ShowStorePage", flags)!.Invoke(store, null);
        CheckGap(daily, "returned store");
    }

    private static void CheckStoreNavigationSpacing()
    {
        using var api = new RiotApi();
        using var window = new TestWindow { ClientSize = new Size(1400, 800), ShowInTaskbar = false };
        using var store = new StorePreviewControl(api) { Dock = DockStyle.Fill };
        window.Controls.Add(store);
        window.Show();
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var tabs = (Control)typeof(StorePreviewControl).GetField("catalogTab", flags)!.GetValue(store)!;
        var storeTab = (Control)typeof(StorePreviewControl).GetField("storeTab", flags)!.GetValue(store)!;
        var search = (Control)typeof(StorePreviewControl).GetField("searchBox", flags)!.GetValue(store)!;
        var actions = ((Control)typeof(StorePreviewControl).GetField("refreshCatalogButton", flags)!.GetValue(store)!).Parent!;
        var status = (Label)typeof(StorePreviewControl).GetField("statusLabel", flags)!.GetValue(store)!;
        const string otherProgress = "今日のショップを取得しています…";
        typeof(StorePreviewControl).GetMethod("SetStatus", flags)!.Invoke(store, [otherProgress, false]);
        typeof(StorePreviewControl).GetMethod("ShowCatalogPage", flags)!.Invoke(store, null);
        Assert(status.Text == otherProgress, "catalog loading does not replace another operation's status");
        typeof(StorePreviewControl).GetMethod("EndProgress", flags)!.Invoke(store, [otherProgress]);
        Assert(!status.Text.Contains("カタログを読み込んでいます", StringComparison.Ordinal) &&
               !status.Text.Contains("カタログを更新しています", StringComparison.Ordinal), "catalog loading adds no top status message");
        var sawStacked = false;
        foreach (var width in new[] { 1400, 800, 500, 1400 })
        {
            window.ClientSize = new Size(width, 800);
            store.ResetSession();
            window.PerformLayout();
            var navigationBefore = tabs.PointToScreen(Point.Empty);
            var contentBefore = search.PointToScreen(Point.Empty);
            foreach (var message in new[] { "Riotアカウントへ接続しています…", "今日のショップを取得しています…", "今日のショップを取得できませんでした。VALORANTのアカウント接続を確認してください。" })
            {
                typeof(StorePreviewControl).GetMethod("SetStatus", flags)!.Invoke(store, [message, message.Contains("できませんでした", StringComparison.Ordinal)]);
                window.PerformLayout();
                Assert(tabs.PointToScreen(Point.Empty) == navigationBefore && search.PointToScreen(Point.Empty) == contentBefore,
                    $"status does not shift navigation or catalog at {width}px");
                Assert(status.Text == message, "status remains available when the layout is stable");
                var statusRow = status.Parent!;
                var header = statusRow.Parent!;
                var counts = header.Controls.Cast<Control>().Single(control => control.AccessibleName == "カタログの収録件数");
                var wallet = header.Controls.Cast<Control>().Single(control => control.AccessibleName == "所持クレジット");
                Assert(statusRow.Bottom <= header.ClientSize.Height && statusRow.Right <= counts.Left,
                    $"status fits beside catalog counts at {width}px");
                Assert(wallet.Right <= header.ClientSize.Width && wallet.Bottom <= header.ClientSize.Height &&
                       !wallet.Bounds.IntersectsWith(counts.Bounds) && !wallet.Bounds.IntersectsWith(statusRow.Bounds),
                    $"wallet card fits without overlapping header elements at {width}px");
                typeof(StorePreviewControl).GetMethod("EndProgress", flags)!.Invoke(store, [message]);
            }
            var tabsBottom = tabs.PointToScreen(new Point(0, tabs.Height)).Y;
            var actionsBottom = actions.PointToScreen(new Point(0, actions.Height)).Y;
            var searchTop = search.PointToScreen(Point.Empty).Y;
            var gap = searchTop - tabsBottom;
            var horizontalGap = tabs.PointToScreen(Point.Empty).X - storeTab.PointToScreen(new Point(storeTab.Width, 0)).X;
            Assert(gap == horizontalGap, $"catalog vertical and horizontal gaps match at {width}px ({gap}px)");
            var tabsTop = tabs.PointToScreen(Point.Empty).Y;
            if (actionsBottom <= tabsTop)
            {
                sawStacked = true;
                Assert(tabsTop - actionsBottom <= Px(store, 15), "stacked actions and tabs have a compact gap");
            }
            else Assert(searchTop >= actionsBottom, "search does not overlap the navigation actions");
        }
        Assert(sawStacked, "navigation spacing is checked with stacked actions");
    }

    private static void CheckBundleAndPlayerTitleCards()
    {
        var flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var asm = typeof(StorePreviewControl).Assembly;
        object New(string typeName, params object?[] args) => Activator.CreateInstance(asm.GetType("ValoAutoPick." + typeName)!, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic, null, args, null)!;
        object TypedList(string itemTypeName, params object[] items)
        {
            var list = Activator.CreateInstance(typeof(List<>).MakeGenericType(asm.GetType("ValoAutoPick." + itemTypeName)!))!;
            var add = list.GetType().GetMethod("Add")!;
            foreach (var item in items) add.Invoke(list, [item]);
            return list;
        }
        var skin = New("SkinData");
        skin.GetType().GetProperty("Uuid")!.SetValue(skin, "skin-1");
        skin.GetType().GetProperty("DisplayName")!.SetValue(skin, "スノーフォール");
        var weapon = New("WeaponData");
        weapon.GetType().GetProperty("DisplayName")!.SetValue(weapon, "ヴァンダル");
        weapon.GetType().GetProperty("Category")!.SetValue(weapon, "EEquippableCategory::Rifle");
        weapon.GetType().GetProperty("DisplayIcon")!.SetValue(weapon, "https://media.valorant-api.com/weapons/vandal/displayicon.png");
        weapon.GetType().GetProperty("Skins")!.SetValue(weapon, TypedList("SkinData", skin));
        var bundleData = New("BundleData");
        bundleData.GetType().GetProperty("Uuid")!.SetValue(bundleData, "bundle-1");
        bundleData.GetType().GetProperty("DisplayName")!.SetValue(bundleData, "スノーフォール");
        bundleData.GetType().GetProperty("DisplayIcon")!.SetValue(bundleData, "https://media.valorant-api.com/bundles/bundle-1/displayicon.png");
        bundleData.GetType().GetProperty("DisplayIcon2")!.SetValue(bundleData, "https://media.valorant-api.com/bundles/bundle-1/displayicon2.png");
        bundleData.GetType().GetProperty("VerticalPromoImage")!.SetValue(bundleData, "https://media.valorant-api.com/bundles/bundle-1/verticalpromoimage.png");
        var titleData = New("PlayerTitleData");
        titleData.GetType().GetProperty("Uuid")!.SetValue(titleData, "title-1");
        titleData.GetType().GetProperty("DisplayName")!.SetValue(titleData, "スノーフォール タイトル");
        titleData.GetType().GetProperty("TitleText")!.SetValue(titleData, "スノーフォール");

        var payload = New("CatalogCachePayload",
            DateTimeOffset.UtcNow,
            TypedList("WeaponData", weapon),
            TypedList("ContentTierData"),
            TypedList("BundleData", bundleData),
            TypedList("BuddyData"),
            TypedList("AccessoryData"),
            TypedList("AccessoryData"),
            TypedList("PlayerTitleData", titleData));
        var snapshot = asm.GetType("ValoAutoPick.CatalogClient")!
            .GetMethod("CreateSnapshot", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [payload, false])!;
        Assert((bool)snapshot.GetType().GetProperty("HasPlayerTitles")!.GetValue(snapshot)!,
            "catalog snapshot marks player titles available");
        var bundlesDict = snapshot.GetType().GetProperty("Bundles")!.GetValue(snapshot)!;
        var bundleInfo = bundlesDict.GetType().GetProperty("Item")!.GetValue(bundlesDict, ["bundle-1"])!;
        var bundleImages = (IReadOnlyList<string>)bundleInfo.GetType().GetProperty("Images")!.GetValue(bundleInfo)!;
        Assert(bundleImages[0].EndsWith("/displayicon.png", StringComparison.Ordinal),
            "bundle card prefers horizontal displayIcon over vertical promo image");
        var accessoriesDict = snapshot.GetType().GetProperty("Accessories")!.GetValue(snapshot)!;
        var titleCard = accessoriesDict.GetType().GetProperty("Item")!.GetValue(accessoriesDict, ["title-1"])!;
        Assert((string)titleCard.GetType().GetProperty("Type")!.GetValue(titleCard)! == "プレイヤータイトル" &&
               (string?)titleCard.GetType().GetProperty("TitleText")!.GetValue(titleCard) == "スノーフォール",
            "catalog snapshot indexes player titles into accessories");

        using var api = new RiotApi();
        using var window = new TestWindow { ClientSize = new Size(1400, 800), ShowInTaskbar = false };
        using var store = new StorePreviewControl(api) { Dock = DockStyle.Fill };
        typeof(StorePreviewControl).GetField("catalogSnapshot", flags)!.SetValue(store, snapshot);
        window.Controls.Add(store);
        window.Show();

        var bundle = new StorefrontBundle("id-1", "bundle-1", [],
            new Dictionary<string, long> { ["85ad13f7-3d1b-5128-9eb2-7cd8ee0b5741"] = 5100 },
            new Dictionary<string, long> { ["85ad13f7-3d1b-5128-9eb2-7cd8ee0b5741"] = 5100 }, 0, 3600);
        using var bundleCard = (Control)typeof(StorePreviewControl).GetMethod("CreateBundleCard", flags)!.Invoke(store, [bundle])!;
        var bundleImage = bundleCard.Controls.Cast<Control>().Single(c => c.GetType().Name == "SharpImageBox");
        var fit = bundleImage.GetType().GetProperty("Fit")!.GetValue(bundleImage)!.ToString();
        Assert(fit == "Contain", $"bundle image preserves full aspect ratio with Contain (got {fit})");

        var titleOffer = new StorefrontOffer("offer-title", "title-1", "de7caa6b-adf7-4588-bbd1-143831e786c6",
            new Dictionary<string, long> { ["85ca954a-41f2-ce94-9b45-8ca3dd39a00d"] = 5000 });
        using var accessoryCard = (Control)typeof(StorePreviewControl).GetMethod("CreateAccessoryCard", flags)!.Invoke(store, [titleOffer])!;
        accessoryCard.Size = new Size(280, 260);
        accessoryCard.PerformLayout();
        Assert(!accessoryCard.Controls.Cast<Control>().Any(c => c.GetType().Name == "SharpImageBox"),
            "player title accessory card does not render an empty image placeholder");
        var plate = accessoryCard.Controls.Cast<Control>().Single(c => c.AccessibleName == "プレイヤータイトルのプレビュー");
        Assert(plate.Controls.OfType<Label>().Any(l => l.Text == "スノーフォール"),
            "player title preview displays the title text");
        Assert(accessoryCard.Controls.OfType<Label>().Any(l => l.Text.Contains("プレイヤータイトル") && l.Text.Contains("5,000 KC")),
            "player title accessory caption displays type and cost");
    }

    private static void CheckCareerMetricLayout()
    {
        using var api = new RiotApi();
        using var window = new TestWindow { ClientSize = new Size(1200, 760), ShowInTaskbar = false };
        using var history = new MatchHistoryControl(api, Array.Empty<Agent>()) { Dock = DockStyle.Fill };
        window.Controls.Add(history);
        window.Show();
        typeof(MatchHistoryControl).GetMethod("ShowMainPage", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(history, [false]);
        var labels = (List<(Label Value, Label Caption)>)typeof(MatchHistoryControl)
            .GetField("metricLabels", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(history)!;
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.NonPublic;
        var filter = (Control)typeof(MatchHistoryControl).GetField("modeFilter", flags)!.GetValue(history)!;
        var historyTab = (Control)typeof(MatchHistoryControl).GetField("historyTab", flags)!.GetValue(history)!;
        var matchList = (Control)typeof(MatchHistoryControl).GetField("matchList", flags)!.GetValue(history)!;
        void CheckNavigationGaps(Control firstCard)
        {
            var filterBottom = filter.PointToScreen(new Point(0, filter.Height)).Y;
            var tabTop = historyTab.PointToScreen(Point.Empty).Y;
            var tabBottom = historyTab.PointToScreen(new Point(0, historyTab.Height)).Y;
            var cardTop = firstCard.PointToScreen(Point.Empty).Y;
            Assert(Math.Abs((tabTop - filterBottom) - (cardTop - tabBottom)) <= 1,
                $"filter, tabs, and card have equal vertical gaps ({tabTop - filterBottom}, {cardTop - tabBottom})");
        }
        typeof(MatchHistoryControl).GetMethod("SetMetrics", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(history,
            [new (string, string)[] { ("13W 5L", "勝敗 · 18 試合"), ("72.2%", "勝率"), ("309", "平均ACS"), ("1.33", "通算K/D"), ("613.7", "平均ADR"), ("24.5%", "平均HS%"), ("4W · 80.0%", "直近5戦 · 5戦"), ("9W · 90.0%", "直近10戦 · 10戦") }]);

        void CheckCards()
        {
            window.PerformLayout();
            foreach (var (value, caption) in labels)
            {
                var card = value.Parent!;
                Assert(value.Top >= 0 && caption.Top >= value.Bottom && caption.Bottom <= card.ClientSize.Height,
                    "career metric value and caption fit inside the card");
            }
        }

        CheckCards();
        CheckNavigationGaps(labels[0].Value.Parent!);
        typeof(MatchHistoryControl).GetMethod("ShowMainPage", flags)!.Invoke(history, [true]);
        ((Control)typeof(MatchHistoryControl).GetField("historyContent", flags)!.GetValue(history)!).Visible = true;
        window.PerformLayout();
        CheckNavigationGaps(matchList.Parent!);
        typeof(MatchHistoryControl).GetMethod("ShowMainPage", flags)!.Invoke(history, [false]);
        var capturePath = Environment.GetEnvironmentVariable("SPIKESIDE_METRICS_CAPTURE");
        if (!string.IsNullOrEmpty(capturePath))
        {
            var grid = labels[0].Value.Parent!.Parent!;
            using var bitmap = new Bitmap(grid.Width, grid.Height);
            grid.DrawToBitmap(bitmap, grid.ClientRectangle);
            bitmap.Save(capturePath);
        }
        window.ClientSize = new Size(1600, 860);
        CheckCards();
        CheckNavigationGaps(labels[0].Value.Parent!);
        using var largerValue = new Font(labels[0].Value.Font.FontFamily, 24, FontStyle.Bold);
        using var largerCaption = new Font(labels[0].Caption.Font.FontFamily, 14);
        foreach (var (value, caption) in labels) { value.Font = largerValue; caption.Font = largerCaption; }
        CheckCards();
    }

    private static int Px(Control control, int value) => (int)Math.Round(value * control.DeviceDpi / 96f * .7f);
    private static async Task CheckSettingsLifecycle()
    {
        using var main = new MainWindow(skipBackgroundWork: true) { ShowInTaskbar = false };
        main.Show();
        await Task.Delay(100);
        var showSettings = typeof(MainWindow).GetMethod("ShowSettings", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var restore = typeof(MainWindow).GetMethod("Restore", BindingFlags.Instance | BindingFlags.NonPublic)!;
        Exception? error = null;
        var phase = 0;
        using (var timer = new System.Windows.Forms.Timer { Interval = 100 })
        {
            timer.Tick += (_, _) =>
            {
                var dialog = main.OwnedForms.FirstOrDefault(f => f.Modal && !f.IsDisposed);
                if (dialog == null) return;
                try
                {
                    if (phase++ == 0)
                    {
                        Assert(!dialog.MinimizeBox, "settings cannot be minimized independently of its owner");
                        main.WindowState = FormWindowState.Minimized;
                        restore.Invoke(main, null);
                        Assert(main.Visible && dialog.Visible && dialog.WindowState != FormWindowState.Minimized,
                            "tray restore brings the open settings dialog back with its owner");
                    }
                    else
                    {
                        Assert(IsAbove(dialog, main), "tray restore keeps the modal settings dialog above its owner");
                        timer.Stop();
                        var caption = dialog.Controls.Cast<Control>().Single(c => c.GetType().Name == "WindowCaption");
                        var close = caption.Controls.Cast<Control>().Single(c => c.AccessibleName == "閉じる");
                        typeof(Control).GetMethod("OnClick", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(close, [EventArgs.Empty]);
                    }
                }
                catch (Exception ex) { error = ex; timer.Stop(); dialog.Close(); }
            };
            timer.Start();
            showSettings.Invoke(main, null);
        }
        if (error != null) throw new InvalidOperationException("settings lifecycle failed", error);
        Assert(phase >= 2 && main.Visible && main.Enabled && !main.IsDisposed,
            "settings caption close leaves the main window open and usable");
        using (var timer = new System.Windows.Forms.Timer { Interval = 100 })
        {
            timer.Tick += (_, _) =>
            {
                var dialog = main.OwnedForms.FirstOrDefault(f => f.Modal && !f.IsDisposed);
                if (dialog == null) return;
                timer.Stop();
                dialog.CancelButton!.PerformClick();
            };
            timer.Start();
            showSettings.Invoke(main, null);
        }
        Assert(main.Visible && main.Enabled && !main.IsDisposed, "settings close button leaves the main window usable");
        main.Close();
        await Task.Delay(100);
    }

    private static async Task CheckAutoStopOnValorantExit()
    {
        var running = true;
        using var main = new MainWindow(skipBackgroundWork: true, valorantRunning: () => running) { ShowInTaskbar = false };
        var riot = (RiotApi)typeof(MainWindow).GetField("api", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
        var sessionField = typeof(RiotApi).GetField("session", BindingFlags.Instance | BindingFlags.NonPublic)!;
        var emptySession = sessionField.GetValue(riot)!;
        var session = emptySession.GetType().GetMethod("<Clone>$")!.Invoke(emptySession, null)!;
        const string subject = "11111111-1111-1111-1111-111111111111";
        session.GetType().GetProperty("HasAccountSession")!.SetValue(session, true);
        session.GetType().GetProperty("Subject")!.SetValue(session, subject);
        session.GetType().GetProperty("Server")!.SetValue(session, "https://glz-ap-1.ap.a.pvp.net");
        session.GetType().GetProperty("PlayerDataServer")!.SetValue(session, "https://pd.ap.a.pvp.net");
        session.GetType().GetProperty("Access")!.SetValue(session, "fake-access");
        session.GetType().GetProperty("Entitlement")!.SetValue(session, "fake-entitlement");
        session.GetType().GetProperty("Version")!.SetValue(session, "release-10.00");
        session.GetType().GetProperty("ExpiresAt")!.SetValue(session, DateTimeOffset.UtcNow.AddHours(1));
        sessionField.SetValue(riot, session);

        const string baseAgentId = "add6443a-41bd-e414-f6ad-e58d267f4e95";
        typeof(MainWindow).GetField("ownedAgents", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(main, new HashSet<string>(StringComparer.OrdinalIgnoreCase) { baseAgentId });
        typeof(MainWindow).GetField("ownedSubject", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(main, subject);
        var agents = (List<Agent>)typeof(MainWindow).GetField("agents", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
        typeof(MainWindow).GetField("mainAgent", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(main, agents.First(a => a.Id.Equals(baseAgentId, StringComparison.OrdinalIgnoreCase)));

        typeof(MainWindow).GetField("pickMonitoring", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(main, true);
        typeof(MainWindow).GetField("autoQueueActive", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(main, true);
        typeof(MainWindow).GetMethod("StartMonitor", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(main, null);

        var trayMenu = (ContextMenuStrip)typeof(MainWindow).GetField("trayMenu", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
        var pickToggle = trayMenu.Items.Cast<ToolStripItem>().First(i => i.Text == "自動ピック監視");
        var queueToggle = trayMenu.Items.Cast<ToolStripItem>().First(i => i.Text == "自動インキュー");
        var statusItem = trayMenu.Items[1];
        var isOnProp = pickToggle.GetType().GetProperty("IsOn")!;
        var activeProp = statusItem.GetType().GetProperty("Active")!;
        var warningProp = statusItem.GetType().GetProperty("Warning")!;

        Assert((bool)isOnProp.GetValue(pickToggle)! && (bool)isOnProp.GetValue(queueToggle)!, "tray toggles turn on when monitoring and auto-queue start");
        Assert((bool)activeProp.GetValue(statusItem)! && statusItem.Text == "自動ピック・インキューを監視中", "tray status reflects active monitoring and auto-queue");

        running = false;
        var monitorTask = (Task?)typeof(MainWindow).GetField("monitorTask", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main);
        if (monitorTask != null) await monitorTask.WaitAsync(TimeSpan.FromSeconds(5));

        var pickMonitoring = (bool)typeof(MainWindow).GetField("pickMonitoring", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
        var autoQueueActive = (bool)typeof(MainWindow).GetField("autoQueueActive", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
        var statusLabel = (Label)typeof(MainWindow).GetField("status", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
        var detailLabel = (Label)typeof(MainWindow).GetField("detail", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;
        var queueDetailLabel = (Label)typeof(MainWindow).GetField("queueDetail", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!;

        Assert(!pickMonitoring && !autoQueueActive, "monitoring and auto-queue turn off automatically when VALORANT closes");
        Assert(statusLabel.Text == "停止中" && detailLabel.Text == "VALORANTが終了したため監視を停止しました" && queueDetailLabel.Text == "自動インキューは停止中", "main window status reflects automatic stop on VALORANT exit");
        Assert(!(bool)isOnProp.GetValue(pickToggle)! && !(bool)isOnProp.GetValue(queueToggle)!, "tray toggles turn off automatically when VALORANT closes");
        Assert(!(bool)activeProp.GetValue(statusItem)! && (bool)warningProp.GetValue(statusItem)! && statusItem.Text == "VALORANTが終了したため停止しました", "tray status reflects automatic stop on VALORANT exit");
        ((NotifyIcon)typeof(MainWindow).GetField("tray", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(main)!).Dispose();
    }

    private static Color Pixel(Control control, Point point)
    {
        using var image = new Bitmap(control.Width, control.Height);
        control.DrawToBitmap(image, control.ClientRectangle);
        return image.GetPixel(point.X, point.Y);
    }
    [DllImport("user32.dll")]
    private static extern IntPtr GetWindow(IntPtr window, uint command);
    private static bool IsAbove(Form front, Form back)
    {
        for (var window = GetWindow(front.Handle, 2); window != IntPtr.Zero; window = GetWindow(window, 2))
            if (window == back.Handle) return true;
        return false;
    }
    private static void CheckRrChartScale()
    {
        var type = typeof(MatchHistoryControl).Assembly.GetType("ValoAutoPick.MatchTrendChart")!;
        using var chart = (Control)Activator.CreateInstance(type, true)!;
        chart.Size = new Size(640, 260);
        using var font = new Font("Yu Gothic UI", 16);
        chart.Font = font;
        type.GetProperty("Metric", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(chart, "RR推移");
        var date = DateTimeOffset.UtcNow;
        TrendPoint[] points = [new("a", "map", date, 0, MatchOutcome.Loss), new("b", "map", date.AddDays(1), 100, MatchOutcome.Win)];
        type.GetMethod("SetPoints")!.Invoke(chart, [points]);
        using var bitmap = new Bitmap(chart.Width, chart.Height);
        chart.DrawToBitmap(bitmap, chart.ClientRectangle);
        var positions = (PointF[])type.GetField("positions", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(chart)!;
        var labelWidth = TextRenderer.MeasureText("100", font, Size.Empty, TextFormatFlags.NoPadding | TextFormatFlags.SingleLine).Width;
        Assert(positions.Length == 2 && positions[0].X > labelWidth, "RR axis reserves room for three-digit labels");
        Assert(positions[0].Y > positions[1].Y, "RR axis places 100 above zero");
    }

    private static int Distance(Color a, Color b) => Math.Abs(a.R - b.R) + Math.Abs(a.G - b.G) + Math.Abs(a.B - b.B);
    private static void Assert(bool condition, string text)
    {
        if (!condition) throw new InvalidOperationException(text);
        Console.WriteLine("PASS " + text);
    }

    private static async Task CheckMotion(string name, Control control, Func<Control, Point> sample)
    {
        var point = sample(control);
        var idle = Pixel(control, point);
        var clock = Stopwatch.StartNew();
        MouseEnter.Invoke(control, [EventArgs.Empty]);
        await Task.Delay(55);
        var intermediate = Pixel(control, point);
        var observedAt = clock.ElapsedMilliseconds;
        await Task.Delay(200);
        var hovered = Pixel(control, point);
        Assert(Distance(idle, hovered) > 5, name + " changes on hover");
        if (observedAt < 130)
            Assert(Distance(idle, intermediate) > 0 && Distance(intermediate, hovered) > 0, name + " transitions through intermediate frames");
        MouseDown.Invoke(control, [new MouseEventArgs(MouseButtons.Left, 1, point.X, point.Y, 0)]);
        await Task.Delay(150);
        var pressed = Pixel(control, point);
        Assert(Distance(hovered, pressed) > 5, name + $" changes while pressed (hover={hovered}, pressed={pressed})");
        MouseUp.Invoke(control, [new MouseEventArgs(MouseButtons.Left, 1, point.X, point.Y, 0)]);
        await Task.Delay(200);
        Assert(Distance(hovered, Pixel(control, point)) <= 3, name + " returns to hover after release");
        if (name == "primary button")
        {
            MouseDown.Invoke(control, [new MouseEventArgs(MouseButtons.Left, 1, point.X, point.Y, 0)]);
            MouseUp.Invoke(control, [new MouseEventArgs(MouseButtons.Left, 1, point.X, point.Y, 0)]);
            await Task.Delay(25);
            Assert(Distance(hovered, Pixel(control, point)) > 2, "a quick click remains visible after release");
            await Task.Delay(200);
        }
        MouseLeave.Invoke(control, [EventArgs.Empty]);
        await Task.Delay(240);
        Assert(Distance(idle, Pixel(control, point)) <= 3, name + " returns to idle after leaving");
    }
}
