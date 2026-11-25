using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace ValoAutoPick;

public sealed class RiotApi : IGameApi, IQueueApi, ILiveTrackerSource, IDisposable
{
    private static readonly string ClientPlatformHeader = Convert.ToBase64String(
        Encoding.UTF8.GetBytes("{\"platformType\":\"PC\",\"platformOS\":\"Windows\",\"platformOSVersion\":\"10.0.19042.1.256.64bit\",\"platformChipset\":\"Unknown\"}"));
    private readonly HttpClient remote = new(new HttpClientHandler { AllowAutoRedirect = false }) { Timeout = TimeSpan.FromSeconds(8) };
    private readonly HttpClient local = new(new HttpClientHandler
    {
        UseProxy = false, AllowAutoRedirect = false,
        // Riot Client uses a self-signed certificate. This client is used ONLY for fixed loopback URLs.
        ServerCertificateCustomValidationCallback = (request, _, _, _) => request.RequestUri is { Host: "127.0.0.1", Scheme: "https" }
    }) { Timeout = TimeSpan.FromSeconds(4) };
    private sealed record RiotSession(
        string Access, string Entitlement, string Version, string Server, string PlayerDataServer, string Fingerprint,
        DateTimeOffset Refreshed, string Subject, string RegionLabel, string AccountName, DateTimeOffset ExpiresAt, bool HasAccountSession)
    {
        public static RiotSession Empty { get; } = new("", "", "", "", "", "", DateTimeOffset.MinValue,
            "", "", "", DateTimeOffset.MinValue, false);
    }

    private static readonly TimeSpan PregameRequestTimeout = TimeSpan.FromSeconds(3);
    private static readonly Regex GameFlowRegex = new(@"LogGameFlowStateManager:.*Broadcasting state changed to ([A-Za-z0-9_]+)",
        RegexOptions.NonBacktracking | RegexOptions.CultureInvariant);
    private RiotSession session = RiotSession.Empty;
    private readonly object sessionGate = new();
    private long sessionEpoch;
    private RiotSession CurrentSession => Volatile.Read(ref session);
    private DateTimeOffset readinessConnectionCheck;
    private string readinessSubject = "";
    private bool readinessLocalAccount;
    private string localEventConnectedSubject = "";
    private readonly CharacterSelectLogTracker logTracker = new();
    public string Subject => CurrentSession.Subject;
    public string RegionLabel => CurrentSession.RegionLabel;
    public string AccountName => CurrentSession.AccountName;
    public DateTimeOffset ExpiresAt => CurrentSession.ExpiresAt;
    public bool HasAccountSession => CurrentSession.HasAccountSession;
    public void Invalidate()
    {
        while (true)
        {
            var current = CurrentSession;
            var invalidated = current with { Refreshed = DateTimeOffset.MinValue };
            if (ReferenceEquals(Interlocked.CompareExchange(ref session, invalidated, current), current)) return;
        }
    }

    private void Invalidate(RiotSession expected) =>
        Interlocked.CompareExchange(ref session, expected with { Refreshed = DateTimeOffset.MinValue }, expected);

    public static bool IsValorantRunning()
    {
        var processes = Process.GetProcessesByName("VALORANT-Win64-Shipping");
        try { return processes.Length > 0; }
        finally { foreach (var process in processes) process.Dispose(); }
    }

    public RiotApi() { }
    internal RiotApi(HttpMessageHandler testTransport)
    {
        remote.Dispose(); remote = new HttpClient(testTransport) { Timeout = TimeSpan.FromSeconds(8) };
    }

    public void Logout()
    {
        lock (sessionGate)
        {
            Interlocked.Increment(ref sessionEpoch);
            Interlocked.Exchange(ref session, RiotSession.Empty);
        }
        readinessSubject = "";
        readinessLocalAccount = false;
        readinessConnectionCheck = DateTimeOffset.MinValue;
        Volatile.Write(ref localEventConnectedSubject, "");
        logTracker.Reset();
    }

    internal void SetLocalEventConnected(string subject, bool connected)
    {
        if (connected && Guid.TryParse(subject, out _))
        {
            Volatile.Write(ref localEventConnectedSubject, subject);
            readinessSubject = subject;
            readinessLocalAccount = true;
            readinessConnectionCheck = DateTimeOffset.UtcNow.AddSeconds(3);
            return;
        }
        var current = Volatile.Read(ref localEventConnectedSubject);
        if (string.IsNullOrEmpty(subject) || string.Equals(current, subject, StringComparison.OrdinalIgnoreCase))
        {
            Volatile.Write(ref localEventConnectedSubject, "");
            readinessConnectionCheck = DateTimeOffset.MinValue;
        }
    }

    public async Task Login(AuthResult auth, string regionSetting, CancellationToken ct, string? expectedSubject = null)
    {
        long epoch;
        lock (sessionGate) epoch = Interlocked.Increment(ref sessionEpoch);
        // Tokens are deliberately held in memory only. All service URLs are fixed and HTTPS.
        using var info = await AuthRequest(HttpMethod.Get, "https://auth.riotgames.com/userinfo", auth.AccessToken, null, ct);
        var subject = RequiredString(info.RootElement, "sub");
        if (!Guid.TryParse(subject, out _)) throw new InvalidDataException("ログイン情報を確認できませんでした");
        if (expectedSubject != null && !subject.Equals(expectedSubject, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("ログイン中のアカウントが一致しません");
        using var ent = await AuthRequest(HttpMethod.Post, "https://entitlements.auth.riotgames.com/api/token/v1", auth.AccessToken, "{}", ct);
        using var geo = await AuthRequest(HttpMethod.Put, "https://riot-geo.pas.si.riotgames.com/pas/v1/product/valorant", auth.AccessToken,
            JsonSerializer.Serialize(new { id_token = auth.IdToken }), ct);
        var region = regionSetting == "auto"
            ? RequiredString(RequiredProperty(geo.RootElement, "affinities"), "live")
            : regionSetting;
        var shard = region is "latam" or "br" ? "na" : region;
        if (region == "pbe") region = "na";
        if (!new[] { "na", "eu", "ap", "kr", "latam", "br" }.Contains(region))
            throw new ClientUnavailableException("地域を検出できません · 設定で地域を指定してください");
        using var verResponse = await remote.GetAsync("https://valorant-api.com/v1/version", ct);
        verResponse.EnsureSuccessStatusCode();
        using var ver = JsonDocument.Parse(await verResponse.Content.ReadAsStringAsync(ct));
        var newVersion = RequiredString(RequiredProperty(ver.RootElement, "data"), "riotClientVersion");
        var newEnt = RequiredString(ent.RootElement, "entitlements_token");
        var name = "ログイン済み";
        if (info.RootElement.TryGetProperty("acct", out var acct))
        {
            var gameName = Text(acct, "game_name"); var tag = Text(acct, "tag_line");
            if (gameName.Length > 0) name = gameName + "#" + tag;
        }
        lock (sessionGate)
        {
            if (Interlocked.Read(ref sessionEpoch) != epoch) return;
            Interlocked.Exchange(ref session, new RiotSession(
                auth.AccessToken, newEnt, newVersion, $"https://glz-{region}-1.{shard}.a.pvp.net",
                $"https://pd.{shard}.a.pvp.net", "", DateTimeOffset.MinValue,
                subject, RegionDisplay.Name(region, shard), name, auth.ExpiresAt, true));
        }
    }

    private async Task<JsonDocument> AuthRequest(HttpMethod method, string url, string token, string? body, CancellationToken ct)
    {
        using var req = new HttpRequestMessage(method, url);
        req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
        if (body != null) req.Content = new StringContent(body, Encoding.UTF8, "application/json");
        using var res = await remote.SendAsync(req, ct);
        if (!res.IsSuccessStatusCode) throw new ApiException((int)res.StatusCode);
        return JsonDocument.Parse(await res.Content.ReadAsStringAsync(ct));
    }

    public async Task EnsureConnected(string regionSetting, CancellationToken ct, string? expectedSubject = null)
    {
        var (raw, port, password) = ReadRiotClientConnection();
        var key = raw + "|" + regionSetting;
        var epoch = Interlocked.Read(ref sessionEpoch);
        var current = CurrentSession;
        if (current.Fingerprint == key && DateTimeOffset.UtcNow - current.Refreshed < TimeSpan.FromSeconds(30) &&
            (expectedSubject == null || string.Equals(expectedSubject, current.Subject, StringComparison.OrdinalIgnoreCase))) return;
        using var token = await LocalGet(port, password, "/entitlements/v1/token", ct);
        var subject = RequiredString(token.RootElement, "subject");
        var newAccess = RequiredString(token.RootElement, "accessToken");
        var newEntitlement = RequiredString(token.RootElement, "token");
        if (expectedSubject != null && (TokenExpiry(newAccess) is not { } expires || expires <= DateTimeOffset.UtcNow.AddMinutes(3)))
            throw new ClientUnavailableException("Riot Clientのトークンの更新を待っています");
        if (!Guid.TryParse(subject, out _)) throw new InvalidDataException("Riot Clientのアカウント情報が不正です");
        if (expectedSubject != null && !string.Equals(subject, expectedSubject, StringComparison.OrdinalIgnoreCase))
            throw new ClientUnavailableException("Riot Clientのアカウントが一致しません");
        using var sessions = await LocalGet(port, password, "/product-session/v1/external-sessions", ct);
        var game = sessions.RootElement.EnumerateObject().Select(x => x.Value)
            .FirstOrDefault(x => Text(x, "productId") == "valorant" && Text(x, "phase") != "Stopped");
        if (game.ValueKind != JsonValueKind.Object)
            throw new ClientUnavailableException("VALORANTの起動を待っています");
        var log = ReadValorantLog();
        var (region, shard) = ResolveLocalRegion(game, regionSetting, log);
        var clientVersion = ResolveLocalVersion(game, log);
        var sameAccount = current.Subject.Equals(subject, StringComparison.OrdinalIgnoreCase);
        var refreshed = new RiotSession(
            newAccess, newEntitlement, clientVersion, $"https://glz-{region}-1.{shard}.a.pvp.net",
            $"https://pd.{shard}.a.pvp.net", key, DateTimeOffset.UtcNow, subject,
            RegionDisplay.Name(region, shard), sameAccount ? current.AccountName : "",
            sameAccount ? current.ExpiresAt : DateTimeOffset.MinValue,
            sameAccount && current.HasAccountSession);
        lock (sessionGate)
        {
            if (Interlocked.Read(ref sessionEpoch) != epoch || !ReferenceEquals(CurrentSession, current)) return;
            Interlocked.Exchange(ref session, refreshed);
        }
    }

    private static (string Raw, int Port, string Password) ReadRiotClientConnection()
    {
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Riot Games", "Riot Client", "Config", "lockfile");
        string raw;
        try { raw = ReadShared(path, 4096).Trim(); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        { throw new ClientUnavailableException("Riot Clientの起動を待っています"); }
        var parts = raw.Split(':');
        if (parts.Length != 5 || !int.TryParse(parts[1], out var pid) || !int.TryParse(parts[2], out var port) || port is < 1 or > 65535 || parts[4] != "https")
            throw new ClientUnavailableException("Riot Clientの接続情報を待っています");
        try
        {
            using var process = Process.GetProcessById(pid);
            if (!process.ProcessName.StartsWith("RiotClient", StringComparison.OrdinalIgnoreCase))
                throw new ClientUnavailableException("Riot Clientの起動を待っています");
        }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception)
        { throw new ClientUnavailableException("Riot Clientの起動を待っています"); }
        return (raw, port, parts[3]);
    }

    private static string ReadValorantLog()
    {
        string log = "";
        var logPath = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VALORANT", "Saved", "Logs", "ShooterGame.log");
        try { log = ReadShared(logPath, 8 * 1024 * 1024); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException)
        {
            // The log is optional; deployment and the selected region can still resolve the server.
        }
        return log;
    }

    internal static (string Region, string Shard) ResolveLocalRegion(JsonElement game, string regionSetting, string log)
    {
        var deployment = "";
        if (game.TryGetProperty("launchConfiguration", out var launch) && launch.TryGetProperty("arguments", out var args))
            deployment = args.EnumerateArray().Select(a => a.GetString() ?? "").FirstOrDefault(a => a.StartsWith("-ares-deployment="))?.Split('=', 2)[1] ?? "";
        var logServers = Regex.Matches(log, @"https://glz-(na|eu|ap|kr|br|latam)-1\.(na|eu|ap|kr|pbe)\.a\.pvp\.net");
        var region = regionSetting == "auto" ? deployment : regionSetting;
        var shard = region is "br" or "latam" ? "na" : region;
        if (region == "pbe") region = "na";
        if (!new[] { "na", "eu", "ap", "kr", "br", "latam" }.Contains(region))
        {
            if (logServers.Count == 0) throw new ClientUnavailableException("地域を検出できません · 設定で地域を選んでください");
            region = logServers[^1].Groups[1].Value; shard = logServers[^1].Groups[2].Value;
        }
        if (regionSetting == "auto" && Text(game, "patchlineId") == "pbe") { region = "na"; shard = "pbe"; }
        return (region, shard);
    }

    internal static string ResolveLocalVersion(JsonElement game, string log)
    {
        var versions = Regex.Matches(log, @"CI server version:\s*([\w.\-]+)");
        var clientVersion = Text(game, "version");
        if (!clientVersion.StartsWith("release-")) clientVersion = versions.Count > 0 ? versions[^1].Groups[1].Value : "";
        if (clientVersion.Length == 0) throw new ClientUnavailableException("ゲームのバージョン情報を待っています");
        return clientVersion;
    }

    public async Task<bool> TryRefreshAccountFromLocal(string regionSetting, CancellationToken ct)
    {
        var before = CurrentSession;
        if (!before.HasAccountSession || !Guid.TryParse(before.Subject, out _)) return false;
        var expected = before.Subject;
        if (await GetLocalEventConnection(expected, ct) == null) return false;
        try { await EnsureConnected(regionSetting, ct, expected); }
        catch (Exception e) when (e is ClientUnavailableException or IOException or UnauthorizedAccessException or HttpRequestException or System.ComponentModel.Win32Exception or InvalidOperationException or JsonException) { return false; }
        var refreshedSession = CurrentSession;
        if (!refreshedSession.Subject.Equals(expected, StringComparison.OrdinalIgnoreCase) || !refreshedSession.HasAccountSession) return false;
        var expires = TokenExpiry(refreshedSession.Access);
        if (expires == null || expires <= DateTimeOffset.UtcNow.AddMinutes(3)) return false;
        var updated = refreshedSession with { ExpiresAt = expires.Value };
        if (!ReferenceEquals(Interlocked.CompareExchange(ref session, updated, refreshedSession), refreshedSession)) return false;
        return true;
    }

    internal static DateTimeOffset? TokenExpiry(string token)
    {
        try
        {
            var parts = token.Split('.');
            if (parts.Length != 3) return null;
            var payload = parts[1].Replace('-', '+').Replace('_', '/');
            payload = payload.PadRight((payload.Length + 3) / 4 * 4, '=');
            using var data = JsonDocument.Parse(Convert.FromBase64String(payload));
            if (!data.RootElement.TryGetProperty("exp", out var exp) || !exp.TryGetInt64(out var seconds)) return null;
            return DateTimeOffset.FromUnixTimeSeconds(seconds);
        }
        catch (Exception e) when (e is FormatException or JsonException or ArgumentOutOfRangeException or InvalidOperationException) { return null; }
    }

    internal static string ReadShared(string path, int maxBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        // Read the beginning (version) and end (latest deployment), bounded even for long sessions.
        if (stream.Length <= maxBytes) return new StreamReader(stream).ReadToEnd();
        var half = maxBytes / 2;
        var bytes = new byte[maxBytes];
        stream.ReadExactly(bytes.AsSpan(0, half));
        stream.Seek(-half, SeekOrigin.End);
        stream.ReadExactly(bytes.AsSpan(half, half));
        return Encoding.UTF8.GetString(bytes);
    }
    internal static string ReadTailShared(string path, int maxBytes)
    {
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        stream.Seek(Math.Max(0, stream.Length - maxBytes), SeekOrigin.Begin);
        using var reader = new StreamReader(stream);
        return reader.ReadToEnd();
    }
    private async Task<JsonDocument> LocalGet(int port, string password, string path, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(HttpMethod.Get, $"https://127.0.0.1:{port}{path}");
        request.Headers.Authorization = new AuthenticationHeaderValue("Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("riot:" + password)));
        using var response = await local.SendAsync(request, ct);
        if (!response.IsSuccessStatusCode) throw new ClientUnavailableException("Riot Clientへのログインを待っています");
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync(ct));
    }
    internal async Task<(int Port, string Password, string Subject)?> GetLocalEventConnection(string? expectedSubject, CancellationToken ct)
    {
        if (expectedSubject != null && !Guid.TryParse(expectedSubject, out _)) return null;
        try
        {
            var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "Riot Games", "Riot Client", "Config", "lockfile");
            var parts = ReadShared(path, 4096).Trim().Split(':');
            if (parts.Length != 5 || !int.TryParse(parts[1], out var pid) || !int.TryParse(parts[2], out var port) ||
                port is < 1 or > 65535 || parts[4] != "https") return null;
            using var process = Process.GetProcessById(pid);
            if (!process.ProcessName.StartsWith("RiotClient", StringComparison.OrdinalIgnoreCase)) return null;
            using var token = await LocalGet(port, parts[3], "/entitlements/v1/token", ct);
            var subject = Text(token.RootElement, "subject");
            if (!Guid.TryParse(subject, out _) || (expectedSubject != null && !string.Equals(subject, expectedSubject, StringComparison.OrdinalIgnoreCase))) return null;
            return (port, parts[3], subject);
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or InvalidOperationException or System.ComponentModel.Win32Exception or
            HttpRequestException or ClientUnavailableException or JsonException) { return null; }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return null; }
    }
    private async Task<JsonDocument?> Request(HttpMethod method, string path, bool missingIsIdle, CancellationToken ct,
        bool playerData = false, string? jsonBody = null, RiotSession? sessionSnapshot = null, TimeSpan? timeout = null, bool invalidateOnForbidden = true)
    {
        var current = sessionSnapshot ?? CurrentSession;
        var baseUrl = playerData ? current.PlayerDataServer : current.Server;
        if (baseUrl.Length == 0 || current.Access.Length == 0 || current.Entitlement.Length == 0)
            throw new InvalidOperationException("アカウントに接続してください");
        using var request = new HttpRequestMessage(method, baseUrl + path);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", current.Access);
        request.Headers.Add("X-Riot-Entitlements-JWT", current.Entitlement);
        request.Headers.Add("X-Riot-ClientVersion", current.Version);
        request.Headers.Add("X-Riot-ClientPlatform", ClientPlatformHeader);
        if (jsonBody is not null || method == HttpMethod.Post)
            request.Content = new StringContent(jsonBody ?? "{}", Encoding.UTF8, "application/json");
        using var requestTimeout = timeout is { } limit ? CancellationTokenSource.CreateLinkedTokenSource(ct) : null;
        if (requestTimeout != null && timeout is { } limitValue) requestTimeout.CancelAfter(limitValue);
        var effectiveToken = requestTimeout?.Token ?? ct;
        using var response = await remote.SendAsync(request, effectiveToken);
        if (response.StatusCode == HttpStatusCode.NotFound && missingIsIdle) return null;
        if (!response.IsSuccessStatusCode)
        {
            if (response.StatusCode == HttpStatusCode.Unauthorized || response.StatusCode == HttpStatusCode.Forbidden && invalidateOnForbidden) Invalidate(current);
            var retry = response.Headers.RetryAfter?.Delta?.TotalSeconds
                ?? (response.Headers.RetryAfter?.Date - DateTimeOffset.UtcNow)?.TotalSeconds ?? 5;
            throw new ApiException((int)response.StatusCode, (int)Math.Ceiling(retry));
        }
        var json = await response.Content.ReadAsStringAsync(effectiveToken);
        return JsonDocument.Parse(string.IsNullOrWhiteSpace(json) ? "{}" : json);
    }
    private const string AgentItemType = "01bb38e1-da47-4e6a-9b3d-945fe4655707";
    public async Task<HashSet<string>> GetOwnedAgents(CancellationToken ct, IEnumerable<Agent>? catalogue = null)
    {
        var current = CurrentSession;
        EnsurePlayerDataSession(current);
        using var result = await Request(HttpMethod.Get, $"/store/v1/entitlements/{current.Subject}/{AgentItemType}", false, ct, playerData: true, sessionSnapshot: current);
        return AgentAccess.WithBaseAgents(ParseOwnedAgents(result!.RootElement), catalogue);
    }
    public async Task<JsonDocument> GetStorefrontResponse(CancellationToken ct)
    {
        var current = CurrentSession;
        EnsurePlayerDataSession(current);
        return (await Request(HttpMethod.Post, $"/store/v3/storefront/{current.Subject}", false, ct, playerData: true, sessionSnapshot: current))!;
    }
    public async Task<WalletBalances> GetWallet(CancellationToken ct)
    {
        var current = CurrentSession;
        EnsurePlayerDataSession(current);
        using var result = await Request(HttpMethod.Get, $"/store/v1/wallet/{current.Subject}", false, ct, playerData: true, sessionSnapshot: current);
        return WalletBalances.Parse(result!.RootElement);
    }

    public async Task<IReadOnlyDictionary<string, CompetitiveUpdate>> GetCompetitiveUpdates(CancellationToken ct)
    {
        var current = CurrentSession;
        EnsurePlayerDataSession(current);
        using var result = await Request(HttpMethod.Get, $"/mmr/v1/players/{current.Subject}/competitiveupdates?startIndex=0&endIndex=20&queue=competitive", false, ct, playerData: true, sessionSnapshot: current);
        return CompetitiveUpdate.Parse(result!.RootElement);
    }
    public async Task<LiveRecentRank?> GetPlayerRecentRank(string subject, CancellationToken ct)
    {
        ValidateId(subject);
        var current = CurrentSession;
        EnsurePlayerDataSession(current);
        using var result = await Request(HttpMethod.Get, $"/mmr/v1/players/{subject}/competitiveupdates?startIndex=0&endIndex=1&queue=competitive",
            true, ct, playerData: true, sessionSnapshot: current, invalidateOnForbidden: false);
        return result is null ? null : LiveRecentRank.Parse(result.RootElement);
    }
    public async Task<RiotMatchHistoryPage> GetMatchHistory(int startIndex, int pageSize, CancellationToken ct, string? queue = null)
    {
        var current = CurrentSession;
        EnsurePlayerDataSession(current);
        if (startIndex < 0) throw new ArgumentOutOfRangeException(nameof(startIndex));
        if (pageSize is < 1 or > 20) throw new ArgumentOutOfRangeException(nameof(pageSize));
        var endIndex = checked(startIndex + pageSize);
        var queueFilter = string.IsNullOrWhiteSpace(queue) ? "" : "&queue=" + Uri.EscapeDataString(queue);
        using var result = await Request(HttpMethod.Get,
            $"/match-history/v1/history/{current.Subject}?startIndex={startIndex}&endIndex={endIndex}{queueFilter}",
            false, ct, playerData: true, sessionSnapshot: current);
        var root = result!.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("History", out var history) || history.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException("試合履歴の形式を読み取れませんでした");
        var entries = new List<RiotMatchHistoryEntry>();
        foreach (var entry in history.EnumerateArray())
        {
            var id = Text(entry, "MatchID");
            var started = entry.TryGetProperty("GameStartTime", out var start) && start.TryGetInt64(out var millis) ? millis : 0;
            if (!Guid.TryParse(id, out _) || started <= 0) continue;
            entries.Add(new(id, started, Text(entry, "QueueID")));
        }
        int? total = root.TryGetProperty("Total", out var totalValue) && totalValue.ValueKind == JsonValueKind.Number && totalValue.TryGetInt32(out var value) && value >= 0 ? value : null;
        return new RiotMatchHistoryPage(entries, history.GetArrayLength(), total);
    }
    public async Task<JsonDocument?> GetMatchDetails(string matchId, CancellationToken ct)
    {
        var current = CurrentSession;
        EnsurePlayerDataSession(current);
        ValidateId(matchId);
        return await Request(HttpMethod.Get, $"/match-details/v1/matches/{matchId}", true, ct, playerData: true, sessionSnapshot: current);
    }
    public async Task<IReadOnlyDictionary<string, (string GameName, string TagLine)>> GetPlayerNames(IEnumerable<string> subjects, CancellationToken ct)
    {
        var current = CurrentSession;
        EnsurePlayerDataSession(current);
        var ids = subjects.Where(id => Guid.TryParse(id, out _)).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        var names = new Dictionary<string, (string GameName, string TagLine)>(StringComparer.OrdinalIgnoreCase);
        foreach (var batch in ids.Chunk(20))
        {
            using var result = await Request(HttpMethod.Put, "/name-service/v2/players", false, ct,
                playerData: true, jsonBody: JsonSerializer.Serialize(batch), sessionSnapshot: current);
            if (result?.RootElement.ValueKind != JsonValueKind.Array) continue;
            foreach (var player in result.RootElement.EnumerateArray())
            {
                var subject = TextIgnoreCase(player, "Subject");
                if (!Guid.TryParse(subject, out _)) continue;
                var gameName = TextIgnoreCase(player, "GameName");
                var tagLine = TextIgnoreCase(player, "TagLine");
                if (gameName.Length > 0) names[subject] = (gameName, tagLine);
            }
        }
        return names;
    }
    private void EnsurePlayerDataSession(RiotSession? snapshot = null)
    {
        var current = snapshot ?? CurrentSession;
        if (!Guid.TryParse(current.Subject, out _) || current.PlayerDataServer.Length == 0 || current.Access.Length == 0 || current.Entitlement.Length == 0)
            throw new InvalidOperationException("アカウントに接続してください");
    }
    internal static HashSet<string> ParseOwnedAgents(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("エージェントの所有情報が不正です");
        JsonElement entries;
        if (root.TryGetProperty("Entitlements", out var direct)) entries = direct;
        else if (root.TryGetProperty("EntitlementsByTypes", out var groups) && groups.ValueKind == JsonValueKind.Array)
        {
            var matches = groups.EnumerateArray().Where(x => x.ValueKind == JsonValueKind.Object && Text(x, "ItemTypeID").Equals(AgentItemType, StringComparison.OrdinalIgnoreCase)).ToArray();
            if (matches.Length != 1 || !matches[0].TryGetProperty("Entitlements", out entries)) throw new InvalidDataException("エージェントの所有情報が不正です");
        }
        else throw new InvalidDataException("エージェントの所有情報がありません");
        if (entries.ValueKind != JsonValueKind.Array) throw new InvalidDataException("エージェントの所有情報が不正です");
        var result = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in entries.EnumerateArray())
        {
            if (entry.ValueKind != JsonValueKind.Object || !Guid.TryParse(Text(entry, "ItemID"), out var id))
                throw new InvalidDataException("エージェントの所有情報が不正です");
            result.Add(id.ToString());
        }
        return result;
    }
    public async Task<string?> GetMatchId(CancellationToken ct)
    {
        var current = CurrentSession;
        using var result = await Request(HttpMethod.Get, $"/pregame/v1/players/{current.Subject}", true, ct, sessionSnapshot: current, timeout: PregameRequestTimeout);
        if (result == null) return null;
        var id = Text(result.RootElement, "MatchID");
        return Guid.TryParse(id, out _) ? id : null;
    }
    public async Task<string?> GetCoreMatchId(CancellationToken ct)
    {
        var current = CurrentSession;
        using var result = await Request(HttpMethod.Get, $"/core-game/v1/players/{current.Subject}", true, ct, sessionSnapshot: current);
        if (result == null) return null;
        var id = Text(result.RootElement, "MatchID");
        if (!Guid.TryParse(id, out _)) throw new InvalidDataException("試合情報が不正です");
        return id;
    }
    public async Task<string?> GetCoreMapId(string matchId, CancellationToken ct)
    {
        ValidateId(matchId);
        var current = CurrentSession;
        using var result = await Request(HttpMethod.Get, $"/core-game/v1/matches/{matchId}", true, ct, sessionSnapshot: current);
        if (result == null) return null;
        var root = result.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !string.Equals(Text(root, "MatchID"), matchId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("試合情報が不正です");
        var map = Text(root, "MapID");
        return string.IsNullOrWhiteSpace(map) ? null : map;
    }
    public async Task<LiveRosterMatch?> GetCoreLiveRoster(string matchId, CancellationToken ct)
    {
        ValidateId(matchId);
        var current = CurrentSession;
        using var result = await Request(HttpMethod.Get, $"/core-game/v1/matches/{matchId}", true, ct, sessionSnapshot: current);
        if (result == null) return null;
        return ParseCoreLiveRoster(result.RootElement, matchId);
    }

    public async Task<(int Allies, int Enemies)?> GetLocalLiveScore(string matchMap, CancellationToken ct)
    {
        var current = CurrentSession;
        var connection = await GetLocalEventConnection(current.Subject, ct);
        if (connection is null) return null;
        using var data = await LocalGet(connection.Value.Port, connection.Value.Password, "/chat/v4/presences", ct);
        if (!string.Equals(CurrentSession.Subject, current.Subject, StringComparison.OrdinalIgnoreCase)) return null;
        return ParseLocalLiveScore(data.RootElement, current.Subject, matchMap);
    }

    internal static (int Allies, int Enemies)? ParseLocalLiveScore(JsonElement root, string subject, string matchMap)
    {
        var expectedMap = MapCatalog.Key(matchMap);
        if (expectedMap.Length == 0 || root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("presences", out var presences) || presences.ValueKind != JsonValueKind.Array) return null;
        foreach (var presence in presences.EnumerateArray())
        {
            if (presence.ValueKind != JsonValueKind.Object || !Text(presence, "puuid").Equals(subject, StringComparison.OrdinalIgnoreCase) || !Text(presence, "product").Equals("valorant", StringComparison.OrdinalIgnoreCase)) continue;
            var encoded = Text(presence, "private");
            if (encoded.Length == 0 || encoded.Length > 65536) return null;
            try
            {
                using var data = JsonDocument.Parse(Convert.FromBase64String(encoded));
                var p = data.RootElement;
                if (p.ValueKind != JsonValueKind.Object) return null;
                var match = p.TryGetProperty("matchPresenceData", out var m) && m.ValueKind == JsonValueKind.Object ? m : default;
                var party = p.TryGetProperty("partyPresenceData", out var pt) && pt.ValueKind == JsonValueKind.Object ? pt : default;
                var loopState = FirstNonEmpty(Text(match, "sessionLoopState"), Text(p, "sessionLoopState"), Text(party, "partyOwnerSessionLoopState"));
                var presenceMap = FirstNonEmpty(Text(match, "matchMap"), Text(p, "matchMap"));
                if (!loopState.Equals("INGAME", StringComparison.OrdinalIgnoreCase) ||
                    !MapCatalog.Key(presenceMap).Equals(expectedMap, StringComparison.OrdinalIgnoreCase) ||
                    !TryReadScore(p, match, party, "partyOwnerMatchScoreAllyTeam", out var allies) ||
                    !TryReadScore(p, match, party, "partyOwnerMatchScoreEnemyTeam", out var enemies)) return null;
                return (allies, enemies);
            }
            catch (Exception e) when (e is FormatException or JsonException or InvalidOperationException) { return null; }
        }
        return null;
    }

    private static string FirstNonEmpty(params string[] values)
    {
        foreach (var value in values) if (!string.IsNullOrWhiteSpace(value)) return value;
        return "";
    }

    private static bool TryReadScore(JsonElement root, JsonElement match, JsonElement party, string property, out int score)
    {
        score = 0;
        foreach (var container in new[] { root, match, party })
        {
            if (container.ValueKind == JsonValueKind.Object &&
                container.TryGetProperty(property, out var element) &&
                element.ValueKind == JsonValueKind.Number &&
                element.TryGetInt32(out var value) && value >= 0)
            {
                score = value;
                return true;
            }
        }
        return false;
    }

    internal static LiveRosterMatch ParseCoreLiveRoster(JsonElement root, string matchId)
    {
        if (root.ValueKind != JsonValueKind.Object || !string.Equals(Text(root, "MatchID"), matchId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("試合情報が不正です");
        var players = new List<LiveRosterPlayer>();
        if (root.TryGetProperty("Players", out var list) && list.ValueKind == JsonValueKind.Array)
            foreach (var item in list.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object || !Guid.TryParse(Text(item, "Subject"), out _) ||
                    item.TryGetProperty("IsCoach", out var coach) && coach.ValueKind == JsonValueKind.True) continue;
                var incognito = true; var hideLevel = true; int? level = null;
                if (item.TryGetProperty("PlayerIdentity", out var identity) && identity.ValueKind == JsonValueKind.Object)
                {
                    incognito = !identity.TryGetProperty("Incognito", out var hidden) || hidden.ValueKind != JsonValueKind.False;
                    hideLevel = !identity.TryGetProperty("HideAccountLevel", out var hide) || hide.ValueKind != JsonValueKind.False;
                    if (!hideLevel && identity.TryGetProperty("AccountLevel", out var lv) && lv.ValueKind == JsonValueKind.Number && lv.TryGetInt32(out var n) && n >= 0) level = n;
                }
                players.Add(new(Text(item, "Subject"), Text(item, "TeamID"), Text(item, "CharacterID"), incognito, hideLevel, level));
            }
        return new(matchId, Text(root, "MapID"), Text(root, "ModeID"), players);
    }
    public async Task<string?> GetPartyState(CancellationToken ct)
    {
        var current = CurrentSession;
        using var player = await Request(HttpMethod.Get, $"/parties/v1/players/{current.Subject}", true, ct, sessionSnapshot: current);
        if (player == null) return null;
        var partyId = Text(player.RootElement, "CurrentPartyID");
        if (!Guid.TryParse(partyId, out _)) throw new InvalidDataException("パーティー情報が不正です");
        using var party = await Request(HttpMethod.Get, $"/parties/v1/parties/{partyId}", true, ct, sessionSnapshot: current);
        if (party == null) return null;
        var root = party.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !string.Equals(Text(root, "ID"), partyId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("パーティー情報が不正です");
        var state = Text(root, "State");
        return string.IsNullOrWhiteSpace(state) ? null : state;
    }
    public async Task<bool?> IsCharacterSelectReady(string matchId, CancellationToken ct)
    {
        ValidateId(matchId);
        var current = CurrentSession;
        if (!await HasMatchingLocalAccount(current, ct)) return null;
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VALORANT", "Saved", "Logs", "ShooterGame.log");
        return logTracker.Check(path, matchId);
    }
    private async Task<bool> HasMatchingLocalAccount(RiotSession current, CancellationToken ct)
    {
        var subject = current.Subject;
        var wsSubject = Volatile.Read(ref localEventConnectedSubject);
        if (wsSubject.Length > 0 && string.Equals(wsSubject, subject, StringComparison.OrdinalIgnoreCase))
            return CurrentSession.Subject.Equals(subject, StringComparison.OrdinalIgnoreCase);
        if (!string.Equals(readinessSubject, subject, StringComparison.OrdinalIgnoreCase) || DateTimeOffset.UtcNow >= readinessConnectionCheck)
        {
            readinessLocalAccount = await GetLocalEventConnection(subject, ct) != null;
            readinessSubject = subject;
            readinessConnectionCheck = DateTimeOffset.UtcNow.AddSeconds(3);
        }
        return readinessLocalAccount && CurrentSession.Subject.Equals(subject, StringComparison.OrdinalIgnoreCase);
    }
    public async Task<string?> GetLocalGameFlowState(CancellationToken ct)
    {
        if (!await HasMatchingLocalAccount(CurrentSession, ct)) return null;
        var path = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "VALORANT", "Saved", "Logs", "ShooterGame.log");
        try { return GameFlowStateFromLog(ReadTailShared(path, 512 * 1024)); }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException) { return null; }
    }
    internal static string? GameFlowStateFromLog(string log)
    {
        string? latest = null;
        foreach (Match match in GameFlowRegex.Matches(log)) latest = match.Groups[1].Value;
        return latest;
    }
    internal static bool? CharacterSelectReadyFromLog(string log, string matchId)
    {
        var state = GameFlowStateFromLog(log);
        if (state == null) return null;
        if (!string.Equals(state, "Pregame", StringComparison.Ordinal)) return false;
        return log.Contains(matchId, StringComparison.OrdinalIgnoreCase);
    }
    public async Task<MatchSnapshot> GetMatch(string matchId, CancellationToken ct)
    {
        ValidateId(matchId);
        var current = CurrentSession;
        using var result = await Request(HttpMethod.Get, $"/pregame/v1/matches/{matchId}", false, ct, sessionSnapshot: current, timeout: PregameRequestTimeout);
        return ParseMatchSnapshot(result!.RootElement);
    }
    private static MatchSnapshot ParseMatchSnapshot(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("マッチ情報が不正です");
        var players = new List<Player>();
        if (root.TryGetProperty("AllyTeam", out var team) && team.ValueKind == JsonValueKind.Object && team.TryGetProperty("Players", out var list))
        {
            if (list.ValueKind != JsonValueKind.Array) throw new InvalidDataException("参加者情報が不正です");
            players.AddRange(list.EnumerateArray().Select(p => new Player(Text(p, "Subject"), Text(p, "CharacterID"), Text(p, "CharacterSelectionState"))));
        }
        return new(Text(root, "ID"), Text(root, "PregameState"), players, Text(root, "MapID"));
    }
    private static MatchSnapshot? TryParseMatchSnapshot(JsonElement root, string expectedMatchId)
    {
        if (root.ValueKind != JsonValueKind.Object) return null;
        var id = Text(root, "ID");
        if (!string.Equals(id, expectedMatchId, StringComparison.OrdinalIgnoreCase)) return null;
        if (!root.TryGetProperty("AllyTeam", out var team) || team.ValueKind != JsonValueKind.Object ||
            !team.TryGetProperty("Players", out var list) || list.ValueKind != JsonValueKind.Array)
            return null;
        var players = list.EnumerateArray().Select(p => new Player(Text(p, "Subject"), Text(p, "CharacterID"), Text(p, "CharacterSelectionState"))).ToList();
        return new MatchSnapshot(id, Text(root, "PregameState"), players, Text(root, "MapID"));
    }
    public async Task Select(string matchId, string agentId, CancellationToken ct)
    {
        using var _ = await Write("select", matchId, agentId, ct);
    }
    public async Task Lock(string matchId, string agentId, CancellationToken ct)
    {
        using var _ = await Write("lock", matchId, agentId, ct);
    }
    public async Task<MatchSnapshot?> LockAndConfirm(string matchId, string agentId, CancellationToken ct)
    {
        using var result = await Write("lock", matchId, agentId, ct);
        return result == null ? null : TryParseMatchSnapshot(result.RootElement, matchId);
    }
    public async Task<bool> IsInMatch(CancellationToken ct)
    {
        var current = CurrentSession;
        using var core = await Request(HttpMethod.Get, $"/core-game/v1/players/{current.Subject}", true, ct, sessionSnapshot: current);
        if (core != null) return true;
        using var pregame = await Request(HttpMethod.Get, $"/pregame/v1/players/{current.Subject}", true, ct, sessionSnapshot: current);
        return pregame != null;
    }
    public async Task<PartyQueueSnapshot> GetQueueSnapshot(CancellationToken ct)
    {
        var current = CurrentSession;
        using var player = await Request(HttpMethod.Get, $"/parties/v1/players/{current.Subject}", true, ct, sessionSnapshot: current);
        if (player == null) throw new ClientUnavailableException("パーティー情報を取得できません");
        var partyId = Text(player.RootElement, "CurrentPartyID");
        ValidateId(partyId);
        using var party = await Request(HttpMethod.Get, $"/parties/v1/parties/{partyId}", false, ct, sessionSnapshot: current);
        var root = party!.RootElement;
        if (root.ValueKind != JsonValueKind.Object || !string.Equals(Text(root, "ID"), partyId, StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("パーティー情報が不正です");
        var state = Text(root, "State");
        var queueId = root.TryGetProperty("MatchmakingData", out var matchmaking) ? Text(matchmaking, "QueueID") : "";
        var entryTime = Text(root, "QueueEntryTime");
        if (!DateTimeOffset.TryParse(entryTime, CultureInfo.InvariantCulture, DateTimeStyles.None, out _))
            throw new InvalidDataException("キュー状態が不正です");
        var isLeader = false;
        if (root.TryGetProperty("Members", out var members) && members.ValueKind == JsonValueKind.Array)
        {
            var self = members.EnumerateArray().FirstOrDefault(member => Text(member, "Subject") == current.Subject);
            isLeader = self.ValueKind == JsonValueKind.Object && self.TryGetProperty("IsOwner", out var owner) && owner.ValueKind == JsonValueKind.True;
        }
        if (!root.TryGetProperty("RestrictedSeconds", out var seconds) || seconds.ValueKind != JsonValueKind.Number ||
            !seconds.TryGetInt32(out var restricted) || restricted < 0)
            throw new InvalidDataException("キュー制限情報が不正です");
        // QueueEntryTime can retain the previous search's timestamp after returning to the lobby.
        return new PartyQueueSnapshot(partyId, state, queueId, isLeader, restricted,
            StringArray(root, "EligibleQueues"), StringArray(root, "QueueIneligibilities"), state == "MATCHMAKING");
    }
    public async Task EnterQueue(string partyId, CancellationToken ct)
    {
        ValidateId(partyId);
        using var result = await Request(HttpMethod.Post, $"/parties/v1/parties/{partyId}/matchmaking/join", false, ct);
    }
    public async Task LeaveQueue(string partyId, CancellationToken ct)
    {
        ValidateId(partyId);
        using var result = await Request(HttpMethod.Post, $"/parties/v1/parties/{partyId}/matchmaking/leave", false, ct);
    }
    private async Task<JsonDocument?> Write(string action, string matchId, string agentId, CancellationToken ct)
    {
        ValidateId(matchId); ValidateId(agentId);
        return await Request(HttpMethod.Post, $"/pregame/v1/matches/{matchId}/{action}/{agentId}", false, ct, timeout: PregameRequestTimeout);
    }
    private static void ValidateId(string id) { if (!Guid.TryParse(id, out _)) throw new InvalidDataException("識別情報が不正です"); }
    private static string[] StringArray(JsonElement element, string name)
    {
        if (!element.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Array)
            throw new InvalidDataException($"{name} が不正です");
        var result = new List<string>();
        foreach (var item in value.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString()))
                throw new InvalidDataException($"{name} が不正です");
            result.Add(item.GetString()!);
        }
        return result.ToArray();
    }
    private static JsonElement RequiredProperty(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object || !element.TryGetProperty(name, out var value))
            throw new InvalidDataException($"Riot response is missing '{name}'.");
        return value;
    }

    private static string RequiredString(JsonElement element, string name)
    {
        var value = RequiredProperty(element, name);
        if (value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(value.GetString()))
            throw new InvalidDataException($"Riot response has an invalid '{name}'.");
        return value.GetString()!;
    }

    private static string Text(JsonElement element, string name) => element.ValueKind == JsonValueKind.Object && element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    private static string TextIgnoreCase(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return "";
        foreach (var property in element.EnumerateObject())
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase) && property.Value.ValueKind == JsonValueKind.String)
                return property.Value.GetString() ?? "";
        return "";
    }
    public void Dispose() { Logout(); remote.Dispose(); local.Dispose(); }
}

internal sealed class CharacterSelectLogTracker
{
    private const int TailBytes = 512 * 1024;
    private const int MaxTransientReadFailures = 5;
    private readonly object gate = new();
    private string trackedMatchId = "";
    private bool matchIdSeen;
    private string lastPath = "";
    private long lastLength = -1;
    private DateTime lastWriteUtc = DateTime.MinValue;
    private bool? cachedResult;
    private int consecutiveReadFailures;

    internal void Reset()
    {
        lock (gate)
        {
            trackedMatchId = "";
            matchIdSeen = false;
            lastPath = "";
            lastLength = -1;
            lastWriteUtc = DateTime.MinValue;
            cachedResult = null;
            consecutiveReadFailures = 0;
        }
    }

    internal bool? Check(string path, string matchId)
    {
        lock (gate)
        {
            if (!string.Equals(trackedMatchId, matchId, StringComparison.OrdinalIgnoreCase))
            {
                trackedMatchId = matchId;
                matchIdSeen = false;
                lastLength = -1;
                lastWriteUtc = DateTime.MinValue;
                cachedResult = null;
                consecutiveReadFailures = 0;
            }
            try
            {
                var info = new FileInfo(path);
                if (!info.Exists)
                {
                    consecutiveReadFailures++;
                    return consecutiveReadFailures <= MaxTransientReadFailures ? false : null;
                }
                var length = info.Length;
                var modified = info.LastWriteTimeUtc;
                if (string.Equals(lastPath, path, StringComparison.OrdinalIgnoreCase) &&
                    length == lastLength && modified == lastWriteUtc && cachedResult.HasValue)
                {
                    consecutiveReadFailures = 0;
                    return cachedResult;
                }
                var log = RiotApi.ReadTailShared(path, TailBytes);
                consecutiveReadFailures = 0;
                if (!matchIdSeen && log.Contains(matchId, StringComparison.OrdinalIgnoreCase))
                    matchIdSeen = true;
                var state = RiotApi.GameFlowStateFromLog(log);
                bool? result = state == null
                    ? null
                    : string.Equals(state, "Pregame", StringComparison.Ordinal) && matchIdSeen;
                var after = new FileInfo(path);
                if (after.Exists && after.Length == length && after.LastWriteTimeUtc == modified)
                {
                    lastPath = path;
                    lastLength = length;
                    lastWriteUtc = modified;
                    cachedResult = result;
                }
                else
                {
                    lastLength = -1;
                    cachedResult = null;
                }
                return result;
            }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                lastLength = -1;
                cachedResult = null;
                consecutiveReadFailures++;
                return consecutiveReadFailures <= MaxTransientReadFailures ? false : null;
            }
        }
    }
}
