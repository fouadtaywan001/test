using Pusher.Core.Planning;

namespace Pusher.Core.Scripting;

/// <summary>Convenience facade: source text -> diagnostics + (optional) validated model,
/// and model -> concrete plan. Wraps Lexer/Parser/Validator/Planner so callers (UI, tests,
/// service) share one entry point.</summary>
public sealed record CompileResult(
    ScriptModel? Model,
    IReadOnlyList<Diagnostic> Diagnostics)
{
    public bool Success => Model is not null && Diagnostics.All(d => !d.IsError);
}

public static class PushScriptEngine
{
    /// <summary>Lex + parse + validate. Never throws on bad input — errors come back as diagnostics.</summary>
    public static CompileResult Compile(string source, DateOnly? today = null)
    {
        var diags = new DiagnosticBag();
        var day = today ?? DateOnly.FromDateTime(DateTime.Now);

        var ast = Parser.Parse(source, diags);
        if (diags.Items.Any(d => d.IsError))
            return new CompileResult(null, diags.Items);

        var model = new Validator(diags, day).Validate(ast);
        return new CompileResult(diags.Items.Any(d => d.IsError) ? null : model, diags.Items);
    }

    /// <summary>Compile then build the concrete calendar. Combines diagnostics from both phases.</summary>
    public static PlanResult Plan(string source, IReadOnlyList<string> projectFiles, int seed,
        TimeZoneInfo? tz = null, DateOnly? today = null)
    {
        var compile = Compile(source, today);
        if (!compile.Success || compile.Model is null)
            return new PlanResult(Array.Empty<PlannedCommit>(), compile.Diagnostics);

        var planner = new Planner(tz, today);
        var result = planner.Plan(compile.Model, projectFiles, seed);
        return result with { Diagnostics = compile.Diagnostics.Concat(result.Diagnostics).ToList() };
    }
}
