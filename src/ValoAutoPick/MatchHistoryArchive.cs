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
        return File.Exists(path) && Guid.TryParse(File.ReadAllText(path).Trim(), out var id)
            ? id.ToString("D") : "";
    }

    public SavedMatchHistory Load(string subject, CancellationToken token)
    {
        var account = AccountFolder(subject);
        var matches = new List<MatchAnalytics>();
        var favorites = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var weapons = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var unreadableCount = 0;
        var weaponPath = Path.Combine(account, "weapons.json");
        if (File.Exists(weaponPath))
        {
            try
            {
                var saved = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllBytes(weaponPath));
                if (saved is not null)
                    foreach (var entry in saved) weapons[entry.Key] = entry.Value;
            }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException)
            {
                unreadableCount++;
            }
        }

        var matchFolder = Path.Combine(account, "matches");
        if (Directory.Exists(matchFolder))
        {
            foreach (var path in Directory.EnumerateFiles(matchFolder, "*.json"))
            {
                token.ThrowIfCancellationRequested();
                try
                {
                    var match = JsonSerializer.Deserialize<MatchDetailsData>(File.ReadAllBytes(path));
                    if (match?.Info is null || string.IsNullOrWhiteSpace(match.Info.MatchId) ||
                        !Path.GetFileNameWithoutExtension(path).Equals(MatchKey(match.Info.MatchId), StringComparison.OrdinalIgnoreCase))
                    {
                        unreadableCount++;
                        continue;
                    }
                    matches.Add(MatchAnalyticsEngine.Analyze(match));
                    if (File.Exists(Path.ChangeExtension(path, ".favorite"))) favorites.Add(match.Info.MatchId);
                }
                catch (Exception ex) when (ex is IOException or UnauthorizedAccessException or JsonException or ArgumentException or InvalidOperationException or NullReferenceException)
                {
                    unreadableCount++;
                }
            }
        }
        return new SavedMatchHistory(matches, favorites, weapons, unreadableCount);
    }

    public void Save(string subject, IEnumerable<MatchAnalytics> matches, IReadOnlyDictionary<string, string> weapons, CancellationToken token)
    {
        var account = AccountFolder(subject);
        foreach (var match in matches)
        {
            token.ThrowIfCancellationRequested();
            SaveMatch(account, match.Match);
        }
        if (weapons.Count > 0) WriteAtomic(Path.Combine(account, "weapons.json"), JsonSerializer.SerializeToUtf8Bytes(weapons));
        token.ThrowIfCancellationRequested();
        WriteAtomic(Path.Combine(root, "last-account.txt"), Encoding.UTF8.GetBytes(Guid.Parse(subject).ToString("D")));
    }

    public void SetFavorite(string subject, MatchDetailsData match, bool favorite)
    {
        var account = AccountFolder(subject);
        var path = MatchPath(account, match.Info.MatchId);
        var marker = Path.ChangeExtension(path, ".favorite");
        if (!favorite)
        {
            File.Delete(marker);
            return;
        }
        if (!File.Exists(path)) SaveMatch(account, match);
        WriteAtomic(marker, []);
    }

    private string AccountFolder(string subject) => Path.Combine(root, Guid.Parse(subject).ToString("D"));
    private static string MatchPath(string account, string matchId) => Path.Combine(account, "matches", MatchKey(matchId) + ".json");
    private static string MatchKey(string matchId) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(matchId.ToUpperInvariant())));
    private static void SaveMatch(string account, MatchDetailsData match) =>
        WriteAtomic(MatchPath(account, match.Info.MatchId), JsonSerializer.SerializeToUtf8Bytes(match));

    private static void WriteAtomic(string path, byte[] bytes)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var temporary = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, bytes);
            File.Move(temporary, path, overwrite: true);
        }
        finally
        {
            try { File.Delete(temporary); }
            catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { }
        }
    }
}
