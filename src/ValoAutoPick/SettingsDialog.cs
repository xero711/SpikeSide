using Microsoft.Win32;
using System.Reflection;

namespace ValoAutoPick;

internal static class StartupRegistration
{
    private const string RunKey = @"Software\Microsoft\Windows\CurrentVersion\Run";
    private static string Name => Edition.IsTrial ? "SpikeSide-Trial" : "SpikeSide";

    internal static string Command
    {
        get
        {
            var executable = Environment.ProcessPath ?? throw new InvalidOperationException("実行ファイルの場所を取得できません。");
            var assembly = Assembly.GetEntryAssembly()?.Location;
            // Framework-dependent dotnet runs need both the host and the application DLL.
            return Path.GetFileNameWithoutExtension(executable).Equals("dotnet", StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(assembly) ? $"\"{executable}\" \"{assembly}\"" : $"\"{executable}\"";
        }
    }

    internal static bool IsEnabled()
    {
        using var key = Registry.CurrentUser.OpenSubKey(RunKey);
        return key != null && IsEnabled(key, Name);
    }

    internal static bool IsEnabled(RegistryKey key, string name) => key.GetValue(name) is string value && !string.IsNullOrWhiteSpace(value);

    internal static void SetEnabled(bool enabled)
    {
        using var key = Registry.CurrentUser.CreateSubKey(RunKey)
            ?? throw new InvalidOperationException("スタートアップの設定を開けません。");
        SetEnabled(key, Name, enabled, Command);
    }

    internal static void SetEnabled(RegistryKey key, string name, bool enabled, string command)
    {
        if (enabled) key.SetValue(name, command, RegistryValueKind.String);
        else key.DeleteValue(name, false);
    }
}

internal sealed class SettingsDialog : DarkForm
{
    private readonly SwitchToggle startup = new($"PC サインイン時に {Edition.DisplayName} を起動", false) { Dock = DockStyle.Fill };
    private readonly ThemedSelect theme = new(["ダーク", "ライト"]) { Dock = DockStyle.Fill, AccessibleName = "テーマ" };
    private readonly Func<string, bool> setTheme;
    private bool updating;

    internal SettingsDialog(string themeName, Func<string, bool> setTheme)
    {
        this.setTheme = setTheme;
        Text = "設定";
        MinimizeBox = false;
        StartPosition = FormStartPosition.CenterParent;
        ClientSize = new Size(700, 480);
        BackColor = Theme.Background;
        ForeColor = Theme.Text;

        var content = new TableLayoutPanel { Dock = DockStyle.Fill, BackColor = Theme.Background, Padding = new Padding(28, 20, 28, 20), ColumnCount = 1, RowCount = 8 };
        content.ColumnStyles.Add(new ColumnStyle(SizeType.Percent, 100));
        foreach (var height in new[] { 55, 36, 54, 40, 36, 54, 55 }) content.RowStyles.Add(new RowStyle(SizeType.Absolute, height));
        content.RowStyles.Add(new RowStyle(SizeType.Percent, 100));
        Label Label(string text, bool title = false) => new() { Text = text, Dock = DockStyle.Fill, ForeColor = title ? Theme.Text : Theme.Muted, Font = SharedFonts.Get("Yu Gothic UI", title ? 16 : 10.5f, title ? FontStyle.Bold : FontStyle.Regular), TextAlign = ContentAlignment.MiddleLeft };
        content.Controls.Add(Label("設定", true), 0, 0);
        content.Controls.Add(Label("起動"), 0, 1);
        content.Controls.Add(startup, 0, 2);
        content.Controls.Add(Label("Windows へのサインイン後にアプリを起動します。"), 0, 3);
        content.Controls.Add(Label("テーマ"), 0, 4);
        content.Controls.Add(theme, 0, 5);
        content.Controls.Add(Label("変更はすぐに保存・反映されます。"), 0, 6);
        var close = new ThemedButton { Text = "閉じる", Size = new Size(120, 42), Anchor = AnchorStyles.Right | AnchorStyles.Bottom, DialogResult = DialogResult.OK };
        content.Controls.Add(close, 0, 7);
        Controls.Add(content);
        AcceptButton = close;
        CancelButton = close;

        try { startup.Checked = StartupRegistration.IsEnabled(); }
        catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException)
        {
            startup.Enabled = false;
            MessageBox.Show(this, "スタートアップの状態を読み取れませんでした。", "設定", MessageBoxButtons.OK, MessageBoxIcon.Warning);
        }
        theme.SelectedIndex = themeName == "light" ? 1 : 0;
        startup.CheckedChanged += (_, _) =>
        {
            if (updating) return;
            try { StartupRegistration.SetEnabled(startup.Checked); }
            catch (Exception e) when (e is UnauthorizedAccessException or System.Security.SecurityException or IOException or InvalidOperationException)
            {
                updating = true; startup.Checked = !startup.Checked; updating = false;
                MessageBox.Show(this, "スタートアップの設定を変更できませんでした。", "設定", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };
        theme.SelectedIndexChanged += (_, _) =>
        {
            if (updating) return;
            var previous = Theme.IsLight ? 1 : 0;
            if (!this.setTheme(theme.SelectedIndex == 1 ? "light" : "dark"))
            {
                updating = true; theme.SelectedIndex = previous; updating = false;
                MessageBox.Show(this, "テーマの設定を保存できませんでした。", "設定", MessageBoxButtons.OK, MessageBoxIcon.Warning);
            }
        };
    }
}
