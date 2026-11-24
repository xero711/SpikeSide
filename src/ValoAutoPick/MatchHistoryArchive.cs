using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace ValoAutoPick;

internal sealed record SavedMatchHistory(
    IReadOnlyList<MatchAnalytics> Matches,
    HashSet<string> Favorites,
    IReadOnlyDictionary<string, string> Weapons,
    int UnreadableCount);

internal sealed class MatchHistoryArchive(string root)
{
    public string LastSubject()
    {
        var path = Path.Combine(root, "last-account.txt");
        try { return File.Exists(path) && Guid.TryParse(File.ReadAllText(path).Trim(), out var id) ? id.ToString("D") : ""; }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { return ""; }
    }

    public SavedMatchHistory Load(string subject, CancellationToken token)
    {
        var account = AccountFolder(subject);
        var matches = new List<MatchAnalytics>();
        var favorites = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var weapons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var unreadableCount = 0;
        var weaponPath = Path.Combine(account, "weapons.json");
        if (!LoadWeapons(weaponPath, weapons)) unreadableCount++;

        var matchFolder = Path.Combine(account, "matches");
        if (Directory.Exists(matchFolder))
        {
            foreach (var path in Directory.EnumerateFiles(matchFolder, "*.json"))
            {
                token.ThrowIfCancellationRequested();
                if (!LoadMatch(path, matches, favorites)) unreadableCount++;
            }
        }
        return new SavedMatchHistory(matches, favorites, weapons, unreadableCount);
    }

    private static bool LoadWeapons(string path, Dictionary<string, string> weapons)
    {
        if (!File.Exists(path)) return true;
        try
        {
            var saved = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllBytes(path));
            if (saved is not null)
                foreach (var entry in saved) if (entry.Value is not null) weapons[entry.Key] = entry.Value;
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException) { return false; }
    }

    private static bool LoadMatch(string path, List<MatchAnalytics> matches, HashSet<string> favorites)
    {
        try
        {
            var match = JsonSerializer.Deserialize<MatchDetailsData>(File.ReadAllBytes(path));
            if (match?.Info is null || string.IsNullOrWhiteSpace(match.Info.MatchId) || match.Players is null || match.Rounds is null || match.Teams is null ||
                !Path.GetFileNameWithoutExtension(path).Equals(MatchKey(match.Info.MatchId), StringComparison.OrdinalIgnoreCase)) return false;
            matches.Add(MatchAnalyticsEngine.Analyze(match));
            if (File.Exists(Path.ChangeExtension(path, ".favorite"))) favorites.Add(match.Info.MatchId);
            return true;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or NullReferenceException) { return false; }
    }

    public void Save(string subject, IEnumerable<MatchAnalytics> matches, IReadOnlyDictionary<string, string> weapons, CancellationToken token)
    {
        var account = AccountFolder(subject);
        foreach (var match in matches)
        {
            token.ThrowIfCancellationRequested();
            SaveMatch(account, match.Match, token);
        }
        if (weapons.Count > 0) WriteAtomic(Path.Combine(account, "weapons.json"), JsonSerializer.SerializeToUtf8Bytes(weapons), token);
        token.ThrowIfCancellationRequested();
        WriteAtomic(Path.Combine(root, "last-account.txt"), Encoding.UTF8.GetBytes(Guid.Parse(subject).ToString("D")), token);
    }

    public void SetFavorite(string subject, MatchDetailsData match, bool favorite, CancellationToken token = default)
    {
        token.ThrowIfCancellationRequested();
        var account = AccountFolder(subject);
        var path = MatchPath(account, match.Info.MatchId);
        var marker = Path.ChangeExtension(path, ".favorite");
        if (!favorite)
        {
            if (File.Exists(marker)) File.Delete(marker);
            return;
        }
        if (!File.Exists(path)) SaveMatch(account, match, token);
        WriteAtomic(marker, [], token);
    }

    private string AccountFolder(string subject) => Path.Combine(root, Guid.Parse(subject).ToString("D"));
    private static string MatchPath(string account, string matchId) => Path.Combine(account, "matches", MatchKey(matchId) + ".json");
    private static string MatchKey(string matchId) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(matchId.ToUpperInvariant())));
    private static void SaveMatch(string account, MatchDetailsData match, CancellationToken token) =>
        WriteAtomic(MatchPath(account, match.Info.MatchId), JsonSerializer.SerializeToUtf8Bytes(match), token);

    private static void WriteAtomic(string path, byte[] bytes, CancellationToken token)
    {
        token.ThrowIfCancellationRequested();
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, bytes);
            token.ThrowIfCancellationRequested();
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
            {
                // The temporary file can be cleaned up later; retain the original save result.
            }
        }
    }
}
