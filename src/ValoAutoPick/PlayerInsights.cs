using System.Text.Json;

namespace ValoAutoPick;

public sealed record WalletBalances(long? VP, long? RP, long? KC)
{
    public static WalletBalances Parse(JsonElement root)
    {
        var balances = root.ValueKind == JsonValueKind.Object && root.TryGetProperty("Balances", out var b) && b.ValueKind == JsonValueKind.Object ? b : default;
        long? Read(string id) => balances.ValueKind == JsonValueKind.Object && balances.TryGetProperty(id, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out var n) && n >= 0 ? n : null;
        return new(Read("85ad13f7-3d1b-5128-9eb2-7cd8ee0b5741"), Read("e59aa87c-4cbf-517a-5983-6e81511be9b7"), Read("85ca954a-41f2-ce94-9b45-8ca3dd39a00d"));
    }
}

public sealed record CompetitiveUpdate(string MatchId, int? RankedRatingEarned, int? RankedRatingAfterUpdate, int? TierAfterUpdate)
{
    public static IReadOnlyDictionary<string, CompetitiveUpdate> Parse(JsonElement root)
    {
        var result = new Dictionary<string, CompetitiveUpdate>(StringComparer.OrdinalIgnoreCase);
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("Matches", out var matches) || matches.ValueKind != JsonValueKind.Array) return result;
        foreach (var item in matches.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object || !item.TryGetProperty("MatchID", out var id) || id.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(id.GetString())) continue;
            int? Read(string key) => item.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out var n) ? n : null;
            var matchId = id.GetString()!;
            result.TryAdd(matchId, new(matchId, Read("RankedRatingEarned"), Read("RankedRatingAfterUpdate"), Read("TierAfterUpdate")));
        }
        return result;
    }

    public static MatchDetailsData Attach(MatchDetailsData match, IReadOnlyDictionary<string, CompetitiveUpdate> updates) =>
        updates.TryGetValue(match.Info.MatchId, out var update) && match.Info.QueueId.Equals("competitive", StringComparison.OrdinalIgnoreCase)
            ? match with { CompetitiveUpdate = update } : match;
}

internal sealed class WishlistState
{
    public HashSet<string> SkinIds { get; set; } = new(StringComparer.OrdinalIgnoreCase);
    public bool NotificationsEnabled { get; set; } = true;
    public Dictionary<string, string> NotifiedCycles { get; set; } = new(StringComparer.OrdinalIgnoreCase);
}

internal sealed class WishlistStorage(string path)
{
    public WishlistState Load()
    {
        try
        {
            var state = JsonSerializer.Deserialize<WishlistState>(File.ReadAllBytes(path)) ?? new();
            state.SkinIds = new(state.SkinIds ?? [], StringComparer.OrdinalIgnoreCase);
            state.NotifiedCycles = new(state.NotifiedCycles ?? [], StringComparer.OrdinalIgnoreCase);
            return state;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException) { return new(); }
    }

    public void Save(WishlistState state)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try { File.WriteAllBytes(temporary, JsonSerializer.SerializeToUtf8Bytes(state)); File.Move(temporary, path, overwrite: true); }
        finally { try { File.Delete(temporary); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
    }

    public static bool ShouldNotify(WishlistState state, string account, string cycle, bool hasMatches) =>
        state.NotificationsEnabled && hasMatches && (!state.NotifiedCycles.TryGetValue(account, out var previous) || previous != cycle);

    public static IReadOnlyList<string> MatchOffers(WishlistState state, IEnumerable<StorefrontOffer> offers, Func<string, string?> skinForItem) =>
        offers.Select(offer => skinForItem(offer.ItemId)).Where(id => id is not null && state.SkinIds.Contains(id))
            .Select(id => id!).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
}
