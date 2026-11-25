using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace ValoAutoPick;

internal sealed class CatalogClient : IDisposable
{
    private static readonly Uri WeaponsUri = new("https://valorant-api.com/v1/weapons?language=ja-JP");
    private static readonly Uri TiersUri = new("https://valorant-api.com/v1/contenttiers?language=ja-JP");
    private static readonly Uri BundlesUri = new("https://valorant-api.com/v1/bundles?language=ja-JP");
    private static readonly Uri BuddiesUri = new("https://valorant-api.com/v1/buddies?language=ja-JP");
    private static readonly Uri PlayerCardsUri = new("https://valorant-api.com/v1/playercards?language=ja-JP");
    private static readonly Uri SpraysUri = new("https://valorant-api.com/v1/sprays?language=ja-JP");
    private static readonly Uri PlayerTitlesUri = new("https://valorant-api.com/v1/playertitles?language=ja-JP");
    private static readonly TimeSpan CacheLifetime = TimeSpan.FromHours(12);
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient http;
    private readonly string cachePath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
        "ValoStoreCheck",
        "catalog-cache.json");

    public CatalogClient()
    {
        var handler = new SocketsHttpHandler
        {
            AutomaticDecompression = DecompressionMethods.GZip | DecompressionMethods.Deflate | DecompressionMethods.Brotli,
            PooledConnectionLifetime = TimeSpan.FromMinutes(10)
        };
        http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(25) };
        http.DefaultRequestHeaders.UserAgent.Add(new ProductInfoHeaderValue("SpikeSide", typeof(CatalogClient).Assembly.GetName().Version!.ToString(3)));
        http.DefaultRequestHeaders.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
    }

    public async Task<CatalogSnapshot> LoadAsync(bool refresh, CancellationToken cancellationToken)
    {
        var cached = await TryReadCacheAsync(cancellationToken);
        if (!refresh && cached is { } fresh && fresh.HasWeaponImages && fresh.HasPlayerTitles && DateTimeOffset.UtcNow - fresh.UpdatedAt is var age && age >= TimeSpan.Zero && age < CacheLifetime)
            return fresh with { FromCache = true };

        try
        {
            var weaponsTask = FetchListAsync<WeaponData>(WeaponsUri, cancellationToken);
            var tiersTask = FetchOptionalListAsync<ContentTierData>(TiersUri, cancellationToken);
            var bundlesTask = FetchOptionalListAsync<BundleData>(BundlesUri, cancellationToken);
            var buddiesTask = FetchOptionalListAsync<BuddyData>(BuddiesUri, cancellationToken);
            var cardsTask = FetchOptionalListAsync<AccessoryData>(PlayerCardsUri, cancellationToken);
            var spraysTask = FetchOptionalListAsync<AccessoryData>(SpraysUri, cancellationToken);
            var titlesTask = FetchOptionalListAsync<PlayerTitleData>(PlayerTitlesUri, cancellationToken);
            await Task.WhenAll(weaponsTask, tiersTask, bundlesTask, buddiesTask, cardsTask, spraysTask, titlesTask);
            var weapons = await weaponsTask;
            if (weapons.Count == 0) throw new InvalidDataException("武器カタログが空です。");
            var optionalFailed = tiersTask.Result is null || bundlesTask.Result is null || buddiesTask.Result is null || cardsTask.Result is null || spraysTask.Result is null || titlesTask.Result is null;
            if (optionalFailed && cached is { } fallback) return fallback with { FromCache = true };

            var payload = new CatalogCachePayload(
                DateTimeOffset.UtcNow,
                weapons,
                await tiersTask ?? [],
                await bundlesTask ?? [],
                await buddiesTask ?? [],
                await cardsTask ?? [],
                await spraysTask ?? [],
                await titlesTask ?? []);
            var snapshot = await Task.Run(() => CreateSnapshot(payload, fromCache: false), cancellationToken);
            if (!optionalFailed) await TryWriteCacheAsync(payload, cancellationToken);
            return snapshot;
        }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidDataException or IOException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (cached is { } fallback)
                return fallback with { FromCache = true };
            throw new InvalidOperationException("アイテムカタログを取得できませんでした。ネット接続を確認して、もう一度お試しください。", ex);
        }
    }

    private async Task<List<T>> FetchListAsync<T>(Uri uri, CancellationToken cancellationToken)
    {
        using var response = await http.GetAsync(uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();
        var envelope = await response.Content.ReadFromJsonAsync<ApiEnvelope<List<T>>>(JsonOptions, cancellationToken);
        return envelope?.Data ?? throw new InvalidDataException($"カタログの形式を読み取れませんでした: {uri.AbsolutePath}");
    }

    private async Task<List<T>?> FetchOptionalListAsync<T>(Uri uri, CancellationToken cancellationToken)
    {
        try { return await FetchListAsync<T>(uri, cancellationToken); }
        catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException or JsonException or InvalidDataException or IOException)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return null;
        }
    }

    private async Task<CatalogSnapshot?> TryReadCacheAsync(CancellationToken cancellationToken)
    {
        try
        {
            if (!File.Exists(cachePath)) return null;
            var json = await File.ReadAllTextAsync(cachePath, cancellationToken);
            return await Task.Run(() =>
            {
                var payload = JsonSerializer.Deserialize<CatalogCachePayload>(json, JsonOptions);
                if (payload?.Weapons is not { Count: > 0 }) return null;
                return CreateSnapshot(payload, fromCache: true);
            }, cancellationToken);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or InvalidOperationException or NotSupportedException or ArgumentException)
        {
            return null;
        }
    }

    private async Task TryWriteCacheAsync(CatalogCachePayload payload, CancellationToken cancellationToken)
    {
        string? tempPath = null;
        try
        {
            var directory = Path.GetDirectoryName(cachePath)!;
            Directory.CreateDirectory(directory);
            tempPath = $"{cachePath}.{Guid.NewGuid():N}.tmp";
            var json = await Task.Run(() => JsonSerializer.Serialize(payload, JsonOptions), cancellationToken);
            await File.WriteAllTextAsync(tempPath, json, cancellationToken);
            File.Move(tempPath, cachePath, overwrite: true);
            tempPath = null;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            // A cache write failure should not prevent the online catalog from opening.
        }
        finally
        {
            if (tempPath is not null)
            {
                try { File.Delete(tempPath); }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
            }
        }
    }

    internal static CatalogSnapshot CreateSnapshot(CatalogCachePayload source, bool fromCache)
    {
        var tiersSource = source.Tiers ?? [];
        var weaponsSource = source.Weapons ?? [];
        var bundlesSource = source.Bundles ?? [];
        var buddiesSource = source.Buddies ?? [];
        var cardsSource = source.PlayerCards ?? [];
        var spraysSource = source.Sprays ?? [];
        var titlesSource = source.PlayerTitles ?? [];
        var tiers = tiersSource.Where(t => t is not null && !string.IsNullOrWhiteSpace(t.Uuid))
            .GroupBy(t => t.Uuid, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(group => group.Key, group => group.First(), StringComparer.OrdinalIgnoreCase);
        var weapons = weaponsSource
            .Where(w => w is not null && !string.IsNullOrWhiteSpace(w.DisplayName) && !IsRandomFavoriteName(w.DisplayName))
            .Select(w => new WeaponCard(
                w.DisplayName,
                w.Category ?? "",
                (w.Skins ?? []).Where(s => s is not null && !string.IsNullOrWhiteSpace(s.DisplayName) && !IsRandomFavoriteSkin(s))
                    .Select(s =>
                    {
                        tiers.TryGetValue(s.ContentTierUuid ?? "", out var tier);
                        var chromas = (s.Chromas ?? []).Where(c => c is not null).Select(c => new ChromaCard(
                            c.Uuid ?? "",
                            string.IsNullOrWhiteSpace(c.DisplayName) ? "標準カラー" : c.DisplayName,
                            SafeAsset(c.DisplayIcon),
                            SafeAsset(c.FullRender),
                            SafeAsset(c.Swatch),
                            SafeAsset(c.StreamedVideo))).ToList();
                        var levels = (s.Levels ?? []).Where(l => l is not null).Select(l => new LevelCard(
                            l.Uuid ?? "",
                            string.IsNullOrWhiteSpace(l.DisplayName) ? "レベル" : l.DisplayName,
                            SafeAsset(l.DisplayIcon),
                            SafeAsset(l.StreamedVideo))).ToList();
                        return new SkinCard(
                            s.Uuid ?? "",
                            s.DisplayName,
                            w.DisplayName,
                            w.Category ?? "",
                            SafeAsset(s.DisplayIcon),
                            SafeAsset(w.DisplayIcon),
                            tier?.DisplayName ?? "",
                            NormalizeColor(tier?.HighlightColor),
                            chromas,
                            levels);
                    })
                    .ToList()))
            // Weapons whose only entries were pseudo items would otherwise show as empty groups.
            .Where(w => w.Skins.Count > 0)
            .OrderBy(w => w.Name, StringComparer.CurrentCulture)
            .ToList();

        var bundles = bundlesSource
            .Where(bundle => bundle is not null && !string.IsNullOrWhiteSpace(bundle.Uuid) && !string.IsNullOrWhiteSpace(bundle.DisplayName))
            .GroupBy(bundle => bundle.Uuid, StringComparer.OrdinalIgnoreCase)
            .ToDictionary(
                group => group.Key,
                group => new BundleCard(group.First().DisplayName,
                    SafeAssets(group.First().DisplayIcon, group.First().DisplayIcon2, group.First().VerticalPromoImage)),
                StringComparer.OrdinalIgnoreCase);

        var accessories = new Dictionary<string, StoreItemCard>(StringComparer.OrdinalIgnoreCase);
        foreach (var buddy in buddiesSource.Where(buddy => buddy is not null))
        {
            AddAccessory(accessories, buddy.Uuid, buddy.DisplayName, "ガンバディー", buddy.DisplayIcon);
            foreach (var level in (buddy.Levels ?? []).Where(level => level is not null))
            {
                var name = level.CharmLevel > 0 ? $"{buddy.DisplayName} Lv.{level.CharmLevel}" : buddy.DisplayName;
                AddAccessory(accessories, level.Uuid, name, "ガンバディー", level.DisplayIcon, buddy.DisplayIcon);
            }
        }
        foreach (var card in cardsSource.Where(card => card is not null)) AddAccessory(accessories, card.Uuid, card.DisplayName, "プレイヤーカード", card.DisplayIcon);
        foreach (var spray in spraysSource.Where(spray => spray is not null)) AddAccessory(accessories, spray.Uuid, spray.DisplayName, "スプレー", spray.DisplayIcon);
        foreach (var title in titlesSource.Where(title => title is not null))
        {
            var displayName = !string.IsNullOrWhiteSpace(title.DisplayName) ? title.DisplayName.Trim() : title.TitleText?.Trim() ?? "";
            var titleText = !string.IsNullOrWhiteSpace(title.TitleText) ? title.TitleText.Trim() : displayName;
            if (!string.IsNullOrWhiteSpace(title.Uuid) && !string.IsNullOrWhiteSpace(displayName))
                accessories.TryAdd(title.Uuid, new StoreItemCard(displayName, "プレイヤータイトル", [], titleText));
        }

        var skins = weapons.SelectMany(w => w.Skins).ToList();
        var videoCount = skins.SelectMany(s => s.Levels.Select(l => l.Video).Concat(s.Chromas.Select(c => c.Video)))
            .Where(video => !string.IsNullOrWhiteSpace(video))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .Count();
        return new CatalogSnapshot(
            source.UpdatedAt,
            weapons,
            bundles,
            accessories,
            skins.Count,
            skins.Sum(s => s.Chromas.Count),
            videoCount,
            fromCache)
        {
            // Older cache files did not retain weapon display icons. Fetch once so missing
            // skin art has a real weapon image to fall back to, while keeping the old cache
            // available if the refresh cannot reach the catalog API.
            HasWeaponImages = weaponsSource.Any(weapon => weapon is not null && !string.IsNullOrWhiteSpace(SafeAsset(weapon.DisplayIcon))),
            HasPlayerTitles = titlesSource.Any(title => title is not null && !string.IsNullOrWhiteSpace(title.Uuid))
        };
    }

    /// <summary>
    /// The weapons API lists a per-weapon "Random Favorite Skin" (ja-JP: ランダムお気に入りスキン) entry.
    /// It is the in-game randomizer loadout option, not a purchasable skin, so it is kept out of the catalog.
    /// Its UUID differs per weapon, so the skin and its level/chroma names are checked instead.
    /// </summary>
    private static bool IsRandomFavoriteSkin(SkinData skin) =>
        IsRandomFavoriteName(skin.DisplayName) ||
        (skin.Levels ?? []).Any(level => level is not null && IsRandomFavoriteName(level.DisplayName)) ||
        (skin.Chromas ?? []).Any(chroma => chroma is not null && IsRandomFavoriteName(chroma.DisplayName));

    private static bool IsRandomFavoriteName(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return false;
        string normalized;
        try { normalized = value.Normalize(System.Text.NormalizationForm.FormKC); }
        catch (ArgumentException) { normalized = value; }
        normalized = new string(normalized.Where(c => !char.IsWhiteSpace(c) && c is not ('・' or '-' or '_')).ToArray())
            .ToLowerInvariant();
        return (normalized.Contains("ランダム", StringComparison.Ordinal) && normalized.Contains("お気に入り", StringComparison.Ordinal)) ||
               (normalized.Contains("random", StringComparison.Ordinal) &&
                (normalized.Contains("favorite", StringComparison.Ordinal) || normalized.Contains("favourite", StringComparison.Ordinal)));
    }

    private static void AddAccessory(Dictionary<string, StoreItemCard> accessories, string id, string name, string type, params string?[] images)
    {
        if (!string.IsNullOrWhiteSpace(id) && !string.IsNullOrWhiteSpace(name))
            accessories.TryAdd(id, new StoreItemCard(name, type, SafeAssets(images)));
    }

    private static IReadOnlyList<string> SafeAssets(params string?[] values) => values
        .Select(SafeAsset)
        .Where(url => !string.IsNullOrWhiteSpace(url))
        .Select(url => url!)
        .Distinct(StringComparer.OrdinalIgnoreCase)
        .ToArray();

    private static string? SafeAsset(string? value)
    {
        if (!Uri.TryCreate(value, UriKind.Absolute, out var uri) || uri.Scheme != Uri.UriSchemeHttps)
            return null;
        if (!uri.Host.Equals("media.valorant-api.com", StringComparison.OrdinalIgnoreCase) &&
            !uri.Host.Equals("valorant.dyn.riotcdn.net", StringComparison.OrdinalIgnoreCase))
            return null;
        return uri.AbsoluteUri;
    }

    private static string NormalizeColor(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "#b9c1cf";
        var hex = value.Trim().TrimStart('#');
        if (hex.Length == 8) hex = hex[..6];
        return hex.Length == 6 && hex.All(Uri.IsHexDigit) ? "#" + hex : "#b9c1cf";
    }

    public void Dispose() => http.Dispose();
}
