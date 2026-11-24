namespace ValoAutoPick;

internal static class Program
{
    [STAThread]
    private static void Main(string[] args)
    {
        // Read-only visual previews can run beside the user's active instance.
        var visualPreview = args.Length >= 2 && args[0] is "--smoke-test" or "--preview-agent" or "--agent-picker-preview" or "--map-settings-preview";
        // The mutex keeps its pre-rename name so SpikeSide and older QuickPickerPro builds still exclude each other.
        var mutexName = visualPreview ? $"Local\\ValoAutoPick-Preview-{Environment.ProcessId}" : "Local\\QuickPickerPro-Desktop-v1";
        ApplicationConfiguration.Initialize();
        IsolatedBrowser.CleanupOldProfiles(Path.Combine(Path.GetTempPath(), "ValoAutoPick-PrivateLogin"));
        var product = Edition.DisplayName;
        using var mutex = new Mutex(true, mutexName, out var first);
        if (!first) { MessageBox.Show($"{product}（または旧QuickPickerPro）はすでに起動しています。通知領域のアイコンから開けます。", product); return; }
        Theme.Set(visualPreview && args.Contains("--preview-light") ? "light" : Storage.Load().Theme);
        Application.SetUnhandledExceptionMode(UnhandledExceptionMode.CatchException);
        Application.ThreadException += (_, e) => { NativePageStyle.LogPageError("UI", e.Exception); MessageBox.Show("処理を続けられませんでした。アプリを再起動してください。", product); };
        var previewAgent = args.Length == 3 && args[0] == "--preview-agent" ? args[2] : null;
        var pickerSmoke = args.Contains("--agent-picker-preview");
        var mapSmoke = args.Contains("--map-settings-preview");
        var smokePath = args.Length >= 2 && args[0] is "--smoke-test" or "--login-smoke-test" or "--preview-agent" or "--agent-picker-preview" or "--map-settings-preview" ? args[1] : null;
        Application.Run(new MainWindow(smokePath, args.Contains("--login-smoke-test"), previewAgent, pickerSmoke, mapSmoke));
    }
}
