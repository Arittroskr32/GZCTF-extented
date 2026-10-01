namespace GZCTF.Discord;

/// <summary>
/// A queued Discord notification. Kept small and self-contained so the flag-checking hot path only
/// has to enqueue; any extra data the embed needs (game/challenge/team details) is loaded later in
/// the background service.
/// </summary>
internal abstract record DiscordNotification(int GameId);

/// <summary>
/// A blood solve to announce publicly. <paramref name="BloodType" /> is the final blood tier decided
/// by the flag checker, so the Discord message always agrees with the in-game notice.
/// </summary>
internal sealed record FirstBloodNotification(
    int GameId,
    int ChallengeId,
    int TeamId,
    SubmissionType BloodType,
    DateTimeOffset SolveTimeUtc) : DiscordNotification(GameId);

/// <summary>
/// A shared-flag (cheat) detection to announce privately. Team names, submitting user and flag are
/// captured at the hook point (already resolved there); only game/challenge details are loaded later.
/// </summary>
internal sealed record CheatNotification(
    int GameId,
    int ChallengeId,
    int SubmissionId,
    string SubmitTeamName,
    string SourceTeamName,
    string SubmitUserName,
    string Flag,
    DateTimeOffset SubmitTimeUtc) : DiscordNotification(GameId);
