using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Pusher.Core.Execution;
using Pusher.Core.Models;
using Pusher.Core.Scripting;
using Pusher.Git;
using Pusher.Storage;

// ============================================================================
// FULL end-to-end suite against real GitHub. Creates a dedicated throwaway repo
// with the provided token, exercises every pipeline path in BOTH publish modes,
// then deletes the repo (falls back to branch cleanup if delete scope is missing).
//
//  Engine level
//   1. EnsureStagingRepo on empty repo (unborn HEAD)
//   2. Idempotent re-run
//   3. Backdated commit + push creating a fresh remote branch
//   4. Forward commit + fast-forward push
//   5. Wrong-branch self-heal
//   6. Remote tip readback equals pushed sha
//   7. LocalCommitExists positive/negative
//  Backdated mode (full pipeline: script -> plan -> instant batch -> GitHub)
//   8. Compile + plan a backdate-only script (all dates in the past, ascending)
//   9. InstantExecutor pushes the whole batch at once; remote tip == local tip
//  10. Commit signatures on the remote branch carry the planned PAST dates
//  11. Idempotent re-run: nothing re-created, nothing re-pushed
//  12. Crash recovery: batch with bad token fails at push (commits kept),
//      re-run with good token pushes WITHOUT re-creating commits
//  Real-time mode (planning correctness; no waiting on wall-clock)
//  13. Schedule-only script plans all commits in the FUTURE, ascending
//  14. Non-fast-forward push is DETECTED as an error (silent-failure fix)
// ============================================================================

var token = Environment.GetEnvironmentVariable("E2E_TOKEN")
    ?? throw new InvalidOperationException("E2E_TOKEN not set");

var http = new HttpClient();
http.DefaultRequestHeaders.UserAgent.ParseAdd("pusher-e2e");
http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("token", token);

// Identify the token's account (owner of the throwaway repo).
var user = await http.GetFromJsonAsync<JsonElement>("https://api.github.com/user");
var username = user.GetProperty("login").GetString()!;
Console.WriteLine($"[INFO] authenticated as {username}");

// Create a dedicated private throwaway repo for this run.
var repoName = "pusher-e2e-" + Guid.NewGuid().ToString("N")[..8];
var createResp = await http.PostAsJsonAsync("https://api.github.com/user/repos",
    new { name = repoName, @private = true, auto_init = false, description = "temporary e2e test repo (auto-deleted)" });
if (!createResp.IsSuccessStatusCode)
    throw new InvalidOperationException($"repo creation failed: {createResp.StatusCode} {await createResp.Content.ReadAsStringAsync()}");
var remoteUrl = $"https://github.com/{username}/{repoName}.git";
Console.WriteLine($"[INFO] created test repo {remoteUrl}");

var engine = new GitEngine();
var root = Path.Combine(Path.GetTempPath(), "pusher-e2e-" + Guid.NewGuid().ToString("N")[..8]);
Directory.CreateDirectory(root);
int pass = 0, fail = 0;

void Check(string name, bool ok, string? detail = null)
{
    if (ok) { pass++; Console.WriteLine($"[PASS] {name}"); }
    else { fail++; Console.WriteLine($"[FAIL] {name} {detail}"); }
}

// Builds a real project folder + SQLite store + compiled plan, returns everything
// the InstantExecutor needs. Mirrors NewProjectViewModel.Activate exactly.
(SqliteStateStore store, long projectId, string staging) SetupPipelineProject(
    string dirName, string branch, string script, int fileCount)
{
    var local = Path.Combine(root, dirName + "-src");
    Directory.CreateDirectory(local);
    var files = new List<string>();
    for (int i = 1; i <= fileCount; i++)
    {
        var f = $"src/module{i}.txt";
        Directory.CreateDirectory(Path.Combine(local, "src"));
        File.WriteAllText(Path.Combine(local, f), $"content {i}\n");
        files.Add(f);
    }

    var plan = PushScriptEngine.Plan(script, files, seed: 42);
    if (!plan.Success)
        throw new InvalidOperationException("plan failed: " + string.Join("; ", plan.Diagnostics.Where(d => d.IsError).Select(d => d.Message)));

    // Staging snapshot = copy of the local folder (what the app does).
    var staging = Path.Combine(root, dirName + "-staging");
    Directory.CreateDirectory(staging);
    foreach (var f in files)
    {
        var dst = Path.Combine(staging, f);
        Directory.CreateDirectory(Path.GetDirectoryName(dst)!);
        File.Copy(Path.Combine(local, f), dst);
    }

    var store = new SqliteStateStore(Path.Combine(root, dirName + ".db"));
    store.Initialize();
    var id = store.UpsertProject(new Project
    {
        Name = dirName,
        LocalPath = local,
        StagingPath = staging,
        RemoteUrl = remoteUrl,
        Branch = branch,
        Status = ProjectStatus.Active,
        Mode = ProjectMode.Backdated,
        ScriptText = script,
        ScriptSeed = 42,
    });
    store.SaveCommitPlans(id, plan.Commits.Select(c => new CommitPlan
    {
        ProjectId = id,
        Order = c.Order,
        Message = c.Message,
        Body = c.Body,
        FileGlobs = c.Globs,
        ResolvedFiles = c.Files,
        Mode = c.Mode,
    }).ToList());
    var saved = store.GetCommitPlans(id);
    store.SaveSlots(id, plan.Commits.Zip(saved, (c, cp) => new Slot
    {
        CommitPlanId = cp.Id,
        ScheduledAtUtc = c.ScheduledAt.ToUniversalTime(),
    }).ToList());
    return (store, id, staging);
}

string BackdateScript(string branch) => $$"""
    pushscript 1.0

    repo {
        remote = "{{remoteUrl}}"
        branch = "{{branch}}"
        identity = { name = "E2E Bot", email = "e2e@test.local" }
        visibility = private
    }

    split {
        strategy = manifest
        commit "add module1" { files = ["src/module1.txt"] }
        commit "add module2" { files = ["src/module2.txt"] }
        commit "add module3" { files = ["src/module3.txt"] }
        commit "add module4" { files = ["src/module4.txt"] }
    }

    backdate {
        from = {{DateTime.Today.AddMonths(-2):yyyy-MM-dd}}
        to = {{DateTime.Today.AddDays(-1):yyyy-MM-dd}}
        per_day = random(1, 2)
        active_hours = 09:00 .. 21:00
        commits = all
    }
    """;

try
{
    // ================= Part A: engine level =================
    var branchA = "e2e-engine";
    var staging = Path.Combine(root, "engine-staging");
    Directory.CreateDirectory(staging);
    File.WriteAllText(Path.Combine(staging, "file1.txt"), "e2e case 1\n");
    try
    {
        engine.EnsureStagingRepo(staging, branchA);
        Check("01 EnsureStagingRepo on empty repo (unborn HEAD)", true);
    }
    catch (Exception ex) { Check("01 EnsureStagingRepo on empty repo (unborn HEAD)", false, ex.Message); }

    try
    {
        engine.EnsureStagingRepo(staging, branchA);
        Check("02 EnsureStagingRepo idempotent re-run", true);
    }
    catch (Exception ex) { Check("02 EnsureStagingRepo idempotent re-run", false, ex.Message); }

    string sha1 = "";
    try
    {
        sha1 = engine.CreateCommit(staging, new[] { "file1.txt" }, "e2e: backdated first commit",
            "E2E Bot", "e2e@test.local", DateTimeOffset.UtcNow.AddDays(-2));
        engine.Push(staging, remoteUrl, branchA, username, token);
        Check("03 backdated commit + push new remote branch", sha1.Length == 40);
    }
    catch (Exception ex) { Check("03 backdated commit + push new remote branch", false, ex.Message); }

    string sha2 = "";
    try
    {
        File.WriteAllText(Path.Combine(staging, "file2.txt"), "e2e case 4\n");
        sha2 = engine.CreateCommit(staging, new[] { "file2.txt" }, "e2e: forward second commit",
            "E2E Bot", "e2e@test.local", DateTimeOffset.Now);
        engine.Push(staging, remoteUrl, branchA, username, token);
        Check("04 forward commit + fast-forward push", sha2.Length == 40 && sha2 != sha1);
    }
    catch (Exception ex) { Check("04 forward commit + fast-forward push", false, ex.Message); }

    try
    {
        var wrong = Path.Combine(root, "wrongbranch");
        Directory.CreateDirectory(wrong);
        File.WriteAllText(Path.Combine(wrong, "w.txt"), "x\n");
        engine.EnsureStagingRepo(wrong, "master");
        engine.CreateCommit(wrong, new[] { "w.txt" }, "on master", "E2E Bot", "e2e@test.local", DateTimeOffset.Now);
        engine.EnsureStagingRepo(wrong, branchA);
        using var r = new LibGit2Sharp.Repository(wrong);
        Check("05 self-heal wrong branch -> target", r.Head.FriendlyName == branchA && r.Head.Tip is not null,
            $"head={r.Head.FriendlyName}");
    }
    catch (Exception ex) { Check("05 self-heal wrong branch -> target", false, ex.Message); }

    try
    {
        var tip = engine.GetRemoteBranchTip(remoteUrl, branchA, username, token);
        Check("06 remote tip equals last pushed sha", tip == sha2, $"tip={tip} expected={sha2}");
    }
    catch (Exception ex) { Check("06 remote tip equals last pushed sha", false, ex.Message); }

    try
    {
        var okPos = engine.LocalCommitExists(staging, sha1);
        var okNeg = !engine.LocalCommitExists(staging, new string('0', 40));
        Check("07 LocalCommitExists positive/negative", okPos && okNeg);
    }
    catch (Exception ex) { Check("07 LocalCommitExists positive/negative", false, ex.Message); }

    // ============ Part B: Backdated mode — full instant pipeline ============
    var branchB = "e2e-backdated";
    SqliteStateStore? storeB = null; long idB = 0; string stagingB = "";
    try
    {
        (storeB, idB, stagingB) = SetupPipelineProject("backdated", branchB, BackdateScript(branchB), 4);
        var slots = storeB.GetSlots(idB).OrderBy(s => s.ScheduledAtUtc).ToList();
        var allPast = slots.All(s => s.ScheduledAtUtc < DateTimeOffset.UtcNow);
        var ascending = slots.Zip(slots.Skip(1), (a, b) => a.ScheduledAtUtc <= b.ScheduledAtUtc).All(x => x);
        Check("08 backdate script plans past ascending dates", slots.Count == 4 && allPast && ascending,
            $"count={slots.Count} allPast={allPast} asc={ascending}");
    }
    catch (Exception ex) { Check("08 backdate script plans past ascending dates", false, ex.Message); }

    try
    {
        var result = new InstantExecutor(storeB!, engine).Run(idB, "E2E Bot", "e2e@test.local", username, token);
        var remoteTip = engine.GetRemoteBranchTip(remoteUrl, branchB, username, token);
        using var r = new LibGit2Sharp.Repository(stagingB);
        var localTip = r.Head.Tip.Sha;
        Check("09 instant batch pushes everything at once",
            result.Success && result.Pushed == 4 && remoteTip == localTip,
            $"success={result.Success} pushed={result.Pushed} err={result.Error}");
    }
    catch (Exception ex) { Check("09 instant batch pushes everything at once", false, ex.Message); }

    try
    {
        // Verify the pushed commits carry the planned PAST author dates.
        using var r = new LibGit2Sharp.Repository(stagingB);
        var commits = r.Commits.ToList(); // newest first
        var allBackdated = commits.All(c => c.Author.When < DateTimeOffset.UtcNow.AddHours(-12));
        Check("10 pushed commits signed with planned past dates", commits.Count == 4 && allBackdated,
            $"count={commits.Count} oldest={commits.Last().Author.When:yyyy-MM-dd} newest={commits.First().Author.When:yyyy-MM-dd}");
    }
    catch (Exception ex) { Check("10 pushed commits signed with planned past dates", false, ex.Message); }

    try
    {
        var rerun = new InstantExecutor(storeB!, engine).Run(idB, "E2E Bot", "e2e@test.local", username, token);
        Check("11 idempotent re-run creates and pushes nothing",
            rerun.Success && rerun.Committed == 0 && rerun.Pushed == 0,
            $"committed={rerun.Committed} pushed={rerun.Pushed}");
    }
    catch (Exception ex) { Check("11 idempotent re-run creates and pushes nothing", false, ex.Message); }

    try
    {
        // Crash recovery: first run with a BAD token -> commits succeed, push fails.
        var branchC = "e2e-recovery";
        var (storeC, idC, stagingC) = SetupPipelineProject("recovery", branchC, BackdateScript(branchC), 4);
        var bad = new InstantExecutor(storeC, engine).Run(idC, "E2E Bot", "e2e@test.local", username, "bad-token");
        var committedKept = storeC.GetCommitPlans(idC).Count(p => p.Status == CommitPlanStatus.Committed);

        // Re-run with the good token: must push WITHOUT re-creating.
        var good = new InstantExecutor(storeC, engine).Run(idC, "E2E Bot", "e2e@test.local", username, token);
        var remoteTip = engine.GetRemoteBranchTip(remoteUrl, branchC, username, token);
        using var r = new LibGit2Sharp.Repository(stagingC);
        Check("12 crash recovery: failed push resumes without re-creating",
            !bad.Success && committedKept == 4 && good.Success && good.Committed == 0 && good.Pushed == 4
                && remoteTip == r.Head.Tip.Sha,
            $"bad={bad.Error?[..Math.Min(60, bad.Error.Length)]} kept={committedKept} goodCommitted={good.Committed} goodPushed={good.Pushed}");
    }
    catch (Exception ex) { Check("12 crash recovery: failed push resumes without re-creating", false, ex.Message); }

    // ============ Part C: Real-time mode planning + rejection detection ============
    try
    {
        var rtScript = $$"""
            pushscript 1.0

            repo {
                remote = "{{remoteUrl}}"
                branch = "e2e-realtime"
                identity = { name = "E2E Bot", email = "e2e@test.local" }
                visibility = private
            }

            split {
                strategy = manifest
                commit "step 1" { files = ["src/module1.txt"] }
                commit "step 2" { files = ["src/module2.txt"] }
                commit "step 3" { files = ["src/module3.txt"] }
            }

            schedule {
                start = today
                per_day = random(1, 2)
                active_hours = 00:00 .. 23:59
            }
            """;
        var files = new[] { "src/module1.txt", "src/module2.txt", "src/module3.txt" };
        var plan = PushScriptEngine.Plan(rtScript, files, seed: 7);
        var ascending = plan.Commits.Zip(plan.Commits.Skip(1), (a, b) => a.ScheduledAt <= b.ScheduledAt).All(x => x);
        var futureOrToday = plan.Commits.All(c => c.ScheduledAt.Date >= DateTime.Today);
        Check("13 real-time script plans future ascending schedule",
            plan.Success && plan.Commits.Count == 3 && ascending && futureOrToday,
            $"success={plan.Success} count={plan.Commits.Count} asc={ascending} future={futureOrToday}");
    }
    catch (Exception ex) { Check("13 real-time script plans future ascending schedule", false, ex.Message); }

    try
    {
        // Divergent history: rewrite branch A from a second clone and verify our push
        // surfaces the rejection instead of silently "succeeding" (the old bug).
        var divergent = Path.Combine(root, "divergent");
        Directory.CreateDirectory(divergent);
        File.WriteAllText(Path.Combine(divergent, "other.txt"), "divergent\n");
        engine.EnsureStagingRepo(divergent, branchA);
        engine.CreateCommit(divergent, new[] { "other.txt" }, "divergent history",
            "E2E Bot", "e2e@test.local", DateTimeOffset.Now);
        var detected = false; string msg = "";
        try { engine.Push(divergent, remoteUrl, branchA, username, token); }
        catch (Exception ex) { detected = true; msg = ex.Message; }
        Check("14 non-fast-forward push rejection is detected", detected, "push reported success on divergent history!");
        if (detected) Console.WriteLine($"       rejection surfaced as: {msg[..Math.Min(80, msg.Length)]}");
    }
    catch (Exception ex) { Check("14 non-fast-forward push rejection is detected", false, ex.Message); }
}
finally
{
    try { Directory.Delete(root, recursive: true); } catch { /* best effort */ }

    // Delete the throwaway repo entirely.
    var del = await http.DeleteAsync($"https://api.github.com/repos/{username}/{repoName}");
    if (del.IsSuccessStatusCode)
        Console.WriteLine($"[INFO] deleted test repo {username}/{repoName}");
    else
        Console.WriteLine($"[WARN] could not delete repo ({del.StatusCode}) — delete {username}/{repoName} manually or grant delete_repo scope");
}

Console.WriteLine($"\nRESULT: {pass} passed, {fail} failed");
Environment.Exit(fail == 0 ? 0 : 1);
