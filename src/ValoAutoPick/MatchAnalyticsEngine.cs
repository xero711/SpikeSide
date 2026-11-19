using System.Globalization;
using System.Text.Json;

namespace ValoAutoPick;

// =====================================================================================
// Post-match analytics for GET /match-details/v1/matches/{matchId}.
//
// Layers (all pure, no I/O):
//   1. Immutable "Data" records that mirror the parts of the response we use.
//   2. MatchDetailsParser   - defensive JSON -> records (camelCase first, PascalCase / other
//                             casing tolerated, missing optional structures tolerated).
//   3. MatchAnalyticsEngine - per-match analytics and multi-match history aggregation.
//
// Conventions:
//   - Subjects (PUUIDs), team ids, map ids and queue ids are compared case-insensitively.
//   - Agent ids and GUID-shaped damage items are normalised to lower-case "D" GUID format.
//   - Values that cannot be derived from the payload are null; "0" always means a measured zero.
//   - Every "...Percent" value is on a 0-100 scale. Nothing is rounded; presentation rounds.
//   - Attack/defense sides are never inferred. WinningTeamRole is only surfaced verbatim
//     when the payload explicitly contains it.
// =====================================================================================

#region Parsed payload records

/// <summary>matchInfo block. Empty strings mean "not present in payload".</summary>
public sealed record MatchInfoData(
    string MatchId,
    string MapId,
    string QueueId,
    long? GameStartMillis,
    long? GameLengthMillis,
    string CompletionState)
{
    /// <summary>Format written by the current parser. Archives saved before isCustomGame was kept deserialize as 0.</summary>
    public const int CurrentFormatVersion = 1;

    /// <summary>
    /// Explicit custom-game indicator (matchInfo.isCustomGame, or provisioningFlowID "CustomGame" when
    /// the flag is absent). Null when the payload did not report it or the record predates this field.
    /// Riot sends an empty queueID for custom games, so QueueId alone cannot identify them.
    /// </summary>
    public bool? IsCustomGame { get; init; }

    /// <summary>
    /// 0 = archived before <see cref="IsCustomGame"/> existed; lets legacy archives be told apart
    /// from new payloads that simply omitted the flag.
    /// </summary>
    public int FormatVersion { get; init; }

    private const long MinUnixMillis = -62_135_596_800_000L;
    private const long MaxUnixMillis = 253_402_300_799_999L;

    public DateTimeOffset? GameStartUtc =>
        GameStartMillis is { } ms && ms >= MinUnixMillis && ms <= MaxUnixMillis
            ? DateTimeOffset.FromUnixTimeMilliseconds(ms)
            : null;

    public TimeSpan? GameLength =>
        GameLengthMillis is { } ms && ms >= 0 ? TimeSpan.FromMilliseconds(ms) : null;
}

public sealed record MatchTeamData(string TeamId, bool? Won, int? RoundsWon);

public sealed record MatchPlayerStatsData(int? Score, int? Kills, int? Deaths, int? Assists, int? RoundsPlayed);

public sealed record MatchPlayerData(
    string Subject,
    string GameName,
    string TagLine,
    string TeamId,
    string CharacterId,
    MatchPlayerStatsData? Stats)
{
    public int? CompetitiveTier { get; init; }
    public string DisplayName =>
        GameName.Length == 0 ? "" : TagLine.Length == 0 ? GameName : $"{GameName}#{TagLine}";
}

/// <summary>
/// A single kill. Killer may be empty for environmental / spike deaths.
/// DamageItem is empty when finishingDamage.damageItem was not reported.
/// </summary>
public sealed record RoundKillData(
    string Killer,
    string Victim,
    long? TimeSinceRoundStartMillis,
    string DamageType,
    string DamageItem)
{
    public long? TimeSinceGameStartMillis { get; init; }
}

/// <summary>Damage dealt by <see cref="Dealer"/> (the owning playerStats subject) to <see cref="Receiver"/>.</summary>
public sealed record RoundDamageData(
    string Dealer,
    string Receiver,
    int Damage,
    int Headshots,
    int Bodyshots,
    int Legshots);

/// <summary>
/// One entry of roundResults[]. Kills are merged from playerStats[].kills[] (and a top-level
/// kills[] if present), de-duplicated, in payload order. HasDamageData is true only when at
/// least one playerStats entry carried a damage array, so "no damage" can be told apart from
/// "damage not reported".
/// </summary>
public sealed record RoundResultData(
    int? RoundNum,
    string RoundResult,
    string WinningTeam,
    string WinningTeamRole,
    IReadOnlyList<string> ParticipantSubjects,
    IReadOnlyList<RoundKillData> Kills,
    IReadOnlyList<RoundDamageData> Damage,
    bool HasDamageData);

public sealed record MatchDetailsData(
    MatchInfoData Info,
    IReadOnlyList<MatchPlayerData> Players,
    IReadOnlyList<MatchTeamData> Teams,
    IReadOnlyList<RoundResultData> Rounds,
    IReadOnlyList<string> Warnings);

public sealed record MatchParseResult(MatchDetailsData? Match, string? Error)
{
    public bool Success => Match is not null;
}

#endregion

#region Analytics records

public enum MatchOutcome
{
    Unknown = 0,
    Win,
    Loss,
    Draw,
}

/// <summary>
/// Team result. RoundsWon comes from teams[]; RoundsWonInRoundResults is counted independently
/// from roundResults[].winningTeam so callers can see which source they are using.
/// RoundsLost is only filled for two-team matches with the opponent's roundsWon known.
/// </summary>
public sealed record TeamResult(
    string TeamId,
    bool? Won,
    MatchOutcome Outcome,
    int? RoundsWon,
    int? RoundsLost,
    int RoundsWonInRoundResults,
    int PlayerCount);

/// <summary>DamageItem "" means the payload did not report finishingDamage.damageItem.</summary>
public sealed record WeaponKillCount(string DamageItem, int Kills);

public sealed record PlayerMatchStats(
    string Subject,
    string GameName,
    string TagLine,
    string TeamId,
    string CharacterId,
    MatchOutcome Outcome,
    int? Score,
    int? Kills,
    int? Deaths,
    int? Assists,
    int? KillDeathDifference,
    double? KillDeathRatio,
    int? RoundsPlayed,
    bool RoundsPlayedFromRoundResults,
    double? AverageCombatScore,
    int? DamageDealt,
    int? DamageReceived,
    double? AverageDamagePerRound,
    double? DamageDeltaPerRound,
    int? HeadHits,
    int? BodyHits,
    int? LegHits,
    double? HeadshotPercent,
    int? FirstKills,
    int? FirstDeaths,
    IReadOnlyList<WeaponKillCount> WeaponKills)
{
    public int? CompetitiveTier { get; init; }
    public string DisplayName =>
        GameName.Length == 0 ? "" : TagLine.Length == 0 ? GameName : $"{GameName}#{TagLine}";
}

/// <summary>
/// Round summary. RoundNum is the raw payload value (Riot is 0-based); Index is the position
/// after ordering by RoundNum. KillsByTeam is keyed by the killer's team ("" = unknown team).
/// </summary>
public sealed record RoundSummary(
    int Index,
    int? RoundNum,
    string RoundResult,
    string WinningTeam,
    string WinningTeamRole,
    int KillCount,
    string? FirstKiller,
    string? FirstVictim,
    long? FirstKillTimeMillis,
    string? FirstKillDamageItem,
    IReadOnlyDictionary<string, int> KillsByTeam);

public sealed record MatchAnalytics(
    MatchDetailsData Match,
    IReadOnlyList<TeamResult> Teams,
    IReadOnlyList<PlayerMatchStats> Players,
    IReadOnlyList<RoundSummary> Rounds,
    int TotalRoundResults,
    bool HasDamageData,
    bool HasKillTimeline)
{
    public MatchInfoData Info => Match.Info;

    public PlayerMatchStats? FindPlayer(string subject) =>
        string.IsNullOrWhiteSpace(subject)
            ? null
            : Players.FirstOrDefault(p => string.Equals(p.Subject, subject.Trim(), StringComparison.OrdinalIgnoreCase));
}

/// <summary>Win rate over the most recent <see cref="Window"/> matches (known outcomes only).</summary>
public sealed record RecentWinRate(int Window, int MatchesConsidered, int KnownOutcomes, int Wins, double? WinRatePercent);

/// <summary>Map or agent breakdown. PickRatePercent = Matches / total matches * 100.</summary>
public sealed record HistoryBreakdownEntry(
    string Key,
    int Matches,
    double PickRatePercent,
    int Wins,
    int Losses,
    int Draws,
    int KnownOutcomes,
    double? WinRatePercent);

public sealed record PlayerHistoryEntry(string MatchId, string MapId, string QueueId, long? GameStartMillis, PlayerMatchStats Stats);

/// <summary>
/// History for one subject. Averages are the arithmetic mean of per-match values that are
/// available (match-weighted). OverallKillDeathRatio is total kills / total deaths over matches
/// where both are known (deaths 0 -> kills).
/// </summary>
public sealed record PlayerHistorySummary(
    string Subject,
    int MatchCount,
    int Wins,
    int Losses,
    int Draws,
    int UnknownOutcomes,
    double? WinRatePercent,
    RecentWinRate Recent5,
    RecentWinRate Recent10,
    double? AverageCombatScore,
    double? AverageKillDeathRatio,
    double? OverallKillDeathRatio,
    double? AverageDamagePerRound,
    double? AverageHeadshotPercent,
    IReadOnlyList<HistoryBreakdownEntry> Maps,
    IReadOnlyList<HistoryBreakdownEntry> Agents,
    IReadOnlyList<PlayerHistoryEntry> MatchesNewestFirst);

#endregion

#region Parser

public static class MatchDetailsParser
{
    private static readonly JsonDocumentOptions DocumentOptions = new()
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip,
        MaxDepth = 128,
    };

    /// <summary>Parses a raw match-details JSON body. Never throws for bad input.</summary>
    public static MatchParseResult Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
            return new MatchParseResult(null, "Empty match-details payload.");
        try
        {
            using var document = JsonDocument.Parse(json, DocumentOptions);
            return Parse(document.RootElement);
        }
        catch (JsonException ex)
        {
            return new MatchParseResult(null, $"Invalid match-details JSON: {ex.Message}");
        }
    }

    /// <summary>Parses an already-loaded root element. The result does not reference the element.</summary>
    public static MatchParseResult Parse(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object)
            return new MatchParseResult(null, "Match-details root is not a JSON object.");

        var hasInfo = TryGetObject(root, out var infoElement, "matchInfo");
        var hasPlayers = TryGetProperty(root, out var playersElement, "players") && playersElement.ValueKind == JsonValueKind.Array;
        if (!hasInfo && !hasPlayers)
            return new MatchParseResult(null, "Payload has neither matchInfo nor players; not a match-details response.");

        var warnings = new List<string>();
        var info = hasInfo
            ? ParseInfo(infoElement)
            : new MatchInfoData("", "", "", null, null, "") { FormatVersion = MatchInfoData.CurrentFormatVersion };
        if (!hasInfo) warnings.Add(MatchInfoMissingWarning);
        else if (info.MatchId.Length == 0) warnings.Add("matchInfo.matchId missing.");

        var players = ParsePlayers(root, warnings);
        var teams = ParseTeams(root, warnings);
        var rounds = ParseRounds(root, warnings);
        if (info.CompletionState.Contains("surrender", StringComparison.OrdinalIgnoreCase))
        {
            var reportedRounds = players.Select(p => p.Stats?.RoundsPlayed ?? 0).Where(count => count > 0).DefaultIfEmpty(0).Max();
            if (reportedRounds > 0 && rounds.Count > reportedRounds)
                rounds = rounds.Take(reportedRounds).ToArray();
            while (rounds.Count > 0 && rounds[^1].RoundResult.Contains("surrender", StringComparison.OrdinalIgnoreCase))
                rounds = rounds.Take(rounds.Count - 1).ToArray();
        }

        return new MatchParseResult(new MatchDetailsData(info, players, teams, rounds, warnings.ToArray()), null);
    }

    /// <summary>Warning recorded when the payload had no matchInfo block (also persisted in archives).</summary>
    public const string MatchInfoMissingWarning = "matchInfo missing.";

    private static MatchInfoData ParseInfo(JsonElement info) => new(
        ReadString(info, "matchId", "matchID"),
        ReadString(info, "mapId", "mapID"),
        ReadString(info, "queueID", "queueId"),
        ReadLong(info, "gameStartMillis"),
        ReadLong(info, "gameLengthMillis"),
        ReadString(info, "completionState"))
    {
        IsCustomGame = ReadBool(info, "isCustomGame") ??
            (ReadString(info, "provisioningFlowID", "provisioningFlowId").Equals("CustomGame", StringComparison.OrdinalIgnoreCase) ? true : (bool?)null),
        FormatVersion = MatchInfoData.CurrentFormatVersion,
    };

    private static IReadOnlyList<MatchPlayerData> ParsePlayers(JsonElement root, List<string> warnings)
    {
        var result = new List<MatchPlayerData>();
        if (!TryGetArray(root, out var array, "players"))
        {
            warnings.Add("players[] missing.");
            return result;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var element in array.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object) continue;
            var subject = ReadString(element, "subject", "puuid");
            if (subject.Length == 0)
            {
                warnings.Add("Skipped player without subject.");
                continue;
            }
            if (!seen.Add(subject))
            {
                warnings.Add($"Skipped duplicate player {subject}.");
                continue;
            }

            MatchPlayerStatsData? stats = null;
            if (TryGetObject(element, out var s, "stats"))
            {
                stats = new MatchPlayerStatsData(
                    ReadInt(s, "score"),
                    ReadInt(s, "kills"),
                    ReadInt(s, "deaths"),
                    ReadInt(s, "assists"),
                    ReadInt(s, "roundsPlayed"));
            }

            result.Add(new MatchPlayerData(
                subject,
                ReadString(element, "gameName"),
                ReadString(element, "tagLine"),
                ReadString(element, "teamId", "teamID"),
                NormalizeGuidLike(ReadString(element, "characterId", "characterID")),
                stats) { CompetitiveTier = ReadInt(element, "competitiveTier") });
        }
        return result;
    }

    private static IReadOnlyList<MatchTeamData> ParseTeams(JsonElement root, List<string> warnings)
    {
        var result = new List<MatchTeamData>();
        if (!TryGetArray(root, out var array, "teams"))
        {
            warnings.Add("teams[] missing; match outcome will be unknown.");
            return result;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var element in array.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object) continue;
            var teamId = ReadString(element, "teamId", "teamID");
            if (teamId.Length == 0 || !seen.Add(teamId)) continue;
            result.Add(new MatchTeamData(teamId, ReadBool(element, "won"), ReadInt(element, "roundsWon")));
        }
        return result;
    }

    private static IReadOnlyList<RoundResultData> ParseRounds(JsonElement root, List<string> warnings)
    {
        var parsed = new List<(RoundResultData Round, int Order)>();
        if (!TryGetArray(root, out var array, "roundResults"))
        {
            warnings.Add("roundResults[] missing; round-derived stats unavailable.");
            return Array.Empty<RoundResultData>();
        }

        var matchKillsByRound = TryGetArray(root, out var matchKills, "kills")
            ? matchKills.EnumerateArray().ToLookup(kill => ReadInt(kill, "round", "roundNum"))
            : null;
        var order = 0;
        foreach (var element in array.EnumerateArray())
        {
            if (element.ValueKind != JsonValueKind.Object) continue;

            var participants = new List<string>();
            var participantSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var kills = new List<RoundKillData>();
            var killKeys = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var damage = new List<RoundDamageData>();
            var hasDamage = false;

            if (TryGetArray(element, out var playerStats, "playerStats"))
            {
                foreach (var ps in playerStats.EnumerateArray())
                {
                    if (ps.ValueKind != JsonValueKind.Object) continue;
                    var subject = ReadString(ps, "subject", "puuid");
                    if (subject.Length > 0 && participantSet.Add(subject)) participants.Add(subject);

                    if (TryGetArray(ps, out var killArray, "kills"))
                        AddKills(killArray.EnumerateArray(), subject, kills, killKeys);

                    if (TryGetArray(ps, out var damageArray, "damage"))
                    {
                        hasDamage = true;
                        if (subject.Length == 0) continue;
                        foreach (var d in damageArray.EnumerateArray())
                        {
                            if (d.ValueKind != JsonValueKind.Object) continue;
                            var receiver = ReadString(d, "receiver");
                            if (receiver.Length == 0) continue;
                            damage.Add(new RoundDamageData(
                                subject,
                                receiver,
                                NonNegative(ReadInt(d, "damage")),
                                NonNegative(ReadInt(d, "headshots")),
                                NonNegative(ReadInt(d, "bodyshots")),
                                NonNegative(ReadInt(d, "legshots"))));
                        }
                    }
                }
            }

            // Merge player, round and match-level kill lists without double counting.
            if (TryGetArray(element, out var roundKills, "kills"))
                AddKills(roundKills.EnumerateArray(), "", kills, killKeys);

            var roundNumber = ReadInt(element, "roundNum");
            if (roundNumber.HasValue && matchKillsByRound is not null)
                AddKills(matchKillsByRound[roundNumber], "", kills, killKeys);

            parsed.Add((new RoundResultData(
                roundNumber,
                ReadString(element, "roundResult", "roundResultCode"),
                ReadString(element, "winningTeam"),
                ReadString(element, "winningTeamRole"),
                participants,
                kills,
                damage,
                hasDamage), order++));
        }

        return parsed
            .OrderBy(p => p.Round.RoundNum ?? int.MaxValue)
            .ThenBy(p => p.Order)
            .Select(p => p.Round)
            .ToArray();
    }

    private static void AddKills(IEnumerable<JsonElement> entries, string ownerSubject, List<RoundKillData> kills, HashSet<string> keys)
    {
        foreach (var k in entries)
        {
            if (k.ValueKind != JsonValueKind.Object) continue;

            // When "killer" is absent entirely, the entry belongs to the owning playerStats subject.
            // When it is present but empty, keep it empty (environmental / spike death).
            var killer = TryGetProperty(k, out _, "killer") ? ReadString(k, "killer") : ownerSubject;
            var victim = ReadString(k, "victim");
            var roundTime = ReadKillTime(k, "roundTime", "timeSinceRoundStartMillis");
            var gameTime = ReadKillTime(k, "gameTime", "timeSinceGameStartMillis");
            var damageType = "";
            var damageItem = "";
            if (TryGetObject(k, out var finishing, "finishingDamage"))
            {
                damageType = ReadString(finishing, "damageType");
                damageItem = NormalizeGuidLike(ReadString(finishing, "damageItem"));
            }

            if (killer.Length == 0 && victim.Length == 0) continue;
            var timeKey = roundTime is { } relative ? "r" + relative.ToString(CultureInfo.InvariantCulture)
                : gameTime is { } absolute ? "g" + absolute.ToString(CultureInfo.InvariantCulture) : "";
            var key = string.Join('|', killer, victim, timeKey, damageItem);
            if (!keys.Add(key)) continue;
            kills.Add(new RoundKillData(killer, victim, roundTime, damageType, damageItem) { TimeSinceGameStartMillis = gameTime });
        }
    }

    private static long? ReadKillTime(JsonElement kill, string primary, string alias) =>
        ReadLong(kill, primary) is >= 0 and var time ? time
        : ReadLong(kill, alias) is >= 0 and var alternate ? alternate : null;

    // ---------- JSON helpers (case-tolerant, never throw) ----------

    private static bool TryGetProperty(JsonElement obj, out JsonElement value, params string[] names)
    {
        value = default;
        if (obj.ValueKind != JsonValueKind.Object) return false;

        foreach (var name in names)
            if (obj.TryGetProperty(name, out value)) return true;

        foreach (var property in obj.EnumerateObject())
            foreach (var name in names)
                if (string.Equals(property.Name, name, StringComparison.OrdinalIgnoreCase))
                {
                    value = property.Value;
                    return true;
                }

        value = default;
        return false;
    }

    private static bool TryGetObject(JsonElement obj, out JsonElement value, params string[] names) =>
        TryGetProperty(obj, out value, names) && value.ValueKind == JsonValueKind.Object;

    private static bool TryGetArray(JsonElement obj, out JsonElement value, params string[] names) =>
        TryGetProperty(obj, out value, names) && value.ValueKind == JsonValueKind.Array;

    private static string ReadString(JsonElement obj, params string[] names)
    {
        if (!TryGetProperty(obj, out var value, names)) return "";
        return value.ValueKind switch
        {
            JsonValueKind.String => (value.GetString() ?? "").Trim(),
            JsonValueKind.Number => value.GetRawText(),
            _ => "",
        };
    }

    private static long? ReadLong(JsonElement obj, params string[] names)
    {
        if (!TryGetProperty(obj, out var value, names)) return null;
        switch (value.ValueKind)
        {
            case JsonValueKind.Number:
                if (value.TryGetInt64(out var l)) return l;
                if (value.TryGetDouble(out var d) && double.IsFinite(d) && Math.Floor(d) == d && Math.Abs(d) < 9.0e18)
                    return (long)d;
                return null;
            case JsonValueKind.String:
                return long.TryParse(value.GetString(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var parsed) ? parsed : null;
            default:
                return null;
        }
    }

    private static int? ReadInt(JsonElement obj, params string[] names) =>
        ReadLong(obj, names) is { } l && l >= int.MinValue && l <= int.MaxValue ? (int)l : null;

    private static bool? ReadBool(JsonElement obj, params string[] names)
    {
        if (!TryGetProperty(obj, out var value, names)) return null;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            JsonValueKind.String when bool.TryParse(value.GetString(), out var b) => b,
            JsonValueKind.Number when value.TryGetInt64(out var n) && (n == 0 || n == 1) => n == 1,
            _ => null,
        };
    }

    private static int NonNegative(int? value) => value is > 0 ? value.Value : 0;

    internal static string NormalizeGuidLike(string value) =>
        Guid.TryParse(value, out var guid) ? guid.ToString("D") : value;
}

#endregion

#region Engine

public static class MatchAnalyticsEngine
{
    private static readonly StringComparer IdComparer = StringComparer.OrdinalIgnoreCase;
    private static readonly HashSet<string> AcsQueues = new(StringComparer.OrdinalIgnoreCase)
    {
        "competitive", "unrated", "swiftplay", "spikerush", "premier", "replication", "onefa"
    };

    public const int RecentShortWindow = 5;
    public const int RecentLongWindow = 10;

    public const string CustomQueueId = "custom";

    // ---------------------------------------------------------------- game mode

    /// <summary>
    /// true = custom game, false = explicitly reported as not custom, null = cannot tell.
    /// The explicit matchInfo indicator always wins. Archives written before the indicator was kept
    /// (<see cref="MatchInfoData.FormatVersion"/> 0) fall back to Riot's convention that custom games
    /// carry an empty queueID, but only when the original payload actually had a matchInfo block.
    /// New payloads that omit both the indicator and the queue stay unknown.
    /// </summary>
    public static bool? IsCustomGame(MatchDetailsData match)
    {
        ArgumentNullException.ThrowIfNull(match);
        var info = match.Info;
        if (info.IsCustomGame is { } flag) return flag;
        if (IdComparer.Equals(info.QueueId, CustomQueueId)) return true;
        if (info.QueueId.Length == 0 && info.FormatVersion == 0 &&
            match.Warnings?.Contains(MatchDetailsParser.MatchInfoMissingWarning, StringComparer.Ordinal) != true)
            return true;
        return null;
    }

    /// <summary>Queue id for display: <see cref="CustomQueueId"/> for custom games, otherwise the raw queue id ("" = unknown).</summary>
    public static string DisplayQueueId(MatchDetailsData match) =>
        IsCustomGame(match) == true ? CustomQueueId : match.Info.QueueId;

    // ---------------------------------------------------------------- per match

    public static MatchAnalytics Analyze(MatchDetailsData match)
    {
        ArgumentNullException.ThrowIfNull(match);

        var teamOf = new Dictionary<string, string>(IdComparer);
        foreach (var player in match.Players)
            teamOf.TryAdd(player.Subject, player.TeamId);

        var accumulators = new Dictionary<string, PlayerAccumulator>(IdComparer);
        foreach (var player in match.Players)
            accumulators.TryAdd(player.Subject, new PlayerAccumulator());

        var hasDamage = match.Rounds.Any(r => r.HasDamageData);
        var hasTimeline = false;

        var summaries = new List<RoundSummary>(match.Rounds.Count);
        for (var index = 0; index < match.Rounds.Count; index++)
        {
            var round = match.Rounds[index];

            foreach (var subject in round.ParticipantSubjects)
                if (accumulators.TryGetValue(subject, out var participant))
                    participant.RoundsAppeared++;

            foreach (var hit in round.Damage)
            {
                if (!IsOpponentInteraction(hit.Dealer, hit.Receiver, teamOf)) continue;
                if (accumulators.TryGetValue(hit.Dealer, out var dealer))
                {
                    dealer.DamageDealt += hit.Damage;
                    dealer.HeadHits += hit.Headshots;
                    dealer.BodyHits += hit.Bodyshots;
                    dealer.LegHits += hit.Legshots;
                }
                if (accumulators.TryGetValue(hit.Receiver, out var receiver))
                    receiver.DamageReceived += hit.Damage;
            }

            var killsByTeam = new SortedDictionary<string, int>(StringComparer.Ordinal);
            foreach (var kill in round.Kills)
            {
                if (kill.Killer.Length == 0) continue;
                var team = teamOf.TryGetValue(kill.Killer, out var t) ? t : "";
                killsByTeam[team] = killsByTeam.TryGetValue(team, out var c) ? c + 1 : 1;

                if (IsOpponentInteraction(kill.Killer, kill.Victim, teamOf) && accumulators.TryGetValue(kill.Killer, out var killer))
                    killer.WeaponKills[kill.DamageItem] = killer.WeaponKills.TryGetValue(kill.DamageItem, out var w) ? w + 1 : 1;
            }

            var first = FirstKill(round, teamOf);
            if (first is not null)
            {
                hasTimeline = true;
                if (accumulators.TryGetValue(first.Victim, out var victim))
                    victim.FirstDeaths++;
                if (accumulators.TryGetValue(first.Killer, out var opener))
                    opener.FirstKills++;
            }

            summaries.Add(new RoundSummary(
                index,
                round.RoundNum,
                round.RoundResult,
                round.WinningTeam,
                round.WinningTeamRole,
                round.Kills.Count,
                first?.Killer,
                first?.Victim,
                first?.TimeSinceRoundStartMillis,
                first?.DamageItem,
                killsByTeam));
        }

        var teams = BuildTeams(match);
        var players = match.Players
            .Select(p => BuildPlayer(p, accumulators[p.Subject], teams, hasDamage, hasTimeline, match.Info.QueueId))
            .OrderByDescending(p => p.Score.HasValue)
            .ThenByDescending(p => p.Score ?? 0)
            .ThenByDescending(p => p.Kills ?? 0)
            .ThenBy(p => p.Subject, StringComparer.Ordinal)
            .ToArray();

        return new MatchAnalytics(match, teams, players, summaries, match.Rounds.Count, hasDamage, hasTimeline);
    }

    private static RoundKillData? FirstKill(RoundResultData round, IReadOnlyDictionary<string, string> teamOf)
    {
        var opponentKills = round.Kills.Where(kill => IsOpponentInteraction(kill.Killer, kill.Victim, teamOf) &&
            !kill.DamageType.Equals("Bomb", StringComparison.OrdinalIgnoreCase) &&
            !kill.DamageType.Equals("Fall", StringComparison.OrdinalIgnoreCase)).ToArray();

        // The per-player lists are grouped by killer, so payload order alone cannot identify an opener.
        if (opponentKills.All(kill => kill.TimeSinceRoundStartMillis is >= 0))
            return opponentKills.MinBy(kill => kill.TimeSinceRoundStartMillis);
        if (opponentKills.All(kill => kill.TimeSinceGameStartMillis is >= 0))
            return opponentKills.MinBy(kill => kill.TimeSinceGameStartMillis);
        return null;
    }

    /// <summary>
    /// True when both subjects are present, distinct, and not known to be teammates.
    /// Self damage and known friendly fire are excluded from dealt/received damage and kills credit.
    /// </summary>
    private static bool IsOpponentInteraction(string a, string b, IReadOnlyDictionary<string, string> teamOf)
    {
        if (a.Length == 0 || b.Length == 0 || IdComparer.Equals(a, b)) return false;
        if (teamOf.TryGetValue(a, out var ta) && teamOf.TryGetValue(b, out var tb) &&
            ta.Length > 0 && tb.Length > 0 && IdComparer.Equals(ta, tb))
            return false;
        return true;
    }

    private static IReadOnlyList<TeamResult> BuildTeams(MatchDetailsData match)
    {
        var teamIds = new List<string>();
        var known = new HashSet<string>(IdComparer);
        foreach (var team in match.Teams)
            if (known.Add(team.TeamId)) teamIds.Add(team.TeamId);
        foreach (var player in match.Players)
            if (player.TeamId.Length > 0 && known.Add(player.TeamId)) teamIds.Add(player.TeamId);

        var results = new List<TeamResult>(teamIds.Count);
        foreach (var teamId in teamIds)
        {
            var data = match.Teams.FirstOrDefault(t => IdComparer.Equals(t.TeamId, teamId));
            int? roundsLost = null;
            if (match.Teams.Count == 2 && data is not null)
                roundsLost = match.Teams.FirstOrDefault(t => !IdComparer.Equals(t.TeamId, teamId))?.RoundsWon;

            results.Add(new TeamResult(
                teamId,
                data?.Won,
                OutcomeForTeam(teamId, match.Teams),
                data?.RoundsWon,
                roundsLost,
                match.Rounds.Count(r => IdComparer.Equals(r.WinningTeam, teamId)),
                match.Players.Count(p => IdComparer.Equals(p.TeamId, teamId))));
        }
        return results;
    }

    /// <summary>
    /// Win when won=true. Loss when won=false and another team has won=true.
    /// Draw when every team (2+) reports won=false with equal, known roundsWon.
    /// Anything else is Unknown.
    /// </summary>
    public static MatchOutcome OutcomeForTeam(string teamId, IReadOnlyList<MatchTeamData> teams)
    {
        if (string.IsNullOrEmpty(teamId) || teams.Count == 0) return MatchOutcome.Unknown;
        var team = teams.FirstOrDefault(t => IdComparer.Equals(t.TeamId, teamId));
        if (team?.Won is not { } won) return MatchOutcome.Unknown;
        if (won) return MatchOutcome.Win;
        if (teams.Any(t => t.Won == true)) return MatchOutcome.Loss;
        if (teams.Count >= 2 &&
            teams.All(t => t.Won == false && t.RoundsWon.HasValue) &&
            teams.Select(t => t.RoundsWon!.Value).Distinct().Count() == 1)
            return MatchOutcome.Draw;
        return MatchOutcome.Unknown;
    }

    private static PlayerMatchStats BuildPlayer(
        MatchPlayerData player,
        PlayerAccumulator acc,
        IReadOnlyList<TeamResult> teams,
        bool hasDamage,
        bool hasTimeline,
        string? queueId)
    {
        var stats = player.Stats;
        var kills = stats?.Kills;
        var deaths = stats?.Deaths;

        int? rounds = null;
        var roundsFromResults = false;
        if (stats?.RoundsPlayed is > 0)
        {
            rounds = stats.RoundsPlayed;
        }
        else if (acc.RoundsAppeared > 0)
        {
            rounds = acc.RoundsAppeared;
            roundsFromResults = true;
        }

        int? dealt = hasDamage ? acc.DamageDealt : null;
        int? received = hasDamage ? acc.DamageReceived : null;
        int? head = hasDamage ? acc.HeadHits : null;
        int? body = hasDamage ? acc.BodyHits : null;
        int? leg = hasDamage ? acc.LegHits : null;

        var outcome = teams.FirstOrDefault(t => IdComparer.Equals(t.TeamId, player.TeamId))?.Outcome ?? MatchOutcome.Unknown;

        var weaponKills = acc.WeaponKills
            .Select(kv => new WeaponKillCount(kv.Key, kv.Value))
            .OrderByDescending(w => w.Kills)
            .ThenBy(w => w.DamageItem, StringComparer.Ordinal)
            .ToArray();

        return new PlayerMatchStats(
            player.Subject,
            player.GameName,
            player.TagLine,
            player.TeamId,
            player.CharacterId,
            outcome,
            stats?.Score,
            kills,
            deaths,
            stats?.Assists,
            kills.HasValue && deaths.HasValue ? kills.Value - deaths.Value : null,
            KillDeathRatio(kills, deaths),
            rounds,
            roundsFromResults,
            AcsQueues.Contains((queueId ?? "").Trim()) ? PerRound(stats?.Score, rounds) : null,
            dealt,
            received,
            PerRound(dealt, rounds),
            dealt.HasValue && received.HasValue ? PerRound(dealt.Value - received.Value, rounds) : null,
            head,
            body,
            leg,
            HeadshotPercent(head, body, leg),
            hasTimeline ? acc.FirstKills : null,
            hasTimeline ? acc.FirstDeaths : null,
            weaponKills) { CompetitiveTier = player.CompetitiveTier };
    }

    /// <summary>kills / deaths; when deaths is zero the ratio is kills.</summary>
    public static double? KillDeathRatio(int? kills, int? deaths)
    {
        if (kills is not { } k || deaths is not { } d || k < 0 || d < 0) return null;
        return d == 0 ? k : (double)k / d;
    }

    public static double? PerRound(int? value, int? rounds) =>
        value.HasValue && rounds is > 0 ? (double)value.Value / rounds.Value : null;

    /// <summary>head / (head + body + leg) * 100; null when no hits were recorded.</summary>
    public static double? HeadshotPercent(int? head, int? body, int? leg)
    {
        if (head is not { } h || body is not { } b || leg is not { } l) return null;
        long total = (long)h + b + l;
        return total > 0 ? h * 100.0 / total : null;
    }

    public static double? Percent(int numerator, int denominator) =>
        denominator > 0 ? numerator * 100.0 / denominator : null;

    // ---------------------------------------------------------------- history

    public static PlayerHistorySummary AnalyzeHistory(IEnumerable<MatchDetailsData> matches, string subject, string? queueId = null)
    {
        ArgumentNullException.ThrowIfNull(matches);
        return AnalyzeHistory(matches.Where(m => m is not null).Select(Analyze), subject, queueId);
    }

    /// <summary>
    /// Aggregates one subject's history. Matches are de-duplicated by matchId (first occurrence
    /// wins), optionally filtered by queue id, and ordered newest first by gameStartMillis
    /// (unknown start times last, then matchId ordinal) so results are deterministic.
    /// </summary>
    public static PlayerHistorySummary AnalyzeHistory(IEnumerable<MatchAnalytics> analyses, string subject, string? queueId = null)
    {
        ArgumentNullException.ThrowIfNull(analyses);
        subject = (subject ?? "").Trim();

        var seenMatchIds = new HashSet<string>(IdComparer);
        var entries = new List<PlayerHistoryEntry>();
        foreach (var analysis in analyses)
        {
            if (analysis is null) continue;
            var info = analysis.Info;
            if (!string.IsNullOrEmpty(queueId) && !IdComparer.Equals(info.QueueId, queueId)) continue;
            var stats = analysis.FindPlayer(subject);
            if (stats is null) continue;
            if (info.MatchId.Length > 0 && !seenMatchIds.Add(info.MatchId)) continue;
            entries.Add(new PlayerHistoryEntry(info.MatchId, info.MapId, info.QueueId, info.GameStartMillis, stats));
        }

        var ordered = entries
            .OrderByDescending(e => e.GameStartMillis.HasValue)
            .ThenByDescending(e => e.GameStartMillis ?? 0)
            .ThenBy(e => e.MatchId, StringComparer.Ordinal)
            .ToArray();

        var tally = Tally(ordered);

        long totalKills = 0, totalDeaths = 0;
        var kdSamples = 0;
        foreach (var e in ordered)
        {
            if (e.Stats.Kills is not { } k || e.Stats.Deaths is not { } d || k < 0 || d < 0) continue;
            totalKills += k;
            totalDeaths += d;
            kdSamples++;
        }
        double? overallKd = kdSamples == 0 ? null : totalDeaths == 0 ? totalKills : (double)totalKills / totalDeaths;

        return new PlayerHistorySummary(
            subject,
            ordered.Length,
            tally.Wins,
            tally.Losses,
            tally.Draws,
            ordered.Length - tally.Known,
            Percent(tally.Wins, tally.Known),
            Recent(ordered, RecentShortWindow),
            Recent(ordered, RecentLongWindow),
            Mean(ordered.Select(e => e.Stats.AverageCombatScore)),
            Mean(ordered.Select(e => e.Stats.KillDeathRatio)),
            overallKd,
            Mean(ordered.Select(e => e.Stats.AverageDamagePerRound)),
            Mean(ordered.Select(e => e.Stats.HeadshotPercent)),
            Breakdown(ordered, e => e.MapId),
            Breakdown(ordered, e => e.Stats.CharacterId),
            ordered);
    }

    private static RecentWinRate Recent(IReadOnlyList<PlayerHistoryEntry> newestFirst, int window)
    {
        var slice = newestFirst.Take(window).ToArray();
        var tally = Tally(slice);
        return new RecentWinRate(window, slice.Length, tally.Known, tally.Wins, Percent(tally.Wins, tally.Known));
    }

    private static IReadOnlyList<HistoryBreakdownEntry> Breakdown(
        IReadOnlyList<PlayerHistoryEntry> entries,
        Func<PlayerHistoryEntry, string> keySelector)
    {
        var total = entries.Count;
        return entries
            .GroupBy(keySelector, IdComparer)
            .Select(group =>
            {
                var items = group.ToArray();
                var tally = Tally(items);
                return new HistoryBreakdownEntry(
                    group.Key,
                    items.Length,
                    total > 0 ? items.Length * 100.0 / total : 0,
                    tally.Wins,
                    tally.Losses,
                    tally.Draws,
                    tally.Known,
                    Percent(tally.Wins, tally.Known));
            })
            .OrderByDescending(b => b.Matches)
            .ThenBy(b => b.Key, StringComparer.Ordinal)
            .ToArray();
    }

    private static (int Wins, int Losses, int Draws, int Known) Tally(IEnumerable<PlayerHistoryEntry> entries)
    {
        int wins = 0, losses = 0, draws = 0;
        foreach (var e in entries)
        {
            switch (e.Stats.Outcome)
            {
                case MatchOutcome.Win: wins++; break;
                case MatchOutcome.Loss: losses++; break;
                case MatchOutcome.Draw: draws++; break;
            }
        }
        return (wins, losses, draws, wins + losses + draws);
    }

    private static double? Mean(IEnumerable<double?> values)
    {
        double sum = 0;
        var count = 0;
        foreach (var value in values)
        {
            if (value is not { } v || !double.IsFinite(v)) continue;
            sum += v;
            count++;
        }
        return count == 0 ? null : sum / count;
    }

    private sealed class PlayerAccumulator
    {
        public int RoundsAppeared;
        public int DamageDealt;
        public int DamageReceived;
        public int HeadHits;
        public int BodyHits;
        public int LegHits;
        public int FirstKills;
        public int FirstDeaths;
        public readonly Dictionary<string, int> WeaponKills = new(StringComparer.OrdinalIgnoreCase);
    }
}

#endregion
