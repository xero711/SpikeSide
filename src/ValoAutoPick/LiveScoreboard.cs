using System.Text.Json;

namespace ValoAutoPick;

// Live values from the Overwolf scoreboard. Only K/D/A is always present; the rest depends on what GEP exposes
// for that player (weapon and spike are only sent for the local player and teammates, for example).
internal sealed record LiveScore(int Kills, int Deaths, int Assists)
{
    public int? Money { get; init; }
    public int? UltPoints { get; init; }
    public int? UltMax { get; init; }
    public bool? Alive { get; init; }
    public int? Shield { get; init; }
    public string? Weapon { get; init; }
    public bool? Spike { get; init; }
    public int? Headshots { get; init; }
}

// A participant of the active Riot roster. Name is "GameName#Tag" when it is known, otherwise empty.
internal readonly record struct LiveScoreTarget(string Subject, string Name);

// The Overwolf bridge supplies a snapshot; only the current match and known player IDs are accepted.
internal static class LiveScoreboard
{
    internal static IReadOnlyDictionary<string, LiveScore> Parse(string message, string matchId, IEnumerable<string> subjects, string self) =>
        Parse(message, matchId, subjects.Select(subject => new LiveScoreTarget(subject, "")), self);

    internal static IReadOnlyDictionary<string, LiveScore> Parse(string message, string matchId, IEnumerable<LiveScoreTarget> targets, string self)
    {
        var scores = new Dictionary<string, LiveScore>(StringComparer.OrdinalIgnoreCase);
        if (string.IsNullOrWhiteSpace(matchId) || message.Length > 262144) return scores;
        var roster = targets.Where(target => target.Subject.Length > 0).ToArray();
        var allowed = roster.Select(target => target.Subject).ToHashSet(StringComparer.OrdinalIgnoreCase);
        // GEP's player_id is not documented to be Riot's PUUID, so a scoreboard entry may also be matched by its
        // Riot ID. Names that occur twice in the roster are ambiguous and are never used.
        var byName = roster.Where(target => NormalizeName(target.Name).Length > 0)
            .GroupBy(target => NormalizeName(target.Name), StringComparer.OrdinalIgnoreCase)
            .Where(group => group.Count() == 1)
            .ToDictionary(group => group.Key, group => group.First().Subject, StringComparer.OrdinalIgnoreCase);
        var exactMatch = false;
        try
        {
            using var document = JsonDocument.Parse(message, new JsonDocumentOptions { MaxDepth = 32 });
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("info", out var info) ||
                info.ValueKind != JsonValueKind.Object || !info.TryGetProperty("match_info", out var match) || match.ValueKind != JsonValueKind.Object) return scores;
            var receivedId = ReadText(match, "match_id");
            if (receivedId.Length == 0) receivedId = ReadText(match, "matchId");
            // GEP may provide a pseudo match ID instead of Riot's ID. When it supplies a real UUID,
            // require a match; otherwise correlate the snapshot with the active roster below.
            exactMatch = receivedId.Equals(matchId, StringComparison.OrdinalIgnoreCase);
            if (!exactMatch && Guid.TryParse(receivedId, out _)) return scores;
            foreach (var property in match.EnumerateObject())
            {
                if (!property.Name.StartsWith("scoreboard_", StringComparison.OrdinalIgnoreCase)) continue;
                JsonDocument? nested = null;
                try
                {
                    var player = property.Value;
                    if (player.ValueKind == JsonValueKind.String) { nested = JsonDocument.Parse(player.GetString() ?? ""); player = nested.RootElement; }
                    var subject = Resolve(player, allowed, byName, self);
                    if (subject is null || scores.ContainsKey(subject) || !TryNumber(player, "kills", out var kills) ||
                        !TryNumber(player, "deaths", out var deaths) || !TryNumber(player, "assists", out var assists)) continue;
                    scores[subject] = new(kills, deaths, assists)
                    {
                        Money = OptionalNumber(player, "money", 99999),
                        UltPoints = OptionalNumber(player, "ult_points", 99),
                        UltMax = OptionalNumber(player, "ult_max", 99),
                        Alive = OptionalBool(player, "alive"),
                        Shield = OptionalNumber(player, "shield", 9),
                        Weapon = ReadText(player, "weapon") is { Length: > 0 and <= 128 } weapon ? weapon : null,
                        Spike = OptionalBool(player, "spike")
                    };
                }
                catch (JsonException) { }
                finally { nested?.Dispose(); }
            }
            ApplyLocalCounters(info, scores, self, exactMatch);
        }
        catch (JsonException) { }
        // A GEP snapshot without Riot's match ID is accepted only when it includes the local player
        // and at least one other participant from the active roster, when one exists.
        if (!scores.ContainsKey(self) || (!exactMatch && allowed.Count > 1 && scores.Count < 2)) scores.Clear();
        return scores;
    }

    // Normalizes "Name #Tag" (GEP) and "Name#Tag" (Riot) to the same key.
    internal static string NormalizeName(string? name)
    {
        if (string.IsNullOrWhiteSpace(name) || !name.Contains('#')) return "";
        return string.Concat(name.Where(c => !char.IsWhiteSpace(c))).ToLowerInvariant();
    }

    private static string? Resolve(JsonElement player, HashSet<string> allowed, Dictionary<string, string> byName, string self)
    {
        var id = ReadText(player, "player_id");
        if (allowed.Contains(id)) return allowed.First(subject => subject.Equals(id, StringComparison.OrdinalIgnoreCase));
        if (byName.TryGetValue(NormalizeName(ReadText(player, "name")), out var named)) return named;
        if (OptionalBool(player, "is_local") == true && allowed.Contains(self)) return self;
        return null;
    }

    // The kill and death features count the local player's own totals. They fill in K/D/A when the scoreboard has not
    // been sent yet, but only for a snapshot that carries Riot's match ID, since the counters have no player to correlate.
    private static void ApplyLocalCounters(JsonElement info, Dictionary<string, LiveScore> scores, string self, bool exactMatch)
    {
        int? Counter(string category, string key) =>
            info.TryGetProperty(category, out var group) && group.ValueKind == JsonValueKind.Object ? OptionalNumber(group, key, 999) : null;
        var kills = Counter("kill", "kills");
        var assists = Counter("kill", "assists");
        var headshots = Counter("kill", "headshots");
        var deaths = Counter("death", "deaths");
        if (scores.TryGetValue(self, out var existing))
        {
            if (headshots is int hs) scores[self] = existing with { Headshots = hs };
            return;
        }
        if (!exactMatch || kills is null && deaths is null && assists is null) return;
        scores[self] = new(kills ?? 0, deaths ?? 0, assists ?? 0) { Headshots = headshots };
    }

    private static string ReadText(JsonElement value, string name) => value.ValueKind == JsonValueKind.Object &&
        value.TryGetProperty(name, out var item) && item.ValueKind == JsonValueKind.String ? item.GetString() ?? "" : "";

    private static int? OptionalNumber(JsonElement value, string name, int max) =>
        TryNumber(value, name, out var result, max) ? result : null;

    private static bool? OptionalBool(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out var item)) return null;
        return item.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(item.GetString(), out var parsed) => parsed,
            _ => null
        };
    }

    private static bool TryNumber(JsonElement value, string name, out int result, int max = 999)
    {
        result = 0;
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(name, out var item)) return false;
        var ok = item.ValueKind == JsonValueKind.Number ? item.TryGetInt32(out result) :
            item.ValueKind == JsonValueKind.String && int.TryParse(item.GetString(), out result);
        return ok && result >= 0 && result <= max;
    }
}
