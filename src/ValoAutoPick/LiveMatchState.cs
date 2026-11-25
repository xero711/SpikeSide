using System.Text.Json;
using System.Text.Json.Nodes;

namespace ValoAutoPick;

// Event-style updates (used by the preview and tests) arrive as independent, sometimes JSON-encoded values rather than a complete match.
// The live roster itself is built by ReplaceRoster from the Riot client.
// Keep only the current match in memory; never resolve hidden identities or persist player data.
internal sealed class LiveMatchState
{
    private readonly Dictionary<string, JsonObject> players = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, string> values = new(StringComparer.OrdinalIgnoreCase);
    public IReadOnlyDictionary<string, JsonObject> Players => players;
    public IReadOnlyDictionary<string, string> Values => values;
    public bool MatchEnded { get; private set; }

    public void Reset() { players.Clear(); values.Clear(); MatchEnded = false; }
    public void SetScore(int allies, int enemies) => values["score"] = JsonSerializer.Serialize(new { won = allies, lost = enemies });

    // When a refresh fails, the last confirmed roster stays on screen (labelled with its time) so one dropped
    // request does not blank the list or close the player the user is reading. This decides when it must go.
    // The same rule covers a 429 wait and a tab that was hidden. A healthy poll replaces the snapshot every 5 s
    // and one poll can take ~20 s (two 8 s remote calls plus the 4 s local presence call), so the answer must be
    // false for anything up to that, or the roster would blank on a single slow cycle.
    // TODO(human): choose the grace period. Too short flickers on slow polls; too long keeps showing a match
    // that may already be over. Return true once a snapshot of this age must be discarded.
    internal static bool IsExpired(TimeSpan age) => age > TimeSpan.FromSeconds(30);

    public void ReplaceRoster(LiveRosterMatch match, string self, string selfName, IReadOnlyList<Agent> agents)
    {
        Reset();
        values["match_id"] = match.MatchId; values["map"] = match.MapId; values["game_mode"] = match.ModeId;
        var team = match.Players.FirstOrDefault(p => p.Subject.Equals(self, StringComparison.OrdinalIgnoreCase))?.TeamId;
        var slot = 0;
        foreach (var p in match.Players.OrderBy(p => p.Subject, StringComparer.OrdinalIgnoreCase))
        {
            var isSelf = p.Subject.Equals(self, StringComparison.OrdinalIgnoreCase);
            var agent = agents.FirstOrDefault(a => a.Id.Equals(p.AgentId, StringComparison.OrdinalIgnoreCase))?.Name ?? "不明";
            var player = new JsonObject { ["player_id"] = p.Subject, ["name"] = isSelf ? selfName : p.Incognito ? "非表示" : $"{agent}（名前未取得）", ["character"] = agent, ["is_local"] = isSelf };
            if (!string.IsNullOrEmpty(team) && !string.IsNullOrEmpty(p.TeamId)) player["teammate"] = p.TeamId.Equals(team, StringComparison.OrdinalIgnoreCase);
            if (p.AccountLevel is int level) player["account_level"] = level;
            players[(slot++).ToString()] = player;
        }
    }

    public void Apply(string message)
    {
        if (message.Length > 262144) throw new InvalidDataException("Live event is too large.");
        using var doc = JsonDocument.Parse(message, new JsonDocumentOptions { MaxDepth = 32 });
        var root = doc.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return;
        if (root.TryGetProperty("events", out var events) && events.ValueKind == JsonValueKind.Array)
            foreach (var item in events.EnumerateArray())
                if (item.ValueKind == JsonValueKind.Object && item.TryGetProperty("name", out var eventName) && eventName.ValueKind == JsonValueKind.String)
                {
                    if (eventName.GetString() == "match_start") { if (MatchEnded) players.Clear(); MatchEnded = false; }
                    else if (eventName.GetString() == "match_end") MatchEnded = true;
                }
        if (root.TryGetProperty("key", out var key) && key.ValueKind == JsonValueKind.String &&
            (root.TryGetProperty("data", out var data) || root.TryGetProperty("value", out data)))
            ApplyValue(key.GetString()!, data);
        if (root.TryGetProperty("info", out var info) && info.ValueKind == JsonValueKind.Object)
            foreach (var category in info.EnumerateObject())
                if (category.Value.ValueKind == JsonValueKind.Object)
                    foreach (var field in category.Value.EnumerateObject()) ApplyValue(field.Name, field.Value);
    }

    private void ApplyValue(string key, JsonElement value)
    {
        if (key.StartsWith("scoreboard_", StringComparison.OrdinalIgnoreCase) || key.StartsWith("roster_", StringComparison.OrdinalIgnoreCase))
        {
            var separator = key.IndexOf('_');
            var slot = key[(separator + 1)..];
            if (!int.TryParse(slot, out var n) || n < 0 || n > 99) return;
            JsonNode? node;
            try { node = JsonNode.Parse(value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.GetRawText()); }
            catch (JsonException) { return; }
            if (node is not JsonObject incoming) return;
            var id = Text(incoming, "player_id");
            if (!players.TryGetValue(slot, out var existing)) players[slot] = existing = new JsonObject();
            else if (id.Length > 0 && Text(existing, "player_id") is { Length: > 0 } old && !id.Equals(old, StringComparison.OrdinalIgnoreCase))
                players[slot] = existing = new JsonObject();
            foreach (var property in incoming.ToArray()) existing[property.Key] = property.Value?.DeepClone();
            return;
        }
        if (key is "map" or "game_mode" or "round_number" or "round_phase" or "score" or "match_score" or "match_id" or "match_outcome" or "team")
        {
            var text = value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : value.GetRawText();
            if (key == "match_id" && text.Length > 0 && values.TryGetValue(key, out var previous) && previous.Length > 0 && previous != text)
            { players.Clear(); values.Clear(); MatchEnded = false; }
            values[key] = text.Length > 2048 ? text[..2048] : text;
        }
    }

    public static string Text(JsonObject value, string key)
    {
        if (!value.TryGetPropertyValue(key, out var node) || node is null) return "";
        return node is JsonValue item && item.TryGetValue<string>(out var text) ? text : node.ToJsonString();
    }
}
