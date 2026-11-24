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
                await CheckClosingWaitsForPendingWork();
                await CheckAtomicUiUpdates();
                CheckStoreNavigationSpacing();
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
        typeof(StorePreviewControl).GetMethod("ShowCatalogPage", flags)!.Invoke(store, null);
        var sawStacked = false;
        foreach (var width in new[] { 1400, 800, 500, 1400 })
        {
            window.ClientSize = new Size(width, 800);
            window.PerformLayout();
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
