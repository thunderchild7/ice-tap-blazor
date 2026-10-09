using System.Globalization;
using IceTap.Core;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Web;
using Microsoft.JSInterop;

namespace IceTap.Components;

public partial class IceBoard : IAsyncDisposable
{
    static readonly CultureInfo English = CultureInfo.GetCultureInfo("en-US");

    readonly Game game = Game.Create();
    readonly HoleVm[] holes = Enumerable.Range(0, Rules.HoleCount).Select(i => new HoleVm(i)).ToArray();
    readonly CancellationTokenSource cts = new();

    IJSInProcessObjectReference? platform;
    DotNetObjectReference<IceBoard>? dotNet;
    SaveStore? store;
    SaveData save = SaveData.CreateDefault();
    Summary? lastSummary;

    bool ready;
    bool disposed;
    bool reduced;
    bool ambient = true;
    bool showTitle = true;
    bool showResults;
    bool showPause;
    bool showEndless;
    bool showTitleRecord;
    bool showBest;
    bool showEndRun;
    bool showIosHint;
    bool installReady;
    bool otherNoteHidden;
    bool focusPlay;
    bool focusAgain;
    bool shaking;
    bool comboBreak;
    bool calloutOn;

    int fxSerial;
    int dropSerial;
    int calloutKey;
    int calloutToken;
    int shakeToken;
    int breakToken;
    int bestAtStart;

    string calloutText = "";
    string finalScoreText = "0";
    string recordLine = "";
    string roundLine = "";
    string otherModeText = "Endless";
    string titleRecord = "";
    string dockText = "No record yet";
    string pauseText = "Timer frozen. Tap to resume.";
    string live = "";

    ElementReference playButton;
    ElementReference againButton;

    [Inject]
    public IJSRuntime JS { get; set; } = default!;

    string ScoreText => Format(game.State.Score);

    bool ScoreHot => game.State.Phase == Phase.Playing && bestAtStart > 0 && game.State.Score > bestAtStart;

    string TimeText
    {
        get
        {
            if (game.State.Phase == Phase.Title) return Rules.FormatClock(game.DurationMs);
            if (game.State.Endless) return Rules.FormatClock(game.State.ElapsedMs);
            return Rules.FormatClock(Math.Max(0, game.DurationMs - game.State.ElapsedMs));
        }
    }

    string TimeClass
    {
        get
        {
            const string cls = "value time";
            if (game.State.Endless || game.State.Phase != Phase.Playing) return cls;
            var remaining = Math.Max(0, game.DurationMs - game.State.ElapsedMs);
            if (remaining <= 5_000 && remaining > 0) return $"{cls} danger";
            if (remaining <= 10_000) return $"{cls} warn";
            return cls;
        }
    }

    string ComboText
    {
        get
        {
            var combo = game.State.Combo;
            if (combo < 2) return "\u2014";
            var mult = Rules.Multiplier(combo);
            return mult > 1 ? $"{combo} \u00d7{mult}" : combo.ToString();
        }
    }

    bool ShowStrikes => game.State.Endless && game.State.Phase is Phase.Playing or Phase.Paused;

    string StrikesLabel => $"{Math.Max(0, Rules.StrikeLimit - game.State.Strikes)} snags left";

    string BoardClass => ambient
        ? shaking ? "board ambient shake" : "board ambient"
        : shaking ? "board shake" : "board";

    string SoundLabel => save.Sound ? "Sound on" : "Sound off";

    string HapticsLabel => save.Haptics ? "Haptics on" : "Haptics off";

    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            // Leave the render that called us before touching the renderer again.
            await Task.Yield();
            if (!disposed) await Boot();
            return;
        }

        if (focusPlay)
        {
            focusPlay = false;
            await playButton.FocusAsync(preventScroll: true);
        }

        if (focusAgain)
        {
            focusAgain = false;
            await againButton.FocusAsync(preventScroll: true);
        }
    }

    [JSInvokable]
    public void Frame(double time)
    {
        if (!ready || disposed || game.State.Phase != Phase.Playing) return;
        ApplyEvents(game.Tick(time));
        if (game.State.Phase == Phase.Playing) Sync();
        StateHasChanged();
    }

    [JSInvokable]
    public void PressHole(int index)
    {
        if (!ready || platform is null || game.State.Phase != Phase.Playing) return;
        if (index < 0 || index >= Rules.HoleCount) return;

        var hole = holes[index];
        hole.Tap = true;
        var tapToken = ++fxSerial;
        hole.TapToken = tapToken;
        _ = Later(120, () =>
        {
            if (hole.TapToken != tapToken) return;
            hole.Tap = false;
        });

        var result = game.Tap(index, Now());
        if (result is TapResult.Catch caught)
        {
            var rare = caught.Species is FishId.Char or FishId.Pike;
            FloatAt(hole, $"+{Format(caught.Total)}", rare || caught.Mult > 1 ? "rare" : "catch");
            Splash(hole, rare ? "rare" : "fish");
            Punch(hole, pop: true);
            Sound("catchFish", Rules.Slug(Rules.ToSpecies(caught.Species)));
            Vibrate(Buzz(caught.Species));
            if (caught.Mult > Rules.Multiplier(caught.Combo - 1))
            {
                ShowCallout($"\u00d7{caught.Mult}");
                Sound("milestone");
                Vibrate([16]);
            }
        }
        else if (result is TapResult.Hazard hazard)
        {
            FloatAt(hole, hazard.Penalty > 0 ? $"\u2212{hazard.Penalty}" : "Snap", "bad");
            Splash(hole, "hazard");
            Punch(hole, pop: false);
            _ = ShakeBoard();
            Sound("hazard");
            Vibrate([40]);
            if (game.State.Phase == Phase.Over)
            {
                var summary = game.CurrentSummary();
                if (summary is not null) ShowResults(summary);
                return;
            }
        }

        Sync();
        StateHasChanged();
    }

    [JSInvokable]
    public void KeyBegin(string mode)
    {
        var endless = mode switch
        {
            "endless" => true,
            "timed" => false,
            _ => game.State.Endless,
        };
        BeginRound(endless);
    }

    [JSInvokable]
    public void ResumeRound()
    {
        if (!ready || platform is null || game.State.Phase != Phase.Paused) return;
        platform.InvokeVoid("unlock");
        game.Resume(Now());
        SetOverlay(Overlay.None);
        Sync();
        platform.InvokeVoid("startLoop");
        StateHasChanged();
    }

    [JSInvokable]
    public void PauseFromHide() => PauseRound();

    [JSInvokable]
    public void InstallReady()
    {
        installReady = true;
        StateHasChanged();
    }

    [JSInvokable]
    public void Installed()
    {
        installReady = false;
        StateHasChanged();
    }

    public async ValueTask DisposeAsync()
    {
        if (disposed) return;
        disposed = true;
        cts.Cancel();
        if (platform is not null)
        {
            try
            {
                platform.InvokeVoid("stopLoop");
                platform.InvokeVoid("unbind");
            }
            catch (JSDisconnectedException)
            {
            }
            catch (JSException)
            {
            }

            await platform.DisposeAsync();
        }

        dotNet?.Dispose();
        cts.Dispose();
    }

    async Task Boot()
    {
        var module = await JS.InvokeAsync<IJSObjectReference>("import", "./js/platform.js");
        if (disposed)
        {
            await module.DisposeAsync();
            return;
        }

        platform = (IJSInProcessObjectReference)module;
        dotNet = DotNetObjectReference.Create(this);
        platform.InvokeVoid("bind", dotNet);
        var probe = platform.Invoke<ClientProbe>("probe");
        reduced = probe.ReducedMotion;
        store = new SaveStore(
            () => platform.Invoke<string?>("loadSave"),
            json => platform.InvokeVoid("storeSave", json));
        save = store.Load();
        platform.InvokeVoid("setSound", save.Sound);
        showEndless = save.EndlessUnlocked;
        showIosHint = probe.AppleTouch && !probe.Standalone && !save.HintDismissed;
        UpdateDock();
        UpdateTitleRecord();
        SetOverlay(Overlay.Title);
        ready = true;
        focusPlay = true;
        StateHasChanged();
    }

    void PlayTimed() => BeginRound(false);

    void PlayEndless() => BeginRound(true);

    void PlayAgain() => BeginRound(game.State.Endless);

    void PlayOther() => BeginRound(!game.State.Endless);

    void OnPointer(int index, PointerEventArgs e)
    {
        if (e.Button != 0) return;
        PressHole(index);
    }

    void Swallow()
    {
    }

    void BeginRound(bool endless)
    {
        if (!ready || platform is null) return;
        platform.InvokeVoid("unlock");
        ClearFx();
        game.Start(Now(), null, endless);
        bestAtStart = endless ? save.BestEndless : save.BestScore;
        Sound("go");
        SetOverlay(Overlay.None);
        platform.InvokeVoid("blurActive");
        live = endless ? "Endless run started." : "Round started.";
        UpdateDock();
        Sync();
        platform.InvokeVoid("startLoop");
        StateHasChanged();
    }

    void PauseRound()
    {
        if (!ready || platform is null || game.State.Phase != Phase.Playing) return;
        game.Pause(Now());
        StopLoop();
        pauseText = game.State.Endless ? "The lake is holding." : "Timer frozen. Tap to resume.";
        showEndRun = game.State.Endless;
        SetOverlay(Overlay.Pause);
        Sync();
        StateHasChanged();
    }

    void EndRun()
    {
        if (!game.State.Endless) return;
        if (game.State.Phase is not (Phase.Paused or Phase.Playing)) return;
        ShowResults(game.CashOut(Now()));
    }

    void ToggleSound()
    {
        if (!ready || platform is null || store is null) return;
        save.Sound = !save.Sound;
        platform.InvokeVoid("setSound", save.Sound);
        if (save.Sound)
        {
            Sound("unlock");
            Sound("blip");
        }

        store.Write(save);
    }

    void ToggleHaptics()
    {
        if (!ready || store is null) return;
        save.Haptics = !save.Haptics;
        store.Write(save);
        if (save.Haptics) Vibrate([16]);
    }

    void Install()
    {
        installReady = false;
        platform?.InvokeVoid("promptInstall");
    }

    void DismissHint()
    {
        save.HintDismissed = true;
        store?.Write(save);
        showIosHint = false;
    }

    void ApplyEvents(List<RoundEvent> events)
    {
        var broke = 0;
        foreach (var ev in events)
        {
            switch (ev)
            {
                case RoundEvent.Dodge dodge:
                    FloatAt(holes[dodge.Hole], $"+{dodge.Bonus}", "dodge");
                    Sound("dodge");
                    break;
                case RoundEvent.Escape escape:
                    broke = Math.Max(broke, escape.ComboLost);
                    break;
                case RoundEvent.Over over:
                    ShowResults(over.Summary);
                    break;
            }
        }

        if (broke >= 3) _ = BreakCombo(broke);
    }

    void ShowResults(Summary summary)
    {
        StopLoop();
        var previous = summary.Endless ? save.BestEndless : save.BestScore;
        var isNew = summary.Score > previous;
        if (summary.Endless) save.BestEndless = Math.Max(save.BestEndless, summary.Score);
        else save.BestScore = Math.Max(save.BestScore, summary.Score);
        save.BestCombo = Math.Max(save.BestCombo, summary.BestCombo);
        save.Games += 1;
        save.EndlessUnlocked = true;
        foreach (var id in Rules.FishIds) save.Catches[id] += summary.Catches[id];
        store?.Write(save);

        finalScoreText = Format(summary.Score);
        showBest = isNew;
        recordLine = summary.Endless
            ? save.BestEndless > 0 ? $"Endless best {Format(save.BestEndless)}" : "No endless record yet"
            : $"Best {Format(save.BestScore)}";
        lastSummary = summary;
        var stats = $"Combo {summary.BestCombo} · Dodged {summary.Dodged} · Snagged {summary.HazardsHit}";
        roundLine = summary.Endless ? $"{Rules.FormatClock(summary.ElapsedMs)} · {stats}" : stats;
        otherModeText = summary.Endless ? "60-second cast" : "Endless";
        otherNoteHidden = summary.Endless;
        showEndless = true;
        SetOverlay(Overlay.Results);
        UpdateDock();
        UpdateTitleRecord();
        calloutOn = false;
        live = isNew
            ? $"Time. Score {Format(summary.Score)}. New best."
            : $"Time. Score {Format(summary.Score)}.";
        Sound("over");
        Sync();
        focusAgain = true;
        StateHasChanged();
    }

    void Sync() => SyncHoles();

    void SyncHoles()
    {
        var elapsed = game.State.ElapsedMs;
        for (var i = 0; i < Rules.HoleCount; i++)
        {
            var vm = holes[i];
            if (vm.FxToken != 0) continue;
            var actor = game.State.Holes[i];
            if (actor is null)
            {
                if (vm.ActorId is not null || vm.Phase.Length > 0) vm.ClearActor();
                continue;
            }

            var phase = elapsed < actor.BreachAt ? "ripple" : "up";
            var sig = $"{actor.Id}:{phase}";
            if (vm.Sig == sig) continue;
            vm.Sig = sig;
            if (vm.ActorId != actor.Id)
            {
                vm.ActorId = actor.Id;
                vm.Kind = actor.Kind == SpeciesKind.Hazard ? "hazard" : "fish";
                vm.SpeciesCss = Rules.Slug(actor.Species);
                vm.Sprite = Critters.Sprite(actor.Species);
            }

            vm.Phase = phase;
            var name = actor.Kind == SpeciesKind.Hazard ? "hazard" : Rules.Info(actor.Species).Name;
            vm.Aria = $"Hole {i + 1}, {name}";
        }
    }

    void SetOverlay(Overlay which)
    {
        showTitle = which == Overlay.Title;
        showResults = which == Overlay.Results;
        showPause = which == Overlay.Pause;
        ambient = which == Overlay.Title;
        PublishPhase();
    }

    void PublishPhase()
    {
        platform?.InvokeVoid("setPhase", game.State.Phase switch
        {
            Phase.Title => "title",
            Phase.Playing => "playing",
            Phase.Paused => "paused",
            _ => "over",
        });
    }

    void UpdateDock()
    {
        var endless = game.State.Endless && game.State.Phase != Phase.Title;
        var best = endless ? save.BestEndless : save.BestScore;
        var label = endless ? "Endless best" : "Best";
        dockText = best > 0
            ? $"{label} {Format(best)}"
            : endless ? "No endless record" : "No record yet";
    }

    void UpdateTitleRecord()
    {
        var total = save.Catches.Total;
        if (save.Games == 0 && save.BestScore <= 0 && save.BestEndless <= 0)
        {
            showTitleRecord = false;
            return;
        }

        showTitleRecord = true;
        var parts = new List<string>();
        if (save.BestScore > 0 || total > 0)
        {
            parts.Add(total > 0
                ? $"Best {Format(save.BestScore)} · {total} fish"
                : $"Best {Format(save.BestScore)}");
        }

        if (save.BestEndless > 0) parts.Add($"Endless {Format(save.BestEndless)}");
        titleRecord = string.Join(" · ", parts);
    }

    void FloatAt(HoleVm hole, string text, string kind)
    {
        var token = ++fxSerial;
        hole.FloatToken = token;
        hole.FloatKey = token;
        hole.FloaterText = text;
        hole.FloaterClass = $"floater show {kind}";
        _ = Later(680, () =>
        {
            if (hole.FloatToken != token) return;
            hole.FloaterClass = "floater";
            hole.FloaterText = "";
        });
    }

    void Punch(HoleVm hole, bool pop)
    {
        var token = ++fxSerial;
        hole.FxToken = token;
        hole.Pop = pop;
        hole.Hurt = !pop;
        _ = Later(200, () =>
        {
            if (hole.FxToken != token) return;
            hole.Pop = false;
            hole.Hurt = false;
            hole.FxToken = 0;
            SyncHoles();
        });
    }

    void Splash(HoleVm hole, string kind)
    {
        if (reduced) return;
        var ids = new int[6];
        for (var i = 0; i < ids.Length; i++)
        {
            var id = ++dropSerial;
            ids[i] = id;
            var angle = (i * 60 + Random.Shared.NextDouble() * 16).ToString(CultureInfo.InvariantCulture);
            var dist = (16 + Random.Shared.NextDouble() * 18).ToString(CultureInfo.InvariantCulture);
            hole.Drops.Add(new DropVm($"drop-{id}", kind, $"--a:{angle}deg;--d:{dist}px"));
        }

        _ = Later(450, () =>
        {
            var keys = ids.Select(id => $"drop-{id}").ToHashSet();
            hole.Drops.RemoveAll(drop => keys.Contains(drop.Key));
        });
    }

    void ShowCallout(string text)
    {
        var token = ++fxSerial;
        calloutToken = token;
        calloutKey = token;
        calloutText = text;
        calloutOn = true;
        _ = Later(720, () =>
        {
            if (calloutToken != token) return;
            calloutOn = false;
        });
    }

    async Task ShakeBoard()
    {
        if (reduced || disposed) return;
        var token = ++shakeToken;
        await Task.Yield();
        if (shakeToken != token || disposed) return;
        shaking = false;
        StateHasChanged();
        await Task.Yield();
        if (shakeToken != token || disposed) return;
        shaking = true;
        StateHasChanged();
        try
        {
            await Task.Delay(180, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (shakeToken != token || disposed) return;
        shaking = false;
        await InvokeAsync(StateHasChanged);
    }

    async Task BreakCombo(int broke)
    {
        var token = ++breakToken;
        await Task.Yield();
        if (breakToken != token || disposed) return;
        if (comboBreak)
        {
            comboBreak = false;
            StateHasChanged();
            await Task.Yield();
            if (breakToken != token || disposed) return;
        }

        comboBreak = true;
        Sound("escape", broke >= 5);
        StateHasChanged();
        try
        {
            await Task.Delay(240, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (breakToken != token || disposed) return;
        comboBreak = false;
        await InvokeAsync(StateHasChanged);
    }

    void ClearFx()
    {
        fxSerial++;
        calloutToken = 0;
        calloutOn = false;
        shakeToken++;
        breakToken++;
        shaking = false;
        comboBreak = false;
        foreach (var hole in holes) hole.Reset();
    }

    async Task Later(int ms, Action action)
    {
        try
        {
            await Task.Delay(ms, cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (disposed) return;
        try
        {
            await InvokeAsync(() =>
            {
                if (disposed) return;
                action();
                StateHasChanged();
            });
        }
        catch (ObjectDisposedException)
        {
        }
    }

    void Sound(string name, object? arg = null)
    {
        if (platform is null) return;
        if (arg is bool include && !include) return;
        try
        {
            if (arg is null || arg is bool) platform.InvokeVoid(name);
            else platform.InvokeVoid(name, arg);
        }
        catch (JSException)
        {
            // A rejected audio graph should not drop the catch.
        }
    }

    void StopLoop() => platform?.InvokeVoid("stopLoop");

    double Now() => platform?.Invoke<double>("now") ?? 0;

    void Vibrate(int[] pattern)
    {
        if (!save.Haptics || platform is null) return;
        platform.InvokeVoid("vibrate", pattern);
    }

    int Caught(FishId id) => lastSummary?.Catches[id] ?? 0;

    bool PipSpent(int index) => index < game.State.Strikes;

    static string Format(int score) => score.ToString("N0", English);

    static int[] Buzz(FishId species) => species switch
    {
        FishId.Pike => [12, 24, 14, 24, 20],
        FishId.Char => [10, 28, 16],
        _ => [12],
    };

    enum Overlay
    {
        Title,
        Results,
        Pause,
        None,
    }

    sealed class ClientProbe
    {
        public bool AppleTouch { get; set; }
        public bool Standalone { get; set; }
        public bool ReducedMotion { get; set; }
    }

    sealed class HoleVm(int index)
    {
        public int Index { get; } = index;
        public int? ActorId { get; set; }
        public string Sig { get; set; } = "";
        public string Phase { get; set; } = "";
        public string Kind { get; set; } = "";
        public string SpeciesCss { get; set; } = "";
        public MarkupString Sprite { get; set; }
        public bool Tap { get; set; }
        public bool Pop { get; set; }
        public bool Hurt { get; set; }
        public int FxToken { get; set; }
        public int TapToken { get; set; }
        public int FloatToken { get; set; }
        public int FloatKey { get; set; }
        public string FloaterClass { get; set; } = "floater";
        public string FloaterText { get; set; } = "";
        public string Aria { get; set; } = $"Hole {index + 1}";
        public List<DropVm> Drops { get; } = [];

        public string ClassName
        {
            get
            {
                var cls = "hole";
                if (Phase.Length > 0) cls += $" is-{Phase}";
                if (Kind.Length > 0) cls += $" is-{Kind}";
                if (SpeciesCss.Length > 0) cls += $" is-{SpeciesCss}";
                if (Tap) cls += " is-tap";
                if (Pop) cls += " is-pop";
                if (Hurt) cls += " is-hurt";
                return cls;
            }
        }

        public void ClearActor()
        {
            ActorId = null;
            Sig = "";
            Phase = "";
            Kind = "";
            SpeciesCss = "";
            Sprite = default;
            Pop = false;
            Hurt = false;
            Aria = $"Hole {Index + 1}";
        }

        public void Reset()
        {
            ClearActor();
            Tap = false;
            FxToken = 0;
            TapToken = 0;
            FloatToken = 0;
            FloaterClass = "floater";
            FloaterText = "";
            Drops.Clear();
        }
    }

    sealed record DropVm(string Key, string Kind, string Style);
}
