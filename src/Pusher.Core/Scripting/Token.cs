namespace Pusher.Core.Scripting;

public enum TokenType
{
    // literals
    Identifier,     // per_day, active_hours, weekday names, enum literals
    String,         // "Add parser core"
    Integer,        // 3
    Percent,        // 15%
    Date,           // 2026-08-10
    Time,           // 09:30
    Duration,       // 45m / 2h / 3d
    VersionNumber,  // 1.0  (only after the `pushscript` keyword)

    // punctuation
    LBrace, RBrace, // { }
    LBracket, RBracket, // [ ]
    LParen, RParen, // ( )
    Equals,         // =
    Comma,          // ,
    Colon,          // :
    DotDot,         // ..

    EndOfFile,
    Bad,
}

/// <summary>One lexical token with its 1-based source position.</summary>
public readonly record struct Token(TokenType Type, string Text, int Line, int Column)
{
    public override string ToString() => $"{Type} '{Text}' @{Line}:{Column}";
}
