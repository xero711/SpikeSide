namespace ValoAutoPick;

internal interface ILiveTrackerSource
{
    Task<string?> GetCoreMatchId(CancellationToken ct);
    Task<LiveRosterMatch?> GetCoreLiveRoster(string matchId, CancellationToken ct);
    Task<string?> GetMatchId(CancellationToken ct);
    Task<MatchSnapshot> GetMatch(string matchId, CancellationToken ct);
}

internal sealed record LiveTrackerSnapshot(LiveRosterMatch? Game, MatchSnapshot? Pregame);

internal static class LiveTrackerReader
{
    // A core-game roster takes precedence during the transition from character select.
    internal static async Task<LiveTrackerSnapshot> ReadAsync(ILiveTrackerSource source, string subject, CancellationToken ct)
    {
        var coreId = await source.GetCoreMatchId(ct);
        if (coreId is not null)
        {
            var game = await source.GetCoreLiveRoster(coreId, ct);
            if (game is not null)
            {
                if (!game.MatchId.Equals(coreId, StringComparison.OrdinalIgnoreCase) ||
                    !game.Players.Any(p => p.Subject.Equals(subject, StringComparison.OrdinalIgnoreCase)))
                    throw new InvalidDataException("現在の試合の参加者を確認できません");
                return new(game, null);
            }
        }

        var pregameId = await source.GetMatchId(ct);
        if (pregameId is null) return new(null, null);
        MatchSnapshot pregame;
        try { pregame = await source.GetMatch(pregameId, ct); }
        catch (ApiException e) when (e.Status == 404) { return new(null, null); }
        if (!pregame.Id.Equals(pregameId, StringComparison.OrdinalIgnoreCase) ||
            !pregame.Allies.Any(p => p.Subject.Equals(subject, StringComparison.OrdinalIgnoreCase)))
            throw new InvalidDataException("選択画面の参加者を確認できません");
        return new(null, pregame);
    }
}
