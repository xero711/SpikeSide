using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Reflection;
using ValoAutoPick;

if (args.Length is < 1 or > 2 || !File.Exists(args[0])) throw new ArgumentException("Pass the browser executable path");
var debugClose = args.Contains("--debug-close");
var debugSecond = args.Contains("--debug-second");
var sessionCookie = args.Contains("--session-cookie");
var socket = new TcpListener(IPAddress.Loopback, 0);
socket.Start(); var port = ((IPEndPoint)socket.LocalEndpoint).Port; socket.Stop();
using var listener = new HttpListener();
listener.Prefixes.Add($"http://127.0.0.1:{port}/"); listener.Start();
var setReceived = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var cookieReceived = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(60));
var server = Task.Run(async () =>
{
    while (!stop.IsCancellationRequested)
    {
        HttpListenerContext context;
        try { context = await listener.GetContextAsync().WaitAsync(stop.Token); }
        catch (Exception e) when (e is OperationCanceledException or HttpListenerException or ObjectDisposedException) { return; }
        if (context.Request.Url?.AbsolutePath == "/set")
        {
            context.Response.Headers.Add("Set-Cookie", "trial_probe=local-only; Path=/; HttpOnly; SameSite=Lax" + (sessionCookie ? "" : "; Max-Age=3600"));
            context.Response.StatusCode = 302;
            context.Response.RedirectLocation = "/verify";
            context.Response.Close();
        }
        else if (context.Request.Url?.AbsolutePath == "/verify")
        {
            var found = context.Request.Cookies["trial_probe"]?.Value == "local-only";
            context.Response.Close();
            if (!found) setReceived.TrySetException(new Exception("Browser did not accept initial cookie"));
            else setReceived.TrySetResult();
        }
        else if (context.Request.Url?.AbsolutePath == "/check")
        {
            var found = context.Request.Cookies["trial_probe"]?.Value == "local-only";
            context.Response.Close(); cookieReceived.TrySetResult(found);
        }
        else context.Response.Close();
    }
});
var profile = Path.Combine(Path.GetTempPath(), "QuickPicker-BrowserProbe-" + Guid.NewGuid().ToString("N"));
Directory.CreateDirectory(profile);
if (Environment.GetEnvironmentVariable("QUICKPICKER_PROBE_KEEP") == "1") Console.WriteLine("Probe profile: " + profile);
try
{
    async Task CloseUsingApp(Process process, bool debug)
    {
        var id = process.Id;
        var browser = new PersistentBrowser();
        typeof(PersistentBrowser).GetField("process", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(browser, process);
        if (debug) typeof(PersistentBrowser).GetField("debuggingPortFile", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(browser, Path.Combine(profile, "DevToolsActivePort"));
        await browser.DisposeAsync();
        try
        {
            using var existing = Process.GetProcessById(id);
            await existing.WaitForExitAsync(stop.Token);
        }
        catch (ArgumentException) { }
    }
    Process Launch(string path, bool debug)
    {
        var start = PersistentBrowser.CreateStartInfo(args[0], profile, new Uri($"http://127.0.0.1:{port}/{path}"), false);
        if (debug)
        {
            File.Delete(Path.Combine(profile, "DevToolsActivePort"));
            start.ArgumentList.Insert(start.ArgumentList.Count - 1, "--remote-debugging-port=0");
        }
        start.ArgumentList.Insert(start.ArgumentList.Count - 1, "--start-minimized");
        return Process.Start(start) ?? throw new Exception("Could not start browser");
    }
    var first = Launch("set", debugClose);
    await setReceived.Task.WaitAsync(stop.Token);
    await Task.Delay(500, stop.Token);
    await CloseUsingApp(first, debugClose);
    if (int.TryParse(Environment.GetEnvironmentVariable("QUICKPICKER_PROBE_DELAY_MS"), out var delay) && delay > 0)
        await Task.Delay(delay, stop.Token);
    var second = Launch("check", debugClose || debugSecond);
    var restored = await cookieReceived.Task.WaitAsync(stop.Token);
    await CloseUsingApp(second, debugClose || debugSecond);
    if (!restored) throw new Exception("Session cookie was not restored");
    Console.WriteLine("PASS interactive browser closes normally and restores a " + (sessionCookie ? "session" : "persistent") + " cookie after restart");
}
finally
{
    stop.Cancel(); listener.Stop(); await server;
    if (Environment.GetEnvironmentVariable("QUICKPICKER_PROBE_KEEP") != "1" && Directory.Exists(profile)) Directory.Delete(profile, true);
}

namespace ValoAutoPick
{
    internal static class Storage { public static string Folder => Path.GetTempPath(); }
}
