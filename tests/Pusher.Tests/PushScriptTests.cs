using Pusher.Core.Models;
using Pusher.Core.Scripting;

namespace Pusher.Tests;

/// <summary>Lexer/parser/validator coverage for the PushScript spec (plan/05).</summary>
public class PushScriptTests
{
    private const string ValidScript = """
        pushscript 1.0

        repo {
            remote = "https://github.com/user/proj.git"
            branch = "main"
            identity = { name = "Dev", email = "dev@example.com" }
            visibility = private
        }

        split {
            commit "Initial setup" {
                files = ["README*", ".gitignore"]
            }
            commit "Core" {
                files = ["src/**"]
            }
            commit "Rest" {
                files = ["**"]
            }
        }

        schedule {
            start = today
            per_day = random(1, 2)
            active_hours = 18:00 .. 22:30
        }
        """;

    private static readonly DateOnly Today = new(2026, 8, 4);

    [Fact]
    public void ValidScript_Compiles()
    {
        var result = PushScriptEngine.Compile(ValidScript, Today);
        Assert.True(result.Success, string.Join("\n", result.Diagnostics));
        Assert.NotNull(result.Model);
        Assert.Equal("main", result.Model!.Repo.Branch);
        Assert.Equal(3, result.Model.Split.Commits.Count);
        Assert.Equal(new TimeOnly(18, 0), result.Model.Schedule!.ActiveFrom);
    }

    [Fact]
    public void MissingVersionHeader_IsE001()
    {
        var result = PushScriptEngine.Compile("repo { }", Today);
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, d => d.Code == "E001");
    }

    [Fact]
    public void MissingRemote_IsError()
    {
        var script = ValidScript.Replace("remote = \"https://github.com/user/proj.git\"", "");
        var result = PushScriptEngine.Compile(script, Today);
        Assert.False(result.Success);
        Assert.Contains(result.Diagnostics, d => d.Code == "E003" && d.Message.Contains("remote"));
    }

    [Fact]
    public void NonGitHubRemote_IsVRepo1()
    {
        var script = ValidScript.Replace("https://github.com/user/proj.git", "https://gitlab.com/user/proj.git");
        var result = PushScriptEngine.Compile(script, Today);
        Assert.Contains(result.Diagnostics, d => d.Code == "V-REPO-1");
    }

    [Fact]
    public void BadEmail_IsVRepo2()
    {
        var script = ValidScript.Replace("dev@example.com", "not-an-email");
        var result = PushScriptEngine.Compile(script, Today);
        Assert.Contains(result.Diagnostics, d => d.Code == "V-REPO-2");
    }

    [Fact]
    public void ActiveHoursReversed_IsVSched2()
    {
        var script = ValidScript.Replace("18:00 .. 22:30", "22:30 .. 18:00");
        var result = PushScriptEngine.Compile(script, Today);
        Assert.Contains(result.Diagnostics, d => d.Code == "V-SCHED-2");
    }

    [Fact]
    public void StartInPast_IsVSched1()
    {
        var script = ValidScript.Replace("start = today", "start = 2020-01-01");
        var result = PushScriptEngine.Compile(script, Today);
        Assert.Contains(result.Diagnostics, d => d.Code == "V-SCHED-1");
    }

    [Fact]
    public void UnknownKey_IsWarning_NotError()
    {
        var script = ValidScript.Replace("visibility = private", "visibility = private\n    banana = 3");
        var result = PushScriptEngine.Compile(script, Today);
        Assert.True(result.Success);
        Assert.Contains(result.Diagnostics, d => d.Code == "E002" && !d.IsError);
    }

    [Fact]
    public void BackdateInFuture_IsVBack1()
    {
        var script = ValidScript + """

            backdate {
                from = 2030-01-01
                to = 2030-02-01
                commits = all
            }
            """;
        var result = PushScriptEngine.Compile(script, Today);
        Assert.Contains(result.Diagnostics, d => d.Code.StartsWith("V-BACK"));
    }

    [Fact]
    public void WeightedPercents_MustSumTo100()
    {
        var script = ValidScript.Replace("per_day = random(1, 2)", "per_day = weighted([1: 50%, 2: 30%])");
        var result = PushScriptEngine.Compile(script, Today);
        Assert.Contains(result.Diagnostics, d => d.Code == "E005");
    }

    [Fact]
    public void Diagnostics_CarryLineAndColumn()
    {
        var result = PushScriptEngine.Compile("pushscript 1.0\nrepo {\n    remote = 42\n}", Today);
        Assert.False(result.Success);
        Assert.All(result.Diagnostics.Where(d => d.IsError), d => Assert.True(d.Line >= 1));
    }
}
