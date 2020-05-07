using Pusher.Core.Models;
using Pusher.Core.Scripting;

namespace Pusher.Tests;

/// <summary>Planner determinism + calendar invariants (plan/05 §9, plan/03 §2).</summary>
public class PlannerTests
{
    private static readonly DateOnly Today = new(2026, 8, 4);

    private static readonly string[] Files =
    [
        "README.md", ".gitignore", "src/App.cs", "src/Util.cs", "tests/AppTests.cs", "docs/notes.md",
    ];

    private const string Script = """
        pushscript 1.0

        repo {
            remote = "https://github.com/user/proj.git"
            identity = { name = "Dev", email = "dev@example.com" }
        }

        split {
            commit "Setup" { files = ["README*", ".gitignore"] }
            commit "Source" { files = ["src/**"] }
            commit "Tests and docs" { files = ["**"] }
        }

        schedule {
            start = today
            per_day = random(1, 2)
            active_hours = 09:00 .. 22:00
        }
        """;

    [Fact]
    public void SameSeed_SamePlan()
    {
        var a = PushScriptEngine.Plan(Script, Files, seed: 12345, today: Today);
        var b = PushScriptEngine.Plan(Script, Files, seed: 12345, today: Today);

        Assert.True(a.Success, string.Join("\n", a.Diagnostics));
        Assert.Equal(a.Commits.Count, b.Commits.Count);
        for (int i = 0; i < a.Commits.Count; i++)
        {
            Assert.Equal(a.Commits[i].ScheduledAt, b.Commits[i].ScheduledAt);
            Assert.Equal(a.Commits[i].Message, b.Commits[i].Message);
        }
    }

    [Fact]
    public void DifferentSeed_DifferentTimes()
    {
        var a = PushScriptEngine.Plan(Script, Files, seed: 1, today: Today);
        var b = PushScriptEngine.Plan(Script, Files, seed: 2, today: Today);
        Assert.True(a.Success && b.Success);
        Assert.NotEqual(
            a.Commits.Select(c => c.ScheduledAt).ToList(),
            b.Commits.Select(c => c.ScheduledAt).ToList());
    }

    [Fact]
    public void Dates_AscendMonotonically()
    {
        var plan = PushScriptEngine.Plan(Script, Files, seed: 7, today: Today);
        Assert.True(plan.Success);
        for (int i = 1; i < plan.Commits.Count; i++)
            Assert.True(plan.Commits[i].ScheduledAt > plan.Commits[i - 1].ScheduledAt,
                $"commit {i} not after commit {i - 1}");
    }

    [Fact]
    public void EveryFile_AssignedExactlyOnce()
    {
        var plan = PushScriptEngine.Plan(Script, Files, seed: 7, today: Today);
        Assert.True(plan.Success);
        var all = plan.Commits.SelectMany(c => c.Files).ToList();
        Assert.Equal(Files.Length, all.Count);
        Assert.Equal(Files.OrderBy(f => f), all.OrderBy(f => f));
    }

    [Fact]
    public void FirstMatchWins_AcrossManifest()
    {
        var plan = PushScriptEngine.Plan(Script, Files, seed: 7, today: Today);
        Assert.True(plan.Success);
        var setup = plan.Commits.First(c => c.Message == "Setup");
        var rest = plan.Commits.First(c => c.Message == "Tests and docs");
        Assert.Contains("README.md", setup.Files);
        Assert.DoesNotContain("README.md", rest.Files);
        Assert.Contains("tests/AppTests.cs", rest.Files);
    }

    [Fact]
    public void Times_StayInsideActiveHours()
    {
        var plan = PushScriptEngine.Plan(Script, Files, seed: 99, today: Today);
        Assert.True(plan.Success);
        foreach (var c in plan.Commits)
        {
            var t = TimeOnly.FromDateTime(c.ScheduledAt.DateTime);
            Assert.InRange(t, new TimeOnly(9, 0), new TimeOnly(22, 0));
        }
    }

    [Fact]
    public void Backdate_ProducesPastDatedBatch_BeforeForward()
    {
        var script = Script.Replace("commit \"Setup\" { files = [\"README*\", \".gitignore\"] }",
            "commit \"Setup\" { files = [\"README*\", \".gitignore\"] }") + """

            backdate {
                from = 2026-07-01
                to = 2026-07-20
                per_day = random(1, 2)
                commits = first 1
            }
            """;

        var plan = PushScriptEngine.Plan(script, Files, seed: 4, today: Today);
        Assert.True(plan.Success, string.Join("\n", plan.Diagnostics));

        var backdated = plan.Commits.Where(c => c.Mode == CommitMode.Backdated).ToList();
        var forward = plan.Commits.Where(c => c.Mode == CommitMode.Forward).ToList();

        Assert.Single(backdated);
        Assert.True(backdated[0].ScheduledAt.Date <= new DateTime(2026, 7, 20));
        Assert.All(forward, f => Assert.True(f.ScheduledAt > backdated[0].ScheduledAt));
    }
}
