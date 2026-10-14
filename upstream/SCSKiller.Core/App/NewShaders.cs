namespace SCSKiller.Core.App;

/// <summary>When the app tells about compiled games that have pipelines to compile again: their recording (this PC's or
/// the community database's) or a newer planner found ones the last warm didn't compile. Driver-stale games are the
/// driver-update notification's. A game without shader stutter (Games.GameVerdicts) is never told about.</summary>
public static class NewShaders
{
    public static long Count(GameState s) => (s.NewPipelines ?? 0) + s.RecordedSinceWarm;

    /// <summary>A game with fewer new pipelines than this isn't told about (its page still shows them): 1% of its plan,
    /// at least 100, never more than 1,000. Measured after community updates: 2 to about 130 new on plans of 20k to 200k.</summary>
    public static long Enough(GameState s) =>
        Math.Clamp((s.Plan is { } p ? p.Recorded + p.Generated + p.D3D11Shaders + p.MiddlewareItems : 0) / 100, 100, 1_000);

    /// <summary>A rebuilt plan's "can now compile N more" (ScsKiller.PlannerChanged): told once per compile, whatever the
    /// count and the day. Not yet rebuilt ("can now compile more of this game"), its count is the last plan's.</summary>
    static bool PlannerChanged(GameState s) => s.Status == GameStatus.Stale && s.StatusReason.StartsWith("SCSKiller can now compile ", StringComparison.Ordinal)
        && !s.StatusReason.StartsWith("SCSKiller can now compile more", StringComparison.Ordinal);

    /// <summary>What a notification about the game says, as the notified store keeps it; null when there is nothing to tell.</summary>
    public static string? Key(GameState s, IReadOnlySet<string> driverStale) =>
        s is { Status: GameStatus.Warmed or GameStatus.Stale, AntiCheat: AntiCheat.None, WarmedAt: { } at, NoStutter: null, CompileUnreached: false } && !driverStale.Contains(s.Game.Id)
        && Count(s) is var n and > 0 ? $"{at.UtcTicks}|{n}" : null;

    /// <summary>A notified store entry: <see cref="Key"/>'s warm and count, when it was told (UTC ticks), and whether that was
    /// the compile's planner notification. An entry kept before told times were: told at <paramref name="now"/>, of either kind.</summary>
    readonly record struct Told(long Warm, long Count, long At, bool Planner)
    {
        public static Told? Parse(string? entry, DateTimeOffset now) =>
            entry?.Split('|') is [var w, var n, .. var rest] && long.TryParse(w, out var warm) && long.TryParse(n, out var count)
                ? rest is [var t, ..] && long.TryParse(t, out var at) ? new Told(warm, count, at, rest is [_, "p"]) : new Told(warm, count, now.UtcTicks, true) : null;

        public override string ToString() => $"{Warm}|{Count}|{At}" + (Planner ? "|p" : "");
    }

    /// <summary>The games to notify about now and the notified store to keep. A running game waits for its exit, a game
    /// queued for a compile isn't told about, and one is told again only when its new pipelines grew by
    /// <see cref="Enough"/> since it was told (since its compile, for a game compiled since), and not within a day of the
    /// last time. A rebuilt plan's (<see cref="PlannerChanged"/>) is told once per compile whatever the count and the day.
    /// <paramref name="driverStale"/>: ids of the games warmed for another driver (ScsKiller.DriverStaleGames).</summary>
    public static (IReadOnlyList<GameState> Due, Dictionary<string, string> Notified) Due(IEnumerable<GameState> games,
        IEnumerable<QueueItem> queue, IReadOnlyDictionary<string, string> notified, IReadOnlySet<string> driverStale, DateTimeOffset now)
    {
        var queued = queue.Where(q => !q.PlanCheck && q.Stage is not (QueueStage.Done or QueueStage.Failed or QueueStage.Stopped))
            .Select(q => q.GameId).ToHashSet();
        var due = new List<GameState>();
        var keep = new Dictionary<string, string>();
        foreach (var s in games)
        {
            var told = Told.Parse(notified.GetValueOrDefault(s.Game.Id), now);
            var recent = told is { } r && now.UtcTicks - r.At < TimeSpan.TicksPerDay;
            var warm = s.WarmedAt?.UtcTicks;
            var same = told is { } t && t.Warm == warm;   // the count told is this compile's
            if (Key(s, driverStale) is null)
            {
                if (recent) keep[s.Game.Id] = told.ToString()!;   // the day counts from the last notification, whatever came since
                continue;
            }
            var n = Count(s);
            var plannerDue = PlannerChanged(s) && !(same && told!.Value.Planner);
            if (!plannerDue && (recent || n - (same ? told!.Value.Count : 0) < Enough(s)))
            {
                if (same || recent) keep[s.Game.Id] = told.ToString()!;
                continue;
            }
            if (s.Playing || queued.Contains(s.Game.Id))
            {
                if (told != null) keep[s.Game.Id] = told.ToString()!;   // what is new now is told once this passes
                continue;
            }
            due.Add(s);
            keep[s.Game.Id] = new Told(warm!.Value, n, now.UtcTicks, plannerDue || same && told!.Value.Planner).ToString();
        }
        return (due, keep);
    }
}
