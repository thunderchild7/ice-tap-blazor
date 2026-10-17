namespace IceTap.Core.Tests;

public class LogicTests
{
    [Fact]
    public void Multiplier_steps_every_five_catches_and_caps_at_five()
    {
        Assert.Equal(1, Rules.Multiplier(1));
        Assert.Equal(1, Rules.Multiplier(4));
        Assert.Equal(2, Rules.Multiplier(5));
        Assert.Equal(2, Rules.Multiplier(9));
        Assert.Equal(3, Rules.Multiplier(10));
        Assert.Equal(3, Rules.Multiplier(14));
        Assert.Equal(4, Rules.Multiplier(15));
        Assert.Equal(4, Rules.Multiplier(19));
        Assert.Equal(5, Rules.Multiplier(20));
        Assert.Equal(5, Rules.Multiplier(40));
    }

    [Fact]
    public void Clock_reads_in_minutes_and_rounds_up_the_visible_second()
    {
        Assert.Equal("1:00", Rules.FormatClock(60_000));
        Assert.Equal("0:59", Rules.FormatClock(59_000));
        Assert.Equal("0:02", Rules.FormatClock(1_500));
        Assert.Equal("0:01", Rules.FormatClock(1));
        Assert.Equal("0:00", Rules.FormatClock(0));
    }

    [Fact]
    public void Difficulty_opens_across_the_minute()
    {
        Assert.Equal(1, Rules.MaxConcurrent(0));
        Assert.Equal(1, Rules.MaxConcurrent(11_999));
        Assert.Equal(2, Rules.MaxConcurrent(12_000));
        Assert.Equal(2, Rules.MaxConcurrent(31_999));
        Assert.Equal(3, Rules.MaxConcurrent(32_000));
        Assert.Equal(3, Rules.MaxConcurrent(47_999));
        Assert.Equal(4, Rules.MaxConcurrent(48_000));
        Assert.Equal(0, Rules.PhaseIndex(0));
        Assert.Equal(1, Rules.PhaseIndex(15_000));
        Assert.Equal(2, Rules.PhaseIndex(30_000));
        Assert.Equal(3, Rules.PhaseIndex(45_000));

        var early = Rules.SpeciesTable(0).Select(entry => entry.Id).ToArray();
        Assert.DoesNotContain(SpeciesId.Pike, early);
        Assert.DoesNotContain(SpeciesId.Char, early);
        var late = Rules.SpeciesTable(3).Select(entry => entry.Id).ToArray();
        Assert.Contains(SpeciesId.Pike, late);
        Assert.Contains(SpeciesId.Boot, late);
        Assert.All(late, id => Assert.True(id == SpeciesId.Boot || Rules.IsFish(id)));

        var open = Rules.VisibleWindow(SpeciesId.Perch, 0);
        var close = Rules.VisibleWindow(SpeciesId.Perch, Rules.RoundMs);
        Assert.True(open.RippleMs + open.ActiveMs > close.RippleMs + close.ActiveMs);
        Assert.True(Rules.SpawnInterval(0, () => 0.5) > Rules.SpawnInterval(Rules.RoundMs, () => 0.5));
    }

    [Fact]
    public void Opening_is_perch_only_and_two_live_hazards_block_a_third()
    {
        var rng = Rules.Mulberry32(4);
        for (var i = 0; i < 12; i++) Assert.Equal(SpeciesId.Perch, Rules.ChooseSpecies(1_000, rng, 0));
        Assert.Equal(SpeciesId.Boot, Rules.ChooseSpecies(10_000, () => 0.999, 0));
        Assert.Equal(SpeciesId.Boot, Rules.ChooseSpecies(50_000, () => 0.999, 0));
        for (var i = 0; i < 30; i++)
        {
            var species = Rules.ChooseSpecies(50_000, rng, 2);
            Assert.Equal(SpeciesKind.Fish, Rules.Info(species).Kind);
        }
    }

    [Fact]
    public void Spawn_picks_an_open_hole_and_skips_a_full_lake()
    {
        var game = Game.Create(1);
        game.Start(0);
        for (var i = 0; i < 9; i++) game.State.Holes[i] = null;
        for (var i = 1; i < 9; i++) game.State.Holes[i] = Fish(i, i);
        Assert.Equal(0, Rules.PickHole(game.State, 1_000, Rules.Mulberry32(1)));

        game.State.Holes = game.State.Holes.Select(_ => (Actor?)null).ToArray();
        Assert.NotNull(Rules.PickHole(game.State, 1_000, Rules.Mulberry32(1)));
        for (var i = 0; i < 9; i++)
        {
            game.State.Holes[i] = new Actor
            {
                Id = 100 + i,
                Hole = i,
                Species = SpeciesId.Boot,
                Kind = SpeciesKind.Hazard,
                Points = 0,
                BornAt = 0,
                BreachAt = 10,
                HideAt = 500,
            };
        }

        Assert.Null(Rules.PickHole(game.State, 1_000, Rules.Mulberry32(2)));
    }

    [Fact]
    public void Fifth_catch_pays_double_and_a_hazard_snaps_the_chain_without_going_negative()
    {
        var game = Game.Create(1);
        game.Start(0);
        game.State.NextSpawnAt = 1_000_000;
        game.State.Combo = 4;
        Rules.Arm(game.State, 0, SpeciesId.Perch, 0);
        var caught = Assert.IsType<TapResult.Catch>(game.Tap(0, 100));
        Assert.Equal(2, caught.Mult);
        Assert.Equal(200, caught.Total);
        Assert.Equal(5, caught.Combo);
        Assert.Equal(200, game.State.Score);
        Assert.Equal(5, game.State.BestCombo);

        Rules.Arm(game.State, 1, SpeciesId.Boot, 0);
        var snag = Assert.IsType<TapResult.Hazard>(game.Tap(1, 150));
        Assert.Equal(200, snag.Penalty);
        Assert.Equal(0, game.State.Score);
        Assert.Equal(0, game.State.Combo);
        Assert.Equal(5, game.State.BestCombo);
        Assert.IsType<TapResult.Whiff>(game.Tap(1, 160));

        game.State.Score = 40;
        Rules.Arm(game.State, 2, SpeciesId.Boot, 0);
        var tiny = Assert.IsType<TapResult.Hazard>(game.Tap(2, 180));
        Assert.Equal(40, tiny.Penalty);
        Assert.Equal(0, game.State.Score);
    }

    [Fact]
    public void Letting_a_hazard_sink_pays_the_dodge_and_keeps_the_combo()
    {
        var game = Game.Create(1);
        game.Start(0);
        game.State.NextSpawnAt = 1_000_000;
        game.State.Combo = 4;
        game.State.Score = 500;
        var actor = Rules.Arm(game.State, 3, SpeciesId.Boot, 0);
        for (var t = 50; t <= actor.HideAt + 100; t += 50) game.Tick(t);
        Assert.Equal(1, game.State.Dodged);
        Assert.Equal(550, game.State.Score);
        Assert.Equal(4, game.State.Combo);
        Assert.Null(game.State.Holes[3]);
        Assert.Equal(0, game.State.HazardsHit);
    }

    [Fact]
    public void Missed_fish_breaks_the_combo()
    {
        var game = Game.Create(1);
        game.Start(0);
        game.State.NextSpawnAt = 1_000_000;
        game.State.Combo = 6;
        game.State.BestCombo = 6;
        var actor = Rules.Arm(game.State, 4, SpeciesId.Trout, 0);
        for (var t = 50; t <= actor.HideAt + 80; t += 50) game.Tick(t);
        Assert.Equal(1, game.State.Escaped);
        Assert.Equal(0, game.State.Combo);
        Assert.Equal(6, game.State.BestCombo);
    }

    [Fact]
    public void Pausing_freezes_the_holes_and_the_clock()
    {
        var game = Game.Create(1);
        game.Start(0);
        game.State.NextSpawnAt = 1_000_000;
        var actor = Rules.Arm(game.State, 0, SpeciesId.Trout, 0);
        game.State.Combo = 6;
        for (var t = 100; t <= 400; t += 100) game.Tick(t);
        Assert.Equal(400, game.State.ElapsedMs);
        Assert.Equal(actor.Id, game.State.Holes[0]?.Id);
        game.Pause(400);
        game.Tick(3_000);
        Assert.Equal(Phase.Paused, game.State.Phase);
        Assert.Equal(400, game.State.ElapsedMs);
        Assert.Equal(actor.Id, game.State.Holes[0]?.Id);
        game.Resume(3_000);
        for (var t = 3_100; t <= 4_000; t += 100) game.Tick(t);
        Assert.Equal(Phase.Playing, game.State.Phase);
        Assert.Equal(1_400, game.State.ElapsedMs);
        Assert.Equal(1, game.State.Escaped);
        Assert.Equal(0, game.State.Combo);
    }

    [Fact]
    public void Stalled_frame_does_not_burn_the_round()
    {
        var game = Game.Create(1);
        game.Start(0);
        game.Tick(20);
        Assert.Equal(20, game.State.ElapsedMs);
        game.Tick(2_000);
        Assert.True(game.State.ElapsedMs < 80);
        Assert.Equal(Phase.Playing, game.State.Phase);
    }

    [Fact]
    public void Endless_holds_the_opening_pace()
    {
        var game = Game.Create(5);
        game.Start(0, 5, true);
        var seen = new Dictionary<int, (double At, int Life, SpeciesId Species)>();
        var maxSeen = 0;
        for (var t = 50; t <= 75_000; t += 50)
        {
            game.Tick(t);
            var active = 0;
            foreach (var hole in game.State.Holes)
            {
                if (hole is null) continue;
                active += 1;
                seen[hole.Id] = (hole.BornAt, (int)(hole.HideAt - hole.BornAt), hole.Species);
            }

            if (active > maxSeen) maxSeen = active;
        }

        Assert.Equal(Phase.Playing, game.State.Phase);
        Assert.Equal(1, maxSeen);
        var late = seen.Values.Where(entry => entry.At >= 65_000).OrderBy(entry => entry.At).ToArray();
        Assert.True(late.Length >= 2);
        var gap = late[1].At - late[0].At;
        Assert.InRange(gap, 1_100, 1_700);
        var opening = Rules.VisibleWindow(late[0].Species, 0);
        Assert.Equal(opening.RippleMs + opening.ActiveMs, late[0].Life);
    }

    [Fact]
    public void Endless_keeps_going_until_the_third_snag()
    {
        var game = Game.Create(2);
        game.Start(0, 2, true);
        game.State.NextSpawnAt = 1_000_000;
        for (var t = 100; t <= 70_000; t += 100) game.Tick(t);
        Assert.Equal(Phase.Playing, game.State.Phase);
        Assert.Equal(70_000, game.State.ElapsedMs);
        Assert.Equal(0, game.State.Strikes);

        var dodge = Rules.Arm(game.State, 4, SpeciesId.Boot, 70_000);
        for (var t = 70_100d; t <= dodge.HideAt + 50; t += 50) game.Tick(t);
        Assert.Equal(0, game.State.Strikes);
        Assert.Equal(Phase.Playing, game.State.Phase);

        for (var n = 0; n < 2; n++)
        {
            var snagAt = game.State.ElapsedMs;
            Rules.Arm(game.State, n, SpeciesId.Boot, snagAt);
            Assert.IsType<TapResult.Hazard>(game.Tap(n, snagAt));
            Assert.Equal(Phase.Playing, game.State.Phase);
        }

        var lastAt = game.State.ElapsedMs;
        Rules.Arm(game.State, 2, SpeciesId.Boot, lastAt);
        Assert.IsType<TapResult.Hazard>(game.Tap(2, lastAt));
        Assert.Equal(3, game.State.Strikes);
        Assert.Equal(Phase.Over, game.State.Phase);
        Assert.True(game.CurrentSummary()?.Endless);
    }

    [Fact]
    public void Cashing_out_an_endless_run_keeps_the_score()
    {
        var game = Game.Create(1);
        game.Start(0, 1, true);
        game.State.NextSpawnAt = 1_000_000;
        Rules.Arm(game.State, 0, SpeciesId.Pike, 0);
        Assert.IsType<TapResult.Catch>(game.Tap(0, 100));
        game.Pause(400);
        var summary = game.CashOut(900);
        Assert.True(summary.Endless);
        Assert.Equal(1000, summary.Score);
        Assert.Equal(400, summary.ElapsedMs);
        Assert.Equal(Phase.Over, game.State.Phase);
    }

    [Fact]
    public void Round_ends_on_the_clock()
    {
        var game = Game.Create(2, 1_000);
        game.Start(0);
        for (var t = 50; t <= 1_500; t += 50) game.Tick(t);
        Assert.Equal(Phase.Over, game.State.Phase);
        Assert.Equal(1_000, game.State.ElapsedMs);
    }

    [Fact]
    public void Reading_the_lake_beats_mashing_and_the_same_seed_replays()
    {
        var calmTotal = 0;
        var slowTotal = 0;
        var mashTotal = 0;
        var sawHazardGap = false;
        Run? seed7 = null;

        for (uint seed = 1; seed <= 12; seed++)
        {
            var calm = Play(seed, Style.Calm);
            var slow = Play(seed, Style.Slow);
            var mash = Play(seed, Style.Mash);
            Assert.Equal(Phase.Over, calm.Phase);
            Assert.Equal(Phase.Over, slow.Phase);
            Assert.Equal(Phase.Over, mash.Phase);
            Assert.True(calm.Score >= slow.Score, $"seed {seed} calm {calm.Score} slow {slow.Score}");
            Assert.True(calm.Score >= mash.Score, $"seed {seed} calm {calm.Score} mash {mash.Score}");
            Assert.True(mash.Score >= 0);
            calmTotal += calm.Score;
            slowTotal += slow.Score;
            mashTotal += mash.Score;
            if (mash.HazardsHit > 0 && calm.Score > mash.Score) sawHazardGap = true;
            if (seed == 7) seed7 = calm;
        }

        Assert.True(calmTotal > slowTotal);
        Assert.True(calmTotal > mashTotal);
        Assert.True(sawHazardGap);
        Assert.True(calmTotal > 12_000);
        Assert.True(calmTotal < 5_000_000);
        Assert.NotNull(seed7);
        Assert.Equal(79_650, seed7.Score);
        Assert.Equal(18, seed7.Catches.Perch);
        Assert.Equal(13, seed7.Catches.Trout);
        Assert.Equal(6, seed7.Catches.Char);
        Assert.Equal(9, seed7.Catches.Pike);
        var replay = Play(7, Style.Calm);
        Assert.Equal(seed7.Score, replay.Score);
        Assert.Equal(seed7.Catches, replay.Catches);
    }

    [Fact]
    public void Mulberry32_matches_the_javascript_sequence()
    {
        AssertSequence(1, 0.627073940588, 0.002735721180, 0.527447039960, 0.981050967472, 0.968377898214, 0.281103502959);
        AssertSequence(4, 0.923636197113, 0.333045879379, 0.221667900914, 0.081841019914, 0.233997242758, 0.508496854221);
        AssertSequence(7, 0.011704753153, 0.061958257575, 0.976907632779, 0.699028705712, 0.521445268532, 0.405521688052);
    }

    [Fact]
    public void Save_keeps_partial_records_and_unlocks_endless_after_a_game()
    {
        var save = SaveData.Parse("""{"games":2,"bestScore":10,"catches":{"perch":3,"trout":1.9,"nope":4},"hintDismissed":true}""");
        Assert.Equal(2, save.Games);
        Assert.Equal(10, save.BestScore);
        Assert.True(save.EndlessUnlocked);
        Assert.True(save.Sound);
        Assert.Equal(3, save.Catches.Perch);
        Assert.Equal(1, save.Catches.Trout);
        Assert.True(save.HintDismissed);

        var negative = SaveData.Parse("""{"bestScore":-5,"sound":false}""");
        Assert.Equal(0, negative.BestScore);
        Assert.False(negative.Sound);
        Assert.False(negative.EndlessUnlocked);

        var roundTrip = SaveData.Parse(save.ToJson());
        Assert.Equal(save.Catches, roundTrip.Catches);
        Assert.Equal(save.Games, roundTrip.Games);
    }

    static void AssertSequence(uint seed, params double[] expected)
    {
        var rng = Rules.Mulberry32(seed);
        foreach (var value in expected) Assert.Equal(value, rng(), 9);
    }

    static Actor Fish(int id, int hole) => new()
    {
        Id = id,
        Hole = hole,
        Species = SpeciesId.Perch,
        Kind = SpeciesKind.Fish,
        Points = 100,
        BornAt = 0,
        BreachAt = 10,
        HideAt = 500,
    };

    enum Style
    {
        Calm,
        Slow,
        Mash,
    }

    sealed record Run(int Score, Phase Phase, int HazardsHit, CatchTally Catches);

    static Run Play(uint seed, Style style)
    {
        var game = Game.Create(seed);
        game.Start(0);
        for (var t = 50; t <= Rules.RoundMs + 200; t += 50)
        {
            game.Tick(t);
            if (game.State.Phase != Phase.Playing) break;
            for (var hole = 0; hole < 9; hole++)
            {
                var actor = game.State.Holes[hole];
                if (actor is null) continue;
                if (actor.Kind == SpeciesKind.Hazard)
                {
                    if (style == Style.Mash) game.Tap(hole, t);
                    continue;
                }

                if (style == Style.Slow && t < actor.BornAt + 700) continue;
                game.Tap(hole, t);
            }
        }

        return new Run(game.State.Score, game.State.Phase, game.State.HazardsHit, game.State.Catches.Clone());
    }
}
