using System.Globalization;
using System.Text;

namespace ValoAutoPick;

public sealed record PickDiagnosticEvent(
    string MatchId,
    string MapKey,
    string Outcome,
    long? DetectToReadyMs,
    long? SelectMs,
    long? LockMs,
    long TotalMs,
    int Attempts,
    string Note = "");

internal sealed class PickDiagnosticsLogger
{
    private const long MaxBytes = 256 * 1024;
    private readonly string path;
    private readonly object gate = new();

    internal PickDiagnosticsLogger(string? logPath = null)
    {
        path = logPath ?? Path.Combine(Storage.Folder, "pick-diagnostics.log");
    }

    internal void Record(PickDiagnosticEvent entry)
    {
        try
        {
            var shortMatch = entry.MatchId.Length > 8 ? entry.MatchId[..8] : entry.MatchId;
            var line = string.Format(
                CultureInfo.InvariantCulture,
                "{0:O} match={1} map={2} outcome={3} detectToReadyMs={4} selectMs={5} lockMs={6} totalMs={7} attempts={8} note={9}{10}",
                DateTimeOffset.UtcNow,
                Sanitize(shortMatch),
                Sanitize(entry.MapKey),
                Sanitize(entry.Outcome),
                entry.DetectToReadyMs?.ToString(CultureInfo.InvariantCulture) ?? "-",
                entry.SelectMs?.ToString(CultureInfo.InvariantCulture) ?? "-",
                entry.LockMs?.ToString(CultureInfo.InvariantCulture) ?? "-",
                entry.TotalMs,
                entry.Attempts,
                Sanitize(entry.Note),
                Environment.NewLine);
            lock (gate)
            {
                var directory = Path.GetDirectoryName(path);
                if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
                if (File.Exists(path) && new FileInfo(path).Length >= MaxBytes)
                {
                    var archive = path + ".old";
                    File.Move(path, archive, overwrite: true);
                }
                File.AppendAllText(path, line, Encoding.UTF8);
            }
        }
        catch (Exception e) when (e is IOException or UnauthorizedAccessException or ArgumentException or NotSupportedException)
        {
            // Diagnostics must never interfere with picking.
        }
    }

    private static string Sanitize(string value)
    {
        if (string.IsNullOrWhiteSpace(value)) return "-";
        return value.Replace('\r', ' ').Replace('\n', ' ').Trim();
    }
}
