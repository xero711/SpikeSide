using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Win32;

namespace ValoAutoPick;

// The old v1 file also acts as a cross-process lock and is migrated in place.
// Protected replicas prevent a single missing/older copy
// from resetting usage. This raises the bar; a local administrator can still
// remove all state or patch an offline application.
public sealed class TrialUsage
{
    public const int Limit = 3;
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("QuickPickerPro.TrialUsage.v2");
    private readonly string legacyPath;
    private readonly ITrialStore[] stores;
    private readonly bool allowReadOnlyMachine;
    private readonly string fallbackLockPath;

    public static string DefaultPath => Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "QuickPickerPro", "trial-v1.json");

    // Isolated stores allow tests to exercise the real reconciliation code
    // without modifying the user's actual trial allowance or registry.
    public TrialUsage(string path) : this(path, [new FileStore(path + ".machine"), new FileStore(path + ".user"), new FileStore(path + ".registry")], false, path + ".lock") { }
    internal TrialUsage(string path, bool readOnlyMachineFallback) : this(path,
        [new FileStore(path + ".machine"), new FileStore(path + ".user"), new FileStore(path + ".registry")], readOnlyMachineFallback, path + ".lock") { }
    private TrialUsage(string path, ITrialStore[] stores, bool allowReadOnlyMachine, string fallbackLockPath)
    { legacyPath = path; this.stores = stores; this.allowReadOnlyMachine = allowReadOnlyMachine; this.fallbackLockPath = fallbackLockPath; }
    public static TrialUsage CreateDefault() => new(DefaultPath,
    [
        new FileStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.CommonApplicationData), "QuickPickerPro", "trial-v2.dat")),
        new FileStore(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuickPickerPro", "trial-v2.dat")),
        new RegistryStore()
    ], true, Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "QuickPickerPro", "trial.lock"));

    public int Remaining() => WithLockedHistory(null, false).remaining;

    public bool Reserve(string matchId)
    {
        if (string.IsNullOrWhiteSpace(matchId)) throw new ArgumentException("Missing match ID", nameof(matchId));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(matchId.ToLowerInvariant())));
        return WithLockedHistory(hash, true).allowed;
    }
    public bool CanUse(string matchId)
    {
        if (string.IsNullOrWhiteSpace(matchId)) throw new ArgumentException("Missing match ID", nameof(matchId));
        var hash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(matchId.ToLowerInvariant())));
        return WithLockedHistory(hash, false).allowed;
    }

    private (bool allowed, int remaining) WithLockedHistory(string? matchHash, bool reserve)
    {
        FileStream? legacy = null;
        FileStream? fallbackLock = null;
        var created = false;
        try
        {
            Directory.CreateDirectory(Path.GetDirectoryName(legacyPath)!);
            try { legacy = new FileStream(legacyPath, FileMode.CreateNew, FileAccess.ReadWrite, FileShare.None); created = true; }
            catch (IOException) when (File.Exists(legacyPath))
            { legacy = new FileStream(legacyPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
        }
        catch (UnauthorizedAccessException) when (allowReadOnlyMachine)
        {
            // An administrator may have created the machine record. Keep its exclusive
            // lock and reconcile its contents even if this user cannot update it.
            if (File.Exists(legacyPath)) legacy = new FileStream(legacyPath, FileMode.Open, FileAccess.Read, FileShare.None);
            else
            {
                Directory.CreateDirectory(Path.GetDirectoryName(fallbackLockPath)!);
                fallbackLock = new FileStream(fallbackLockPath, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None);
            }
        }
        using (fallbackLock)
        using (legacy)
        {
            if (created) WriteLegacy(legacy!, []);
            var legacyData = legacy is null ? Encoding.UTF8.GetBytes("[]") : ReadBytes(legacy);
            var plaintextLegacy = legacyData.SkipWhile(x => x is 9 or 10 or 13 or 32).FirstOrDefault() == (byte)'[';
            var original = Parse(plaintextLegacy ? legacyData : ProtectedData.Unprotect(legacyData, Entropy, DataProtectionScope.LocalMachine));
            var copies = new List<string>?[stores.Length];
            var union = new HashSet<string>(original, StringComparer.Ordinal);
            for (var i = 0; i < stores.Length; i++)
            {
                var data = stores[i].Read();
                if (data == null) continue;
                if (data.Length > 4096) throw new InvalidDataException("Invalid trial record");
                copies[i] = Parse(ProtectedData.Unprotect(data, Entropy, DataProtectionScope.LocalMachine));
                union.UnionWith(copies[i]!);
            }
            if (union.Count > Limit) throw new InvalidDataException("Conflicting trial records");
            var allowed = matchHash == null || union.Contains(matchHash) || union.Count < Limit;
            if (reserve && matchHash != null && allowed) union.Add(matchHash);
            var result = union.Order(StringComparer.Ordinal).ToList();

            // Commit before permitting a game request. Partial writes are merged on
            // the next run; a damaged or inaccessible copy fails closed.
            if (legacy?.CanWrite == true && (plaintextLegacy || !original.ToHashSet(StringComparer.Ordinal).SetEquals(result))) WriteLegacy(legacy, result);
            byte[]? protectedData = null;
            for (var i = 0; i < stores.Length; i++)
            {
                if (copies[i] != null && copies[i]!.ToHashSet(StringComparer.Ordinal).SetEquals(result)) continue;
                protectedData ??= ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(result), Entropy, DataProtectionScope.LocalMachine);
                try { stores[i].Write(protectedData); }
                catch (UnauthorizedAccessException) when (allowReadOnlyMachine && i == 0)
                { /* Keep the readable machine replica; the user and registry replicas remain mandatory. */ }
            }
            return (allowed, Limit - result.Count);
        }
    }

    private static byte[] ReadBytes(FileStream file)
    {
        if (file.Length is <= 0 or > 1024) throw new InvalidDataException("Invalid trial record");
        file.Position = 0;
        using var memory = new MemoryStream(); file.CopyTo(memory); return memory.ToArray();
    }
    private static List<string> Parse(byte[] data)
    {
        var result = JsonSerializer.Deserialize<List<string>>(data) ?? throw new InvalidDataException("Invalid trial record");
        if (result.Count > Limit || result.Any(x => x == null || x.Length != 64 || !x.All(Uri.IsHexDigit)) || result.Distinct(StringComparer.Ordinal).Count() != result.Count)
            throw new InvalidDataException("Invalid trial record");
        return result;
    }
    private static void WriteLegacy(FileStream file, List<string> matches)
    {
        var bytes = ProtectedData.Protect(JsonSerializer.SerializeToUtf8Bytes(matches), Entropy, DataProtectionScope.LocalMachine);
        file.Position = 0; file.Write(bytes); file.SetLength(bytes.Length); file.Flush(true);
    }

    private interface ITrialStore { byte[]? Read(); void Write(byte[] data); }
    private sealed class FileStore(string path) : ITrialStore
    {
        public byte[]? Read()
        {
            try { return File.ReadAllBytes(path); }
            catch (FileNotFoundException) { return null; }
            catch (DirectoryNotFoundException) { return null; }
        }
        public void Write(byte[] data)
        {
            Directory.CreateDirectory(Path.GetDirectoryName(path)!);
            var temp = path + "." + Guid.NewGuid().ToString("N") + ".tmp";
            try
            {
                using (var file = new FileStream(temp, FileMode.CreateNew, FileAccess.Write, FileShare.None))
                { file.Write(data); file.Flush(true); }
                File.Move(temp, path, true);
            }
            finally { try { File.Delete(temp); } catch (Exception ex) when (ex is IOException or UnauthorizedAccessException) { } }
        }
    }
    private sealed class RegistryStore : ITrialStore
    {
        private const string KeyPath = @"Software\QuickPickerPro\Trial";
        public byte[]? Read()
        {
            using var key = Registry.CurrentUser.OpenSubKey(KeyPath);
            var value = key?.GetValue("StateV2");
            return value == null ? null : value as byte[] ?? throw new InvalidDataException("Invalid trial record");
        }
        public void Write(byte[] data)
        {
            using var key = Registry.CurrentUser.CreateSubKey(KeyPath, writable: true) ?? throw new IOException("Cannot open trial registry key");
            key.SetValue("StateV2", data, RegistryValueKind.Binary);
            key.Flush();
        }
    }
}
