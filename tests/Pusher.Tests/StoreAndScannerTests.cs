using Pusher.Core.Models;
using Pusher.Core.Scanning;
using Pusher.Core.Scripting;
using Pusher.Storage;

namespace Pusher.Tests;

/// <summary>SQLite round-trips, two-phase state machine, and pre-push scanner rules.</summary>
public class StoreAndScannerTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "pusher-tests-" + Guid.NewGuid().ToString("N"));

    public StoreAndScannerTests() => Directory.CreateDirectory(_dir);
    public void Dispose() { try { Directory.Delete(_dir, true); } catch { } }

    private SqliteStateStore NewStore()
    {
        var store = new SqliteStateStore(Path.Combine(_dir, Guid.NewGuid().ToString("N") + ".db"));
        store.Initialize();
        return store;
    }

    private static Project NewProject() => new()
    {
        Name = "demo",
        LocalPath = @"C:\code\demo",
        RemoteUrl = "https://github.com/user/demo.git",
        ScriptText = "pushscript 1.0",
        ScriptSeed = 42,
    };

    [Fact]
    public void Project_RoundTrips()
    {
        var store = NewStore();
        long id = store.UpsertProject(NewProject());

        var loaded = store.GetProject(id);
        Assert.NotNull(loaded);
        Assert.Equal("demo", loaded!.Name);
        Assert.Equal(42, loaded.ScriptSeed);
        Assert.Equal(ProjectStatus.Draft, loaded.Status);
    }

    [Fact]
    public void CommitPlans_TwoPhase_CommittedThenPushed()
    {
        var store = NewStore();
        long id = store.UpsertProject(NewProject());

        store.SaveCommitPlans(id, [new CommitPlan
        {
            ProjectId = id, Order = 1, Message = "Init",
            FileGlobs = ["**"], ResolvedFiles = ["README.md"],
        }]);

        var plan = store.GetCommitPlans(id).Single();
        Assert.Equal(CommitPlanStatus.Pending, plan.Status);

        store.MarkCommitted(plan.Id, "abc123");
        var committed = store.GetCommitPlans(id).Single();
        Assert.Equal(CommitPlanStatus.Committed, committed.Status);
        Assert.Equal("abc123", committed.CommitSha);

        // Crash-recovery scan must surface it until pushed.
        Assert.Contains(store.GetRecoverableCommits(), c => c.Id == plan.Id);

        store.MarkPushed(plan.Id);
        Assert.DoesNotContain(store.GetRecoverableCommits(), c => c.Id == plan.Id);
        Assert.Equal(CommitPlanStatus.Pushed, store.GetCommitPlans(id).Single().Status);
    }

    [Fact]
    public void DueSlots_OnlyPastAndPending()
    {
        var store = NewStore();
        // Due slots only surface for ACTIVE projects — the worker must never push a draft.
        var project = NewProject();
        project.Status = ProjectStatus.Active;
        long id = store.UpsertProject(project);
        store.SaveCommitPlans(id, [
            new CommitPlan { ProjectId = id, Order = 1, Message = "a", FileGlobs = ["**"], ResolvedFiles = ["a"] },
            new CommitPlan { ProjectId = id, Order = 2, Message = "b", FileGlobs = ["**"], ResolvedFiles = ["b"] },
        ]);
        var plans = store.GetCommitPlans(id);
        var now = DateTimeOffset.UtcNow;

        store.SaveSlots(id, [
            new Slot { CommitPlanId = plans[0].Id, ScheduledAtUtc = now.AddHours(-1) },
            new Slot { CommitPlanId = plans[1].Id, ScheduledAtUtc = now.AddHours(+5) },
        ]);

        var due = store.GetDueSlots(now);
        Assert.Single(due);
        Assert.Equal(plans[0].Id, due[0].CommitPlanId);
    }

    [Fact]
    public void EditPending_UpdatesMessageAndSchedule_ButNeverTouchesExecuted()
    {
        var store = NewStore();
        long id = store.UpsertProject(NewProject());
        store.SaveCommitPlans(id, [
            new CommitPlan { ProjectId = id, Order = 1, Message = "pending", FileGlobs = ["**"], ResolvedFiles = ["a"] },
            new CommitPlan { ProjectId = id, Order = 2, Message = "pushed", FileGlobs = ["**"], ResolvedFiles = ["b"] },
        ]);
        var plans = store.GetCommitPlans(id);
        var now = DateTimeOffset.UtcNow;
        store.SaveSlots(id, [
            new Slot { CommitPlanId = plans[0].Id, ScheduledAtUtc = now.AddHours(1) },
            new Slot { CommitPlanId = plans[1].Id, ScheduledAtUtc = now.AddHours(-1) },
        ]);
        // second commit already ran
        store.MarkCommitted(plans[1].Id, "abc123");
        store.MarkPushed(plans[1].Id);

        var newTime = now.AddDays(2);
        store.UpdatePendingCommitMessage(plans[0].Id, "edited");
        store.UpdatePendingSlotSchedule(plans[0].Id, newTime);
        store.UpdatePendingCommitMessage(plans[1].Id, "MUST NOT APPLY");
        store.UpdatePendingSlotSchedule(plans[1].Id, newTime);

        var reloaded = store.GetCommitPlans(id);
        Assert.Equal("edited", reloaded[0].Message);
        Assert.Equal("pushed", reloaded[1].Message); // guard: pushed commit untouched

        var slots = store.GetSlots(id).ToDictionary(s => s.CommitPlanId);
        Assert.Equal(newTime.ToUniversalTime(), slots[plans[0].Id].ScheduledAtUtc, TimeSpan.FromSeconds(1));
        Assert.Equal(now.AddHours(-1), slots[plans[1].Id].ScheduledAtUtc, TimeSpan.FromSeconds(1));
    }

    [Fact]
    public void DeleteProject_CascadesEverything()
    {
        var store = NewStore();
        long id = store.UpsertProject(NewProject());
        store.SaveCommitPlans(id, [new CommitPlan { ProjectId = id, Order = 1, Message = "a", FileGlobs = ["**"], ResolvedFiles = ["a"] }]);
        var planId = store.GetCommitPlans(id)[0].Id;
        store.SaveSlots(id, [new Slot { CommitPlanId = planId, ScheduledAtUtc = DateTimeOffset.UtcNow }]);

        store.DeleteProject(id);
        Assert.Null(store.GetProject(id));
        Assert.Empty(store.GetCommitPlans(id));
        Assert.Empty(store.GetSlots(id));
    }

    // ---- scanner -----------------------------------------------------------

    [Fact]
    public void Scanner_FindsGitHubToken()
    {
        File.WriteAllText(Path.Combine(_dir, "config.cs"),
            "var token = \"ghp_0123456789abcdefghijABCDEFGHIJ1234\";"); // scan:ignore — fake fixture token

        var findings = new PrePushScanner().Scan(_dir, ["config.cs"]);
        Assert.Contains(findings, f => f.Rule == "GitHub token" && f.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Scanner_FindsPrivateKey()
    {
        File.WriteAllText(Path.Combine(_dir, "key.pem"), "-----BEGIN RSA PRIVATE KEY-----\nMIIE..."); // scan:ignore — fake fixture key
        var findings = new PrePushScanner().Scan(_dir, ["key.pem"]);
        Assert.Contains(findings, f => f.Rule == "Private key block");
    }

    [Fact]
    public void Scanner_SkipsLinesWithScanIgnoreMarker()
    {
        File.WriteAllText(Path.Combine(_dir, "fixture.cs"),
            "var token = \"ghp_0123456789abcdefghijABCDEFGHIJ1234\"; // scan:ignore — test fixture");
        var findings = new PrePushScanner().Scan(_dir, ["fixture.cs"]);
        Assert.DoesNotContain(findings, f => f.Rule == "GitHub token");
    }

    [Fact]
    public void Scanner_PasswordAssignedFromVariable_NotFlagged()
    {
        File.WriteAllText(Path.Combine(_dir, "engine.cs"),
            "var c = new UsernamePasswordCredentials { Username = username, Password = token, };"); // scan:ignore — fixture string
        var findings = new PrePushScanner().Scan(_dir, ["engine.cs"]);
        Assert.DoesNotContain(findings, f => f.Rule == "Connection string");
    }

    [Fact]
    public void Scanner_LiteralPasswordInConnectionString_Flagged()
    {
        File.WriteAllText(Path.Combine(_dir, "appsettings.cs"),
            "var cs = \"Server=db;Database=app;User Id=sa;Password=hunter22;\";"); // scan:ignore — fixture string
        var findings = new PrePushScanner().Scan(_dir, ["appsettings.cs"]);
        Assert.Contains(findings, f => f.Rule == "Connection string" && f.Severity == DiagnosticSeverity.Error);
    }

    [Fact]
    public void Scanner_WarnsOnBuildOutputFolders()
    {
        Directory.CreateDirectory(Path.Combine(_dir, "bin"));
        File.WriteAllText(Path.Combine(_dir, "bin", "app.txt"), "hello");
        var findings = new PrePushScanner().Scan(_dir, ["bin/app.txt"]);
        Assert.Contains(findings, f => f.Rule == "ignore-sanity" && f.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void Scanner_CleanFile_NoFindings()
    {
        File.WriteAllText(Path.Combine(_dir, "readme.md"), "# Hello\nJust a normal file.");
        var findings = new PrePushScanner().Scan(_dir, ["readme.md"]);
        Assert.Empty(findings);
    }
}
