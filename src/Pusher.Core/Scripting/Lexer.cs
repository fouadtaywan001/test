using System.Text;

namespace Pusher.Core.Scripting;

/// <summary>
/// Hand-written lexer for PushScript 1.0 (plan/05-pushscript-dsl.md §2).
/// Produces a token stream with 1-based line/column positions; never throws —
/// malformed input yields Bad tokens plus E004 diagnostics.
/// </summary>
public sealed class Lexer
{
    private readonly string _src;
    private readonly DiagnosticBag _diags;
    private int _pos;
    private int _line = 1;
    private int _col = 1;

    public Lexer(string source, DiagnosticBag diagnostics)
    {
        _src = source;
        _diags = diagnostics;
    }

    public List<Token> Tokenize()
    {
        var tokens = new List<Token>();
        while (true)
        {
            var tok = Next();
            tokens.Add(tok);
            if (tok.Type == TokenType.EndOfFile) break;
        }
        return tokens;
    }

    private char Current => _pos < _src.Length ? _src[_pos] : '\0';
    private char Peek(int ahead = 1) => _pos + ahead < _src.Length ? _src[_pos + ahead] : '\0';

    private void Advance()
    {
        if (Current == '\n') { _line++; _col = 1; }
        else _col++;
        _pos++;
    }

    private Token Next()
    {
        SkipTrivia();
        int line = _line, col = _col;

        if (_pos >= _src.Length)
            return new Token(TokenType.EndOfFile, "", line, col);

        char c = Current;

        switch (c)
        {
            case '{': Advance(); return new Token(TokenType.LBrace, "{", line, col);
            case '}': Advance(); return new Token(TokenType.RBrace, "}", line, col);
            case '[': Advance(); return new Token(TokenType.LBracket, "[", line, col);
            case ']': Advance(); return new Token(TokenType.RBracket, "]", line, col);
            case '(': Advance(); return new Token(TokenType.LParen, "(", line, col);
            case ')': Advance(); return new Token(TokenType.RParen, ")", line, col);
            case '=': Advance(); return new Token(TokenType.Equals, "=", line, col);
            case ',': Advance(); return new Token(TokenType.Comma, ",", line, col);
            case ':': Advance(); return new Token(TokenType.Colon, ":", line, col);
            case '.':
                if (Peek() == '.') { Advance(); Advance(); return new Token(TokenType.DotDot, "..", line, col); }
                break;
            case '"':
                return LexString(line, col);
        }

        if (char.IsDigit(c))
            return LexNumberLike(line, col);

        if (char.IsLetter(c) || c == '_')
            return LexIdentifier(line, col);

        _diags.Error("E004", $"Unexpected character '{c}'.", line, col);
        Advance();
        return new Token(TokenType.Bad, c.ToString(), line, col);
    }

    private void SkipTrivia()
    {
        while (_pos < _src.Length)
        {
            char c = Current;
            if (c == '#')
            {
                while (_pos < _src.Length && Current != '\n') Advance();
            }
            else if (char.IsWhiteSpace(c))
            {
                Advance();
            }
            else break;
        }
    }

    private Token LexString(int line, int col)
    {
        Advance(); // opening quote
        var sb = new StringBuilder();
        while (_pos < _src.Length && Current != '"' && Current != '\n')
        {
            if (Current == '\\' && (Peek() == '"' || Peek() == '\\'))
            {
                sb.Append(Peek());
                Advance(); Advance();
            }
            else
            {
                sb.Append(Current);
                Advance();
            }
        }
        if (Current != '"')
        {
            _diags.Error("E004", "Unterminated string literal.", line, col);
            return new Token(TokenType.Bad, sb.ToString(), line, col);
        }
        Advance(); // closing quote
        return new Token(TokenType.String, sb.ToString(), line, col);
    }

    /// <summary>
    /// Disambiguates everything that starts with a digit:
    /// Date (YYYY-MM-DD), Time (HH:MM), Duration (45m/2h/3d), Percent (15%),
    /// VersionNumber (1.0), plain Integer.
    /// </summary>
    private Token LexNumberLike(int line, int col)
    {
        int start = _pos;
        while (char.IsDigit(Current)) Advance();
        string digits = _src[start.._pos];

        // Date: exactly 4 digits then -MM-DD
        if (digits.Length == 4 && Current == '-' && char.IsDigit(Peek()))
        {
            int save = _pos;
            Advance(); // -
            int mStart = _pos;
            while (char.IsDigit(Current)) Advance();
            string mm = _src[mStart.._pos];
            if (mm.Length == 2 && Current == '-' && char.IsDigit(Peek()))
            {
                Advance(); // -
                int dStart = _pos;
                while (char.IsDigit(Current)) Advance();
                string dd = _src[dStart.._pos];
                if (dd.Length == 2)
                    return new Token(TokenType.Date, $"{digits}-{mm}-{dd}", line, col);
            }
            // not a valid date — rewind and fall through as integer
            _pos = save;
            RecomputePosition(save);
        }

        // Time: 1-2 digits, ':', exactly 2 digits, not followed by '%' (weighted entries)
        if (digits.Length is 1 or 2 && Current == ':' && char.IsDigit(Peek()) && char.IsDigit(Peek(2))
            && Peek(3) != '%' && !char.IsDigit(Peek(3)))
        {
            Advance(); // :
            int tStart = _pos;
            Advance(); Advance();
            string mins = _src[tStart.._pos];
            return new Token(TokenType.Time, $"{digits.PadLeft(2, '0')}:{mins}", line, col);
        }

        // Version number: digits '.' digits (only meaningful after `pushscript`)
        if (Current == '.' && Peek() != '.' && char.IsDigit(Peek()))
        {
            Advance(); // .
            int vStart = _pos;
            while (char.IsDigit(Current)) Advance();
            return new Token(TokenType.VersionNumber, $"{digits}.{_src[vStart.._pos]}", line, col);
        }

        // Percent
        if (Current == '%')
        {
            Advance();
            return new Token(TokenType.Percent, digits, line, col);
        }

        // Duration: m / h / d suffix not followed by more identifier chars
        if ((Current is 'm' or 'h' or 'd') && !char.IsLetterOrDigit(Peek()) && Peek() != '_')
        {
            char unit = Current;
            Advance();
            return new Token(TokenType.Duration, digits + unit, line, col);
        }

        return new Token(TokenType.Integer, digits, line, col);
    }

    private Token LexIdentifier(int line, int col)
    {
        int start = _pos;
        while (char.IsLetterOrDigit(Current) || Current == '_') Advance();
        return new Token(TokenType.Identifier, _src[start.._pos], line, col);
    }

    /// <summary>Recompute line/col after a rewind (rare path, short distances).</summary>
    private void RecomputePosition(int pos)
    {
        _line = 1; _col = 1;
        for (int i = 0; i < pos; i++)
        {
            if (_src[i] == '\n') { _line++; _col = 1; }
            else _col++;
        }
    }
}
