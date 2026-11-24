using System.Text.Json;

namespace ValoAutoPick;

/// <summary>
/// Validates, merges and caches the agent catalogue. The embedded list that ships with the
/// app is the trusted base; the on-disk cache and the remote catalogue are overlays that may
/// add agents or refresh display text, but can never remove an agent or grant base access.
/// Has no UI dependencies so it can be exercised offline by the test project.
/// </summary>
public static class AgentCatalog
{
    /// <summary>A remote response with fewer valid agents than this is treated as truncated/invalid.</summary>
    public const int MinimumRemoteAgents = 10;
    public const int MaximumAgents = 256;
    public const int MaximumRemoteBytes = 8 * 1024 * 1024;
    public const int MaximumCacheBytes = 1024 * 1024;
    private const int MaximumTextLength = 64;

    private static readonly JsonSerializerOptions CacheJson = new() { WriteIndented = true };

    /// <summary>Returns a normalized copy of <paramref name="agent"/>, or null when it is unusable.</summary>
    public static Agent? Normalize(Agent? agent, bool trustBaseContent = false)
    {
        if (agent == null || !Guid.TryParse(agent.Id, out var id)) return null;
        var name = CleanText(agent.Name);
        if (name.Length == 0) return null;
        var roleIcon = Guid.TryParse(agent.RoleIconId, out var icon) ? icon.ToString("D") : "";
        return new Agent(id.ToString("D"), name, CleanText(agent.Role), roleIcon, trustBaseContent && agent.IsBaseContent);
    }

    private static string CleanText(string? value)
    {
        var text = (value ?? "").Trim();
        return text.Length is > 0 and <= MaximumTextLength && !text.Any(char.IsControl) ? text : "";
    }

    /// <summary>
    /// Merges catalogues by agent ID (case-insensitive). Order follows first appearance, so the
    /// trusted list keeps its order and newly discovered agents are appended. Later overlays win
    /// for non-empty display fields. <see cref="Agent.IsBaseContent"/> is taken only from
    /// <paramref name="trusted"/>, so cached or remote data can never invent account access.
    /// </summary>
    public static List<Agent> Merge(IEnumerable<Agent?> trusted, params IEnumerable<Agent?>?[] overlays)
    {
        var order = new List<string>();
        var byId = new Dictionary<string, Agent>(StringComparer.OrdinalIgnoreCase);
        var baseIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var agent in trusted) AddMergedAgent(agent, true, order, byId, baseIds);
        foreach (var overlay in overlays.OfType<IEnumerable<Agent?>>())
            foreach (var agent in overlay) AddMergedAgent(agent, false, order, byId, baseIds);
        return order.Select(id => byId[id] with { IsBaseContent = baseIds.Contains(id) }).ToList();
    }

    private static void AddMergedAgent(Agent? candidate, bool trusted, List<string> order,
        Dictionary<string, Agent> byId, HashSet<string> baseIds)
    {
        var agent = Normalize(candidate, trusted);
        if (agent == null) return;
        if (agent.IsBaseContent) baseIds.Add(agent.Id);
        if (byId.TryGetValue(agent.Id, out var existing))
        {
            byId[agent.Id] = existing with
            {
                Name = agent.Name,
                Role = agent.Role.Length > 0 ? agent.Role : existing.Role,
                RoleIconId = agent.RoleIconId.Length > 0 ? agent.RoleIconId : existing.RoleIconId
            };
        }
        else if (order.Count < MaximumAgents)
        {
            order.Add(agent.Id);
            byId[agent.Id] = agent;
        }
    }

    /// <summary>
    /// Parses the app's own catalogue format (embedded resource or cache file). Invalid entries
    /// are skipped individually; a non-array document is rejected. Base-content flags are only
    /// kept when <paramref name="trustBaseContent"/> is set (the embedded resource).
    /// </summary>
    public static List<Agent> ParseCatalogue(string json, bool trustBaseContent)
    {
        using var document = JsonDocument.Parse(json);
        if (document.RootElement.ValueKind != JsonValueKind.Array) throw new InvalidDataException("エージェント一覧の形式が正しくありません");
        var result = new List<Agent>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in document.RootElement.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Object) continue;
            var agent = Normalize(new Agent(Text(item, "Id"), Text(item, "Name"), Text(item, "Role"), Text(item, "RoleIconId"),
                Property(item, "IsBaseContent").ValueKind == JsonValueKind.True), trustBaseContent);
            if (agent == null || !seen.Add(agent.Id)) continue;
            result.Add(agent);
            if (result.Count > MaximumAgents) throw new InvalidDataException("エージェント一覧が大きすぎます");
        }
        return result;
    }

    /// <summary>
    /// Parses a valorant-api.com <c>/v1/agents</c> response. Throws <see cref="InvalidDataException"/>
    /// when the payload is not a successful response or holds too few valid playable agents.
    /// The remote <c>isBaseContent</c> flag is deliberately ignored.
    /// </summary>
    public static List<Agent> ParseRemote(string json)
    {
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) throw new InvalidDataException("エージェント一覧の応答形式が正しくありません");
        var status = Property(root, "status");
        if (status.ValueKind != JsonValueKind.Undefined && (status.ValueKind != JsonValueKind.Number || !status.TryGetInt32(out var code) || code != 200))
            throw new InvalidDataException("エージェント一覧を取得できませんでした");
        var data = Property(root, "data");
        if (data.ValueKind != JsonValueKind.Array) throw new InvalidDataException("エージェント一覧の応答形式が正しくありません");
        var result = new List<Agent>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var item in data.EnumerateArray())
        {
            var agent = ParseRemoteAgent(item);
            if (agent == null || !seen.Add(agent.Id)) continue;
            result.Add(agent);
            if (result.Count > MaximumAgents) throw new InvalidDataException("エージェント一覧が大きすぎます");
        }
        if (result.Count < MinimumRemoteAgents) throw new InvalidDataException("エージェント一覧が不完全です");
        return result;
    }

    private static Agent? ParseRemoteAgent(JsonElement item)
    {
        // The catalogue includes non-playable duplicates; accept only explicitly playable agents.
        if (item.ValueKind != JsonValueKind.Object || Property(item, "isPlayableCharacter").ValueKind != JsonValueKind.True) return null;
        var role = Property(item, "role");
        return Normalize(new Agent(Text(item, "uuid"), Text(item, "displayName"),
            role.ValueKind == JsonValueKind.Object ? Text(role, "displayName") : "",
            role.ValueKind == JsonValueKind.Object ? Text(role, "uuid") : ""));
    }

    /// <summary>Reads the cache file; returns null when it does not exist.</summary>
    public static List<Agent>? ReadCache(string path)
    {
        var info = new FileInfo(path);
        if (!info.Exists) return null;
        if (info.Length > MaximumCacheBytes) throw new InvalidDataException("エージェント一覧のキャッシュが大きすぎます");
        return ParseCatalogue(File.ReadAllText(path), trustBaseContent: false);
    }

    /// <summary>
    /// Writes the cache via a uniquely named temporary file in the same folder followed by an
    /// atomic replace, so readers see either the old or the new complete file.
    /// </summary>
    public static void WriteCacheAtomic(string path, IReadOnlyList<Agent> agents)
    {
        var fullPath = Path.GetFullPath(path);
        var folder = Path.GetDirectoryName(fullPath) ?? throw new IOException("キャッシュの保存先が正しくありません");
        Directory.CreateDirectory(folder);
        var temporary = Path.Combine(folder, $".{Path.GetFileName(fullPath)}.{Guid.NewGuid():N}.tmp");
        // Base access is never persisted; it always comes from the embedded catalogue.
        var content = agents.Select(agent => Normalize(agent)).OfType<Agent>().ToList();
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            {
                JsonSerializer.Serialize(stream, content, CacheJson);
                stream.Flush(flushToDisk: true);
            }
            File.Move(temporary, fullPath, overwrite: true);
        }
        finally
        {
            try { if (File.Exists(temporary)) File.Delete(temporary); }
            catch (Exception e) when (e is IOException or UnauthorizedAccessException)
            {
                // Cleanup is best-effort; preserve the original save result.
            }
        }
    }

    /// <summary>
    /// Merges a freshly fetched remote catalogue into the cache file without dropping agents
    /// that were cached before (whether known in memory or present on disk). An unreadable cache
    /// file is replaced. The file is rewritten only when its content would change.
    /// </summary>
    public static List<Agent> UpdateCacheFile(string path, IEnumerable<Agent> knownCache, IReadOnlyList<Agent> remote)
    {
        List<Agent>? onDisk = null;
        try { onDisk = ReadCache(path); }
        catch (Exception e) when (e is JsonException or InvalidDataException)
        {
            // A damaged cache is replaced by the merged catalogue below.
        }
        var merged = Merge([], knownCache, onDisk, remote);
        if (onDisk == null || !merged.SequenceEqual(onDisk)) WriteCacheAtomic(path, merged);
        return merged;
    }

    /// <summary>
    /// Maps a current selection onto a refreshed catalogue by ID. The selection is never cleared
    /// or changed to another agent; when the ID is missing the original record is kept.
    /// </summary>
    public static Agent? ResolveSelection(IEnumerable<Agent> catalogue, Agent? selection) =>
        selection == null ? null : catalogue.FirstOrDefault(agent => agent.Id.Equals(selection.Id, StringComparison.OrdinalIgnoreCase)) ?? selection;

    /// <summary>True when every agent ID in <paramref name="previous"/> is still present in <paramref name="updated"/>.</summary>
    public static bool ContainsAll(IEnumerable<Agent> updated, IEnumerable<Agent> previous)
    {
        var ids = new HashSet<string>(updated.Select(agent => agent.Id), StringComparer.OrdinalIgnoreCase);
        return previous.All(agent => ids.Contains(agent.Id));
    }

    private static JsonElement Property(JsonElement element, string name)
    {
        if (element.ValueKind != JsonValueKind.Object) return default;
        if (element.TryGetProperty(name, out var exact)) return exact;
        foreach (var property in element.EnumerateObject())
            if (property.Name.Equals(name, StringComparison.OrdinalIgnoreCase)) return property.Value;
        return default;
    }

    private static string Text(JsonElement element, string name)
    {
        var value = Property(element, name);
        return value.ValueKind == JsonValueKind.String ? value.GetString() ?? "" : "";
    }
}
