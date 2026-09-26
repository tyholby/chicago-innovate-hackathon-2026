using FamilyStudio.Core.Codex;
using FamilyStudio.Core.Pipeline;

namespace FamilyStudio.Core.Tests;

public class ProgressTests
{
    private static StageProgress P(string stage, StagePhase phase, int seconds, int characters = 0, int counted = 0, string? note = null) =>
        new(stage, stage + "-id", phase, TimeSpan.FromSeconds(seconds), characters, counted, note);

    [Fact]
    public void One_stage_reads_as_a_sentence()
    {
        Assert.Equal("Thinking (2:15)", StageProgressText.Describe(new[] { P("brief", StagePhase.Thinking, 135) }, _ => null));
        Assert.Equal("Writing, 23 parts so far (4:10)", StageProgressText.Describe(new[] { P("recipe-a1", StagePhase.Writing, 250, 9000, 23) }, _ => null));
        Assert.Equal("Writing, 1.2 KB so far (0:32)", StageProgressText.Describe(new[] { P("brief", StagePhase.Writing, 32, 1229) }, _ => null));
        Assert.Equal("Retrying...", StageProgressText.Describe(new[] { P("brief", StagePhase.Thinking, 5, note: "Retrying...") }, _ => null));
    }

    [Fact]
    public void Several_stages_read_as_a_list_by_item()
    {
        var names = new Dictionary<string, string> { ["recipe-a1"] = "Desk", ["recipe-a2"] = "Chair" };
        var text = StageProgressText.Describe(new[] { P("recipe-a1", StagePhase.Writing, 130, 4000, 12), P("recipe-a2", StagePhase.Thinking, 65) },
            name => names.GetValueOrDefault(name));
        Assert.Equal("Desk: writing 12 parts  ·  Chair: thinking 1:05", text);
    }

    [Fact]
    public void Streamed_keys_are_counted_even_when_split_across_pieces()
    {
        var pieces = new[] { "{\"parts\":[{\"sha", "pe\":\"box\"},{\"shape\":\"tu", "be\"},{\"name\":\"shape\",\"sh", "ape\":\"sphere\"}]}" };
        var (total, carry) = (0, "");
        foreach (var piece in pieces)
        {
            var (found, next) = StreamedKeyCounter.Count(carry, piece, "shape");
            total += found;
            carry = next;
        }
        Assert.Equal(3, total); // "shape" as a value is not a key
    }
}
