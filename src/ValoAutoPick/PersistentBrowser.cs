using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;

namespace ValoAutoPick;

// A dedicated profile keeps the Riot session separate from the user's normal browser.
internal sealed class PersistentBrowser : IAsyncDisposable
{
    private Process? process;
    private string? debuggingPortFile;
    private bool headless;
    internal static string Root => Path.Combine(Storage.Folder, "AccountBrowser");

    internal Task Open(Uri url, bool silent)
    {
        if (url.Scheme != Uri.UriSchemeHttps || !url.IsDefaultPort ||
            !url.Host.Equals("auth.riotgames.com", StringComparison.OrdinalIgnoreCase))
            throw new BrowserIsolationException("ログイン先を確認できませんでした");
        var (executable, kind) = IsolatedBrowser.DefaultBrowser();
        if (IsolatedBrowser.HasUserDataDirectoryPolicy(kind, executable))
            throw new BrowserIsolationException("既定ブラウザーの設定によりログイン情報を分離できません");
        var profile = Path.Combine(Root, Path.GetFileNameWithoutExtension(executable).ToLowerInvariant());
        if (IsolatedBrowser.HasReparseAncestor(profile)) throw new BrowserIsolationException("ログイン用プロファイルを確認できません");
        Directory.CreateDirectory(profile);
        if (IsolatedBrowser.HasReparseAncestor(profile)) throw new BrowserIsolationException("ログイン用プロファイルを確認できません");
        if (kind == IsolatedBrowser.BrowserKind.Firefox)
            File.WriteAllText(Path.Combine(profile, "user.js"), "user_pref(\"browser.startup.page\", 3);\nuser_pref(\"browser.sessionstore.privacy_level\", 0);\n");
        headless = silent && kind != IsolatedBrowser.BrowserKind.Firefox;
        if (headless)
        {
            debuggingPortFile = Path.Combine(profile, "DevToolsActivePort");
            File.Delete(debuggingPortFile);
        }
        process = Process.Start(CreateStartInfo(executable, profile, url, silent))
            ?? throw new BrowserIsolationException("既定ブラウザーを起動できませんでした");
        return Task.CompletedTask;
    }

    internal static ProcessStartInfo CreateStartInfo(string executable, string profile, Uri url, bool silent)
    {
        var kind = IsolatedBrowser.KindOf(executable);
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = silent && kind != IsolatedBrowser.BrowserKind.Firefox };
        if (kind == IsolatedBrowser.BrowserKind.Firefox)
        {
            start.ArgumentList.Add("-no-remote");
            start.ArgumentList.Add("-profile");
            start.ArgumentList.Add(profile);
            if (silent) start.WindowStyle = ProcessWindowStyle.Minimized;
        }
        else
        {
            start.ArgumentList.Add("--user-data-dir=" + profile);
            start.ArgumentList.Add("--no-first-run");
            start.ArgumentList.Add("--no-default-browser-check");
            start.ArgumentList.Add("--disable-background-mode");
            start.ArgumentList.Add("--restore-last-session");
            if (silent)
            {
                start.ArgumentList.Add("--remote-debugging-port=0");
                start.ArgumentList.Add("--headless=new");
            }
        }
        start.ArgumentList.Add(url.AbsoluteUri);
        return start;
    }

    public async ValueTask DisposeAsync()
    {
        if (process == null) return;
        try
        {
            if (!process.HasExited)
            {
                if (debuggingPortFile != null && await CloseHeadlessBrowser(debuggingPortFile))
                {
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(4));
                    try { await process.WaitForExitAsync(timeout.Token); } catch (OperationCanceledException) { }
                }
                else if (!headless)
                {
                    // Restoring a previous session can create more than one
                    // window. Close each window owned by this dedicated process.
                    var deadline = DateTime.UtcNow.AddSeconds(6);
                    while (!process.HasExited && DateTime.UtcNow < deadline)
                    {
                        process.Refresh();
                        process.CloseMainWindow();
                        await Task.Delay(300);
                    }
                }
                // A visible login window must not be force-killed: doing so can
                // discard the browser's session cookies. If Windows cannot close
                // it gracefully, let the user close that dedicated window.
                if (headless && !process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(); }
            }
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException) { }
        finally { process.Dispose(); process = null; }
    }

    private static async Task<bool> CloseHeadlessBrowser(string portFile)
    {
        try
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
            while (!File.Exists(portFile)) await Task.Delay(100, timeout.Token);
            var lines = await File.ReadAllLinesAsync(portFile, timeout.Token);
            if (lines.Length < 2 || !int.TryParse(lines[0], out var port) || port is < 1 or > 65535 ||
                !lines[1].StartsWith("/devtools/browser/", StringComparison.Ordinal) ||
                !Guid.TryParse(lines[1]["/devtools/browser/".Length..], out _)) return false;
            using var socket = new ClientWebSocket();
            await socket.ConnectAsync(new Uri($"ws://127.0.0.1:{port}{lines[1]}"), timeout.Token);
            await socket.SendAsync(Encoding.UTF8.GetBytes("{\"id\":1,\"method\":\"Browser.close\"}"), WebSocketMessageType.Text, true, timeout.Token);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or WebSocketException or OperationCanceledException) { return false; }
    }

    internal static void Clear()
    {
        if (!Directory.Exists(Root)) return;
        if (IsolatedBrowser.HasReparseAncestor(Root) || IsolatedBrowser.ContainsReparsePoint(Root))
            throw new IOException("ログイン用プロファイルを確認できません");
        Directory.Delete(Root, true);
    }
}
