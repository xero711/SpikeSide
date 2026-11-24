using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using System.Security.Cryptography;
using System.Runtime.InteropServices;
using Microsoft.Win32.SafeHandles;

namespace ValoAutoPick;

// A dedicated profile keeps the Riot session separate from the user's normal browser.
internal sealed class PersistentBrowser : IAsyncDisposable
{
    private Process? process;
    private string? debuggingPortFile;
    private bool headless;
    private Semaphore? profileLock;
    private BrowserProcessJob? browserJob;
    internal static string Root => Path.Combine(Storage.Folder, "AccountBrowser");

    internal async Task Open(Uri url, bool silent, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (url.Scheme != Uri.UriSchemeHttps || !url.IsDefaultPort ||
            !url.Host.Equals("auth.riotgames.com", StringComparison.OrdinalIgnoreCase))
            throw new BrowserIsolationException("ログイン先を確認できませんでした");
        var (executable, kind) = IsolatedBrowser.DefaultBrowser();
        if (IsolatedBrowser.HasUserDataDirectoryPolicy(kind, executable))
            throw new BrowserIsolationException("既定ブラウザーの設定によりログイン情報を分離できません");
        var profile = Path.Combine(Root, Path.GetFileNameWithoutExtension(executable).ToLowerInvariant());
        var lockName = "Local\\SpikeSide-AccountBrowser-" + Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(profile.ToUpperInvariant())));
        profileLock = new Semaphore(1, 1, lockName);
        if (!profileLock.WaitOne(0))
        {
            profileLock.Dispose(); profileLock = null;
            throw new BrowserIsolationException("ログイン用ブラウザーは別のSpikeSideで使用中です");
        }
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
        if (headless) browserJob = new BrowserProcessJob();
        process = Process.Start(CreateStartInfo(executable, profile, url, silent))
            ?? throw new BrowserIsolationException("既定ブラウザーを起動できませんでした");
        if (headless)
        {
            try { browserJob!.Add(process); }
            catch
            {
                try { if (!process.HasExited) process.Kill(entireProcessTree: true); }
                catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or AggregateException) { }
                throw;
            }
        }
        // Chromium forwards launches to an existing process with the same profile.
        // If that process is headless the new window is invisible, so fail visibly.
        await Task.Delay(800, cancellationToken);
        if (process.HasExited)
            throw new BrowserIsolationException("ログイン用ブラウザーを開けませんでした。開いているログイン用ブラウザーを閉じてから再度お試しください");
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
        try
        {
            if (process != null && !process.HasExited)
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
                if (headless && !process.HasExited)
                {
                    process.Kill(entireProcessTree: true);
                    using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(3));
                    try { await process.WaitForExitAsync(timeout.Token); } catch (OperationCanceledException) { }
                }
            }
        }
        catch (Exception e) when (e is InvalidOperationException or System.ComponentModel.Win32Exception or NotSupportedException or AggregateException) { }
        finally
        {
            browserJob?.Dispose(); browserJob = null;
            process?.Dispose(); process = null;
            if (profileLock != null) { profileLock.Release(); profileLock.Dispose(); profileLock = null; }
        }
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
            // Restore the profile's session cookies, but replace all restored OAuth
            // tabs with one harmless tab before saving the next browser session.
            async Task<JsonDocument> Command(int id, string method, object? parameters = null)
            {
                var request = JsonSerializer.SerializeToUtf8Bytes(new { id, method, @params = parameters });
                await socket.SendAsync(request, WebSocketMessageType.Text, true, timeout.Token);
                var buffer = new byte[8192];
                while (true)
                {
                    using var message = new MemoryStream();
                    WebSocketReceiveResult part;
                    do
                    {
                        part = await socket.ReceiveAsync(new ArraySegment<byte>(buffer), timeout.Token);
                        if (part.MessageType == WebSocketMessageType.Close || message.Length + part.Count > 1024 * 1024)
                            throw new WebSocketException("Invalid browser debugging response");
                        message.Write(buffer, 0, part.Count);
                    } while (!part.EndOfMessage);
                    var response = JsonDocument.Parse(message.ToArray());
                    if (response.RootElement.TryGetProperty("id", out var responseId) && responseId.GetInt32() == id) return response;
                    response.Dispose();
                }
            }
            using var targets = await Command(1, "Target.getTargets");
            using var blank = await Command(2, "Target.createTarget", new { url = "about:blank" });
            if (blank.RootElement.TryGetProperty("result", out var created) && created.TryGetProperty("targetId", out var blankId) &&
                targets.RootElement.TryGetProperty("result", out var targetResult) && targetResult.TryGetProperty("targetInfos", out var infos))
            {
                var id = 3;
                foreach (var target in infos.EnumerateArray())
                {
                    if (!target.TryGetProperty("type", out var type) || type.GetString() != "page" ||
                        !target.TryGetProperty("targetId", out var targetId) || targetId.GetString() == blankId.GetString()) continue;
                    using var closed = await Command(id++, "Target.closeTarget", new { targetId = targetId.GetString() });
                }
            }
            await socket.SendAsync(Encoding.UTF8.GetBytes("{\"id\":999,\"method\":\"Browser.close\"}"), WebSocketMessageType.Text, true, timeout.Token);
            return true;
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or WebSocketException or OperationCanceledException or JsonException or InvalidOperationException) { return false; }
    }

    internal static void Clear()
    {
        if (!Directory.Exists(Root)) return;
        if (IsolatedBrowser.HasReparseAncestor(Root) || IsolatedBrowser.ContainsReparsePoint(Root))
            throw new IOException("ログイン用プロファイルを確認できません");
        Directory.Delete(Root, true);
    }
}

// Windows closes this job even if SpikeSide crashes, preventing an invisible
// browser from retaining the shared profile after its owner disappears.
internal sealed class BrowserProcessJob : IDisposable
{
    private readonly SafeFileHandle handle;

    internal BrowserProcessJob()
    {
        handle = CreateJobObject(IntPtr.Zero, null);
        if (handle.IsInvalid) throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
        var info = new ExtendedLimitInformation();
        info.BasicLimitInformation.LimitFlags = 0x2000; // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
        if (!SetInformationJobObject(handle, 9, ref info, (uint)Marshal.SizeOf<ExtendedLimitInformation>()))
        {
            var error = Marshal.GetLastWin32Error();
            handle.Dispose();
            throw new System.ComponentModel.Win32Exception(error);
        }
    }

    internal void Add(Process process)
    {
        if (!AssignProcessToJobObject(handle, process.Handle))
            throw new System.ComponentModel.Win32Exception(Marshal.GetLastWin32Error());
    }

    public void Dispose() => handle.Dispose();

    [StructLayout(LayoutKind.Sequential)]
    private struct BasicLimitInformation
    {
        public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
        public uint LimitFlags;
        public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
        public uint ActiveProcessLimit;
        public UIntPtr Affinity;
        public uint PriorityClass, SchedulingClass;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct IoCounters
    {
        public ulong ReadOperationCount, WriteOperationCount, OtherOperationCount;
        public ulong ReadTransferCount, WriteTransferCount, OtherTransferCount;
    }

    [StructLayout(LayoutKind.Sequential)]
    private struct ExtendedLimitInformation
    {
        public BasicLimitInformation BasicLimitInformation;
        public IoCounters IoInfo;
        public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
    }

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern SafeFileHandle CreateJobObject(IntPtr attributes, string? name);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool SetInformationJobObject(SafeFileHandle job, int infoClass, ref ExtendedLimitInformation info, uint length);
    [DllImport("kernel32.dll", SetLastError = true)]
    private static extern bool AssignProcessToJobObject(SafeFileHandle job, IntPtr process);
}
