namespace IceTap.Core;

public enum FishId
{
    Perch,
    Trout,
    Char,
    Pike,
}

public enum SpeciesId
{
    Perch,
    Trout,
    Char,
    Pike,
    Boot,
}

public enum SpeciesKind
{
    Fish,
    Hazard,
}

public readonly record struct SpeciesInfo(SpeciesKind Kind, string Name, int Points);

public readonly record struct WeightedSpecies(SpeciesId Id, int Weight);

public readonly record struct VisibleWindow(int RippleMs, int ActiveMs);

public sealed class CatchTally : IEquatable<CatchTally>
{
    public int Perch { get; set; }
    public int Trout { get; set; }
    public int Char { get; set; }
    public int Pike { get; set; }

    public int Total => Perch + Trout + Char + Pike;

    public int this[FishId id]
    {
        get => id switch
        {
            FishId.Perch => Perch,
            FishId.Trout => Trout,
            FishId.Char => Char,
            FishId.Pike => Pike,
            _ => 0,
        };
        set
        {
            switch (id)
            {
                case FishId.Perch:
                    Perch = value;
                    break;
                case FishId.Trout:
                    Trout = value;
                    break;
                case FishId.Char:
                    Char = value;
                    break;
                case FishId.Pike:
                    Pike = value;
                    break;
            }
        }
    }

    public CatchTally Clone() => new()
    {
        Perch = Perch,
        Trout = Trout,
        Char = Char,
        Pike = Pike,
    };

    public bool Equals(CatchTally? other) =>
        other is not null
        && Perch == other.Perch
        && Trout == other.Trout
        && Char == other.Char
        && Pike == other.Pike;

    public override bool Equals(object? obj) => Equals(obj as CatchTally);

    public override int GetHashCode() => HashCode.Combine(Perch, Trout, Char, Pike);
}

public static class Rules
{
    public const double RoundMs = 60_000;
    public const int HoleCount = 9;
    public const int HazardPenalty = 200;
    public const int DodgeBonus = 50;
    public const int StrikeLimit = 3;

    public static readonly FishId[] FishIds = [FishId.Perch, FishId.Trout, FishId.Char, FishId.Pike];

    static readonly (double Open, double Close)[] TotalMs =
    [
        (1200, 760),
        (1080, 680),
        (980, 620),
        (880, 540),
        (1160, 800),
    ];

    static readonly double[] RippleRatio = [0.28, 0.3, 0.4, 0.42, 0.38];

    static readonly WeightedSpecies[][] Tables =
    [
        [
            new(SpeciesId.Perch, 68),
            new(SpeciesId.Trout, 22),
            new(SpeciesId.Boot, 10),
        ],
        [
            new(SpeciesId.Perch, 38),
            new(SpeciesId.Trout, 26),
            new(SpeciesId.Char, 12),
            new(SpeciesId.Boot, 14),
        ],
        [
            new(SpeciesId.Perch, 22),
            new(SpeciesId.Trout, 20),
            new(SpeciesId.Char, 16),
            new(SpeciesId.Pike, 8),
            new(SpeciesId.Boot, 12),
        ],
        [
            new(SpeciesId.Perch, 12),
            new(SpeciesId.Trout, 16),
            new(SpeciesId.Char, 18),
            new(SpeciesId.Pike, 16),
            new(SpeciesId.Boot, 13),
        ],
    ];

    public static SpeciesInfo Info(SpeciesId id) => id switch
    {
        SpeciesId.Perch => new(SpeciesKind.Fish, "Perch", 100),
        SpeciesId.Trout => new(SpeciesKind.Fish, "Trout", 250),
        SpeciesId.Char => new(SpeciesKind.Fish, "Char", 500),
        SpeciesId.Pike => new(SpeciesKind.Fish, "Pike", 1000),
        SpeciesId.Boot => new(SpeciesKind.Hazard, "Boot", 0),
        _ => throw new ArgumentOutOfRangeException(nameof(id)),
    };

    public static bool IsFish(SpeciesId id) => Info(id).Kind == SpeciesKind.Fish;

    public static FishId AsFish(SpeciesId id) => id switch
    {
        SpeciesId.Perch => FishId.Perch,
        SpeciesId.Trout => FishId.Trout,
        SpeciesId.Char => FishId.Char,
        SpeciesId.Pike => FishId.Pike,
        _ => throw new ArgumentOutOfRangeException(nameof(id)),
    };

    public static SpeciesId ToSpecies(FishId id) => id switch
    {
        FishId.Perch => SpeciesId.Perch,
        FishId.Trout => SpeciesId.Trout,
        FishId.Char => SpeciesId.Char,
        FishId.Pike => SpeciesId.Pike,
        _ => throw new ArgumentOutOfRangeException(nameof(id)),
    };

    public static string Slug(SpeciesId id) => id switch
    {
        SpeciesId.Perch => "perch",
        SpeciesId.Trout => "trout",
        SpeciesId.Char => "char",
        SpeciesId.Pike => "pike",
        SpeciesId.Boot => "boot",
        _ => throw new ArgumentOutOfRangeException(nameof(id)),
    };

    public static CatchTally EmptyCatches() => new();

    public static int Multiplier(int combo)
    {
        if (combo >= 20) return 5;
        if (combo >= 15) return 4;
        if (combo >= 10) return 3;
        if (combo >= 5) return 2;
        return 1;
    }

    public static string FormatClock(double remainingMs)
    {
        var secs = Math.Max(0, (int)Math.Ceiling(remainingMs / 1000d));
        var minutes = secs / 60;
        var rest = secs % 60;
        return $"{minutes}:{rest:00}";
    }

    public static int PhaseIndex(double elapsedMs, double durationMs = RoundMs)
    {
        if (elapsedMs * 4 < durationMs) return 0;
        if (elapsedMs * 2 < durationMs) return 1;
        if (elapsedMs * 4 < durationMs * 3) return 2;
        return 3;
    }

    public static WeightedSpecies[] SpeciesTable(int phase) => Tables[phase];

    public static int MaxConcurrent(double elapsedMs, double durationMs = RoundMs)
    {
        if (elapsedMs * 60 < durationMs * 12) return 1;
        if (elapsedMs * 60 < durationMs * 32) return 2;
        if (elapsedMs * 60 < durationMs * 48) return 3;
        return 4;
    }

    public static VisibleWindow VisibleWindow(SpeciesId species, double elapsedMs, double durationMs = RoundMs)
    {
        var t = Clamp(elapsedMs / durationMs, 0, 1);
        var span = TotalMs[(int)species];
        var total = JsRound(Lerp(span.Open, span.Close, t));
        var rippleMs = Math.Max(80, JsRound(total * RippleRatio[(int)species]));
        var activeMs = Math.Max(80, total - rippleMs);
        return new VisibleWindow(rippleMs, activeMs);
    }

    public static int SpawnInterval(double elapsedMs, Func<double> rng, double durationMs = RoundMs)
    {
        var t = Clamp(elapsedMs / durationMs, 0, 1);
        var baseMs = Lerp(1400, 560, t * t);
        var jitter = Lerp(160, 70, t) * (rng() * 2 - 1);
        return Math.Max(280, JsRound(baseMs + jitter));
    }

    public static SpeciesId ChooseSpecies(double elapsedMs, Func<double> rng, int activeHazards, double durationMs = RoundMs)
    {
        // Opening beats are perch only, so the first taps teach the loop before hazards show up.
        if (elapsedMs * 10 < durationMs) return SpeciesId.Perch;
        var fishOnly = activeHazards >= 2;
        var table = SpeciesTable(PhaseIndex(elapsedMs, durationMs));
        var filtered = new List<(SpeciesId Item, double Weight)>(table.Length);
        foreach (var entry in table)
        {
            if (!fishOnly || IsFish(entry.Id)) filtered.Add((entry.Id, entry.Weight));
        }

        if (filtered.Count == 0) return SpeciesId.Perch;
        return PickWeighted(filtered, rng);
    }

    public static int? PickHole(State state, double elapsedMs, Func<double> rng)
    {
        var table = new List<(int Item, double Weight)>(HoleCount);
        for (var i = 0; i < HoleCount; i++)
        {
            if (state.Holes[i] is not null) continue;
            var idle = Math.Max(0, elapsedMs - state.LastUsed[i]);
            table.Add((i, 1 + idle));
        }

        if (table.Count == 0) return null;
        return PickWeighted(table, rng);
    }

    public static Actor Arm(
        State state,
        int hole,
        SpeciesId species,
        double elapsedMs,
        double durationMs = RoundMs,
        double? curveElapsed = null)
    {
        var spec = Info(species);
        var window = VisibleWindow(species, curveElapsed ?? elapsedMs, durationMs);
        var actor = new Actor
        {
            Id = state.ActorSeq,
            Hole = hole,
            Species = species,
            Kind = spec.Kind,
            Points = spec.Points,
            BornAt = elapsedMs,
            BreachAt = elapsedMs + window.RippleMs,
            HideAt = elapsedMs + window.RippleMs + window.ActiveMs,
        };
        state.ActorSeq += 1;
        state.Holes[hole] = actor;
        return actor;
    }

    public static TapResult ApplyTap(State state, int hole, double elapsedMs)
    {
        if (state.Phase != Phase.Playing) return new TapResult.Idle();
        var actor = state.Holes[hole];
        if (actor is null || actor.Resolved || elapsedMs < actor.BornAt || elapsedMs >= actor.HideAt)
        {
            return new TapResult.Whiff();
        }

        actor.Resolved = true;
        state.Holes[hole] = null;
        state.LastUsed[hole] = elapsedMs;

        if (actor.Kind == SpeciesKind.Hazard)
        {
            var comboLost = state.Combo;
            state.Combo = 0;
            var penalty = Math.Min(HazardPenalty, state.Score);
            state.Score -= penalty;
            state.HazardsHit += 1;
            return new TapResult.Hazard(actor.Species, penalty, comboLost, state.Score);
        }

        var combo = state.Combo + 1;
        var mult = Multiplier(combo);
        var total = actor.Points * mult;
        state.Combo = combo;
        if (combo > state.BestCombo) state.BestCombo = combo;
        state.Score += total;
        var fish = AsFish(actor.Species);
        state.Catches[fish] += 1;
        return new TapResult.Catch(fish, actor.Points, combo, mult, total);
    }

    public static T PickWeighted<T>(IReadOnlyList<(T Item, double Weight)> items, Func<double> rng)
    {
        double total = 0;
        foreach (var entry in items) total += entry.Weight;
        var roll = rng() * total;
        foreach (var entry in items)
        {
            roll -= entry.Weight;
            if (roll < 0) return entry.Item;
        }

        return items[^1].Item;
    }

    public static Func<double> Mulberry32(uint seed)
    {
        var a = seed;
        return () =>
        {
            a = unchecked(a + 0x6D2B79F5u);
            var signed = unchecked((int)a);
            var t = Imul(signed ^ (int)(a >> 15), 1 | signed);
            var mixed = Imul(t ^ (int)((uint)t >> 7), 61 | t);
            t = unchecked(t + mixed) ^ t;
            var bits = (uint)t ^ ((uint)t >> 14);
            return bits / 4294967296d;
        };
    }

    internal static int JsRound(double value) => (int)Math.Floor(value + 0.5);

    static int Imul(int x, int y) => unchecked((int)((long)x * y));

    static double Clamp(double value, double min, double max) => Math.Min(max, Math.Max(min, value));

    static double Lerp(double a, double b, double t) => a + (b - a) * t;
}
