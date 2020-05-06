namespace Pusher.Core.Scripting;

public enum DiagnosticSeverity { Error, Warning }

/// <summary>
/// A single validation finding with a stable code from the catalog in
/// plan/05-pushscript-dsl.md §8 (E001..E005, V-REPO-*, V-SPLIT-*, V-SCHED-*, V-BACK-*, V-CROSS-*).
/// Line/column are 1-based for editor squiggles.
/// </summary>
public sealed record Diagnostic(
    string Code,
    DiagnosticSeverity Severity,
    string Message,
    int Line,
    int Column)
{
    public bool IsError => Severity == DiagnosticSeverity.Error;
    public override string ToString() => $"{Code} ({Line},{Column}): {Message}";
}

/// <summary>Accumulates diagnostics during lex/parse/validate.</summary>
public sealed class DiagnosticBag
{
    private readonly List<Diagnostic> _items = new();
    public IReadOnlyList<Diagnostic> Items => _items;
    public bool HasErrors => _items.Any(d => d.IsError);

    public void Error(string code, string message, int line, int column)
        => _items.Add(new Diagnostic(code, DiagnosticSeverity.Error, message, line, column));

    public void Warn(string code, string message, int line, int column)
        => _items.Add(new Diagnostic(code, DiagnosticSeverity.Warning, message, line, column));

    public void AddRange(IEnumerable<Diagnostic> other) => _items.AddRange(other);
}
