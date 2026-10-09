namespace IceTap.Core;

public enum Phase
{
    Title,
    Playing,
    Paused,
    Over,
}

public sealed class Actor
{
    public int Id { get; set; }
    public int Hole { get; set; }
    public SpeciesId Species { get; set; }
    public SpeciesKind Kind { get; set; }
    public int Points { get; set; }
    public double BornAt { get; set; }
    public double BreachAt { get; set; }
    public double HideAt { get; set; }
    public bool Resolved { get; set; }
}

public sealed class State
{
    public Phase Phase { get; set; } = Phase.Title;
    public double ElapsedMs { get; set; }
    public int Score { get; set; }
    public int Combo { get; set; }
    public int BestCombo { get; set; }
    public Actor?[] Holes { get; set; } = new Actor?[Rules.HoleCount];
    public double NextSpawnAt { get; set; }
    public CatchTally Catches { get; set; } = Rules.EmptyCatches();
    public int HazardsHit { get; set; }
    public int Escaped { get; set; }
    public int Dodged { get; set; }
    public int ActorSeq { get; set; } = 1;
    public double[] LastUsed { get; set; } = EmptyLastUsed();
    public bool Endless { get; set; }
    public int Strikes { get; set; }

    public static double[] EmptyLastUsed()
    {
        var used = new double[Rules.HoleCount];
        Array.Fill(used, -10_000);
        return used;
    }
}

public sealed record Summary(
    int Score,
    int BestCombo,
    CatchTally Catches,
    int Dodged,
    int HazardsHit,
    int Escaped,
    bool Endless,
    double ElapsedMs)
{
    public static Summary Empty() => new(0, 0, Rules.EmptyCatches(), 0, 0, 0, false, 0);
}

public abstract record RoundEvent
{
    public sealed record Escape(int Hole, SpeciesId Species, int ComboLost) : RoundEvent;
    public sealed record Dodge(int Hole, int Bonus) : RoundEvent;
    public sealed record Over(Summary Summary) : RoundEvent;
}

public abstract record TapResult
{
    public sealed record Idle : TapResult;
    public sealed record Whiff : TapResult;
    public sealed record Catch(FishId Species, int Points, int Combo, int Mult, int Total) : TapResult;
    public sealed record Hazard(SpeciesId Species, int Penalty, int ComboLost, int Score) : TapResult;
}

public sealed class Game
{
    const double HitchMs = 400;

    readonly bool randomStarts;
    uint seed;
    Func<double> rng;
    double startedAt;
    double pausedAccum;
    double? pauseAt;
    Summary? lastSummary;

    public Game(uint? seed = null, double durationMs = Rules.RoundMs)
    {
        DurationMs = durationMs;
        randomStarts = seed is null;
        this.seed = seed ?? 1;
        rng = Rules.Mulberry32(this.seed);
        State = Fresh();
    }

    public double DurationMs { get; }

    public State State { get; private set; }

    public static Game Create(uint? seed = null, double durationMs = Rules.RoundMs) => new(seed, durationMs);

    public Summary? CurrentSummary() => lastSummary;

    public void Start(double now, uint? nextSeed = null, bool endless = false)
    {
        if (nextSeed is uint explicitSeed) seed = explicitSeed;
        else if (randomStarts) seed = (uint)(Random.Shared.NextDouble() * 0x7fffffff);
        rng = Rules.Mulberry32(seed);
        startedAt = now;
        pausedAccum = 0;
        pauseAt = null;
        lastSummary = null;
        State = Fresh();
        State.Endless = endless;
        State.Phase = Phase.Playing;
        var ramp = RampMs();
        State.NextSpawnAt = Math.Max(16, Rules.JsRound(ramp * 0.004));
    }

    public Summary CashOut(double now)
    {
        if (State.Phase == Phase.Paused) Resume(now);
        if (State.Phase == Phase.Playing)
        {
            State.ElapsedMs = Compute(now);
            Finish([]);
        }

        return lastSummary ?? Summary.Empty();
    }

    public void Pause(double now)
    {
        if (State.Phase != Phase.Playing) return;
        pauseAt = now;
        State.Phase = Phase.Paused;
    }

    public void Resume(double now)
    {
        if (State.Phase != Phase.Paused || pauseAt is null) return;
        pausedAccum += Math.Max(0, now - pauseAt.Value);
        pauseAt = null;
        State.Phase = Phase.Playing;
    }

    public TapResult Tap(int hole, double now)
    {
        if (hole < 0 || hole >= Rules.HoleCount) return new TapResult.Whiff();
        var elapsed = Compute(now);
        var result = Rules.ApplyTap(State, hole, elapsed);
        if (result is TapResult.Hazard && State.Endless && State.Phase == Phase.Playing)
        {
            State.Strikes += 1;
            if (State.Strikes >= Rules.StrikeLimit)
            {
                State.ElapsedMs = elapsed;
                Finish([]);
            }
        }

        return result;
    }

    public List<RoundEvent> Tick(double now)
    {
        if (State.Phase != Phase.Playing) return [];

        var elapsed = Compute(now);
        var gap = elapsed - State.ElapsedMs;
        // A stalled frame must not burn the round or dump every queued spawn at once.
        if (gap > HitchMs)
        {
            pausedAccum += gap - 16;
            elapsed = Compute(now);
        }

        var events = new List<RoundEvent>();
        var capped = State.Endless ? elapsed : Math.Min(elapsed, DurationMs);
        Expire(capped, events);

        if (!State.Endless && capped >= DurationMs)
        {
            State.ElapsedMs = DurationMs;
            Finish(events);
            return events;
        }

        var ramp = RampMs();
        if (capped >= State.NextSpawnAt)
        {
            var spawned = TrySpawn(capped);
            var wait = spawned ? Rules.SpawnInterval(PaceAt(capped), rng, ramp) : 100;
            State.NextSpawnAt = capped + wait;
        }

        State.ElapsedMs = capped;
        return events;
    }

    double Compute(double now)
    {
        var pending = pauseAt is null ? 0 : Math.Max(0, now - pauseAt.Value);
        return Math.Max(0, now - startedAt - pausedAccum - pending);
    }

    double RampMs() => State.Endless ? Rules.RoundMs : DurationMs;

    // Endless holds the opening pace for the whole run. The timed cast still speeds up.
    double PaceAt(double elapsed) => State.Endless ? 0 : elapsed;

    bool TrySpawn(double elapsed)
    {
        var ramp = RampMs();
        var pace = PaceAt(elapsed);
        if (Occupied() >= Rules.MaxConcurrent(pace, ramp)) return false;
        // Two hazards are already up, so the next visitor is a fish.
        var species = Rules.ChooseSpecies(elapsed, rng, HazardCount(), ramp);
        var hole = Rules.PickHole(State, elapsed, rng);
        if (hole is null) return false;
        Rules.Arm(State, hole.Value, species, elapsed, ramp, pace);
        return true;
    }

    void Expire(double elapsed, List<RoundEvent> events)
    {
        for (var i = 0; i < Rules.HoleCount; i++)
        {
            var actor = State.Holes[i];
            if (actor is null || elapsed < actor.HideAt) continue;
            State.Holes[i] = null;
            State.LastUsed[i] = elapsed;
            actor.Resolved = true;
            if (actor.Kind == SpeciesKind.Fish)
            {
                var comboLost = State.Combo;
                State.Combo = 0;
                State.Escaped += 1;
                events.Add(new RoundEvent.Escape(i, actor.Species, comboLost));
            }
            else
            {
                State.Dodged += 1;
                State.Score += Rules.DodgeBonus;
                events.Add(new RoundEvent.Dodge(i, Rules.DodgeBonus));
            }
        }
    }

    void Finish(List<RoundEvent> events)
    {
        if (State.Phase == Phase.Over) return;
        for (var i = 0; i < Rules.HoleCount; i++) State.Holes[i] = null;
        State.Phase = Phase.Over;
        var summary = new Summary(
            State.Score,
            State.BestCombo,
            State.Catches.Clone(),
            State.Dodged,
            State.HazardsHit,
            State.Escaped,
            State.Endless,
            State.ElapsedMs);
        lastSummary = summary;
        events.Add(new RoundEvent.Over(summary));
    }

    int Occupied()
    {
        var count = 0;
        foreach (var hole in State.Holes)
        {
            if (hole is not null) count += 1;
        }

        return count;
    }

    int HazardCount()
    {
        var count = 0;
        foreach (var hole in State.Holes)
        {
            if (hole?.Kind == SpeciesKind.Hazard) count += 1;
        }

        return count;
    }

    static State Fresh() => new()
    {
        Holes = new Actor?[Rules.HoleCount],
        LastUsed = State.EmptyLastUsed(),
        Catches = Rules.EmptyCatches(),
    };
}
