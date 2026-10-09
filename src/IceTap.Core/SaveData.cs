using System.Text.Json;

namespace IceTap.Core;

public sealed class SaveData
{
    public int BestScore { get; set; }
    public int BestCombo { get; set; }
    public int Games { get; set; }
    public CatchTally Catches { get; set; } = Rules.EmptyCatches();
    public bool Sound { get; set; } = true;
    public bool Haptics { get; set; } = true;
    public bool HintDismissed { get; set; }
    public int BestEndless { get; set; }
    public bool EndlessUnlocked { get; set; }

    public static SaveData CreateDefault() => new();

    public SaveData Clone() => new()
    {
        BestScore = BestScore,
        BestCombo = BestCombo,
        Games = Games,
        Catches = Catches.Clone(),
        Sound = Sound,
        Haptics = Haptics,
        HintDismissed = HintDismissed,
        BestEndless = BestEndless,
        EndlessUnlocked = EndlessUnlocked,
    };

    public static SaveData Parse(string? raw)
    {
        var save = CreateDefault();
        if (string.IsNullOrWhiteSpace(raw)) return save;
        try
        {
            using var doc = JsonDocument.Parse(raw);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) return save;
            var root = doc.RootElement;
            save.BestScore = Count(root, "bestScore");
            save.BestCombo = Count(root, "bestCombo");
            save.Games = Count(root, "games");
            save.BestEndless = Count(root, "bestEndless");
            var unlocked = root.TryGetProperty("endlessUnlocked", out var flag) && flag.ValueKind == JsonValueKind.True;
            save.EndlessUnlocked = unlocked || save.Games > 0;
            if (root.TryGetProperty("sound", out var sound) && sound.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                save.Sound = sound.GetBoolean();
            }

            if (root.TryGetProperty("haptics", out var haptics) && haptics.ValueKind is JsonValueKind.True or JsonValueKind.False)
            {
                save.Haptics = haptics.GetBoolean();
            }

            save.HintDismissed = root.TryGetProperty("hintDismissed", out var hint) && hint.ValueKind == JsonValueKind.True;
            if (root.TryGetProperty("catches", out var catches) && catches.ValueKind == JsonValueKind.Object)
            {
                foreach (var id in Rules.FishIds)
                {
                    save.Catches[id] = Count(catches, Rules.Slug(Rules.ToSpecies(id)));
                }
            }
        }
        catch (JsonException)
        {
            return CreateDefault();
        }

        return save;
    }

    public string ToJson()
    {
        return JsonSerializer.Serialize(new
        {
            bestScore = BestScore,
            bestCombo = BestCombo,
            games = Games,
            catches = new Dictionary<string, int>
            {
                ["perch"] = Catches.Perch,
                ["trout"] = Catches.Trout,
                ["char"] = Catches.Char,
                ["pike"] = Catches.Pike,
            },
            sound = Sound,
            haptics = Haptics,
            hintDismissed = HintDismissed,
            bestEndless = BestEndless,
            endlessUnlocked = EndlessUnlocked,
        });
    }

    static int Count(JsonElement obj, string name)
    {
        if (!obj.TryGetProperty(name, out var value) || value.ValueKind != JsonValueKind.Number) return 0;
        if (!value.TryGetDouble(out var number) || !double.IsFinite(number) || number < 0) return 0;
        return (int)Math.Floor(number);
    }
}

public sealed class SaveStore
{
    readonly Func<string?> read;
    readonly Action<string> write;
    SaveData? memory;

    public SaveStore(Func<string?> read, Action<string> write)
    {
        this.read = read;
        this.write = write;
    }

    public SaveData Load()
    {
        if (memory is not null) return memory.Clone();
        try
        {
            return SaveData.Parse(read());
        }
        catch
        {
            return memory is null ? SaveData.CreateDefault() : memory.Clone();
        }
    }

    public void Write(SaveData data)
    {
        memory = data.Clone();
        try
        {
            write(memory.ToJson());
        }
        catch
        {
            // Private browsing can reject storage. The in-memory copy still covers this session.
        }
    }
}
