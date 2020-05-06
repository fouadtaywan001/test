using System.Globalization;

namespace Pusher.Core.Scripting;

/// <summary>
/// Recursive-descent parser for PushScript 1.0 (grammar: plan/05-pushscript-dsl.md §5).
/// Never throws — reports E001/E002/E004 into the DiagnosticBag and recovers at
/// block boundaries so the editor gets as many diagnostics as possible per pass.
/// Key/type legality per block is the Validator's job, not the parser's.
/// </summary>
public sealed class Parser
{
    public static readonly string[] KnownVersions = ["1.0"];

    private static readonly Dictionary<string, DayOfWeek> WeekdayNames = new()
    {
        ["monday"] = DayOfWeek.Monday, ["tuesday"] = DayOfWeek.Tuesday,
        ["wednesday"] = DayOfWeek.Wednesday, ["thursday"] = DayOfWeek.Thursday,
        ["friday"] = DayOfWeek.Friday, ["saturday"] = DayOfWeek.Saturday,
        ["sunday"] = DayOfWeek.Sunday,
    };

    private readonly List<Token> _tokens;
    private readonly DiagnosticBag _diags;
    private int _pos;

    private Parser(List<Token> tokens, DiagnosticBag diags)
    {
        _tokens = tokens;
        _diags = diags;
    }

    /// <summary>Lex + parse in one call.</summary>
    public static ScriptNode Parse(string source, DiagnosticBag diags)
    {
        var tokens = new Lexer(source, diags).Tokenize();
        return new Parser(tokens, diags).ParseScript();
    }

    private Token Current => _tokens[Math.Min(_pos, _tokens.Count - 1)];
    private Token Peek(int ahead = 1) => _tokens[Math.Min(_pos + ahead, _tokens.Count - 1)];
    private Token Advance() { var t = Current; if (_pos < _tokens.Count - 1) _pos++; return t; }

    private bool Match(TokenType type)
    {
        if (Current.Type != type) return false;
        Advance();
        return true;
    }

    private Token Expect(TokenType type, string what)
    {
        if (Current.Type == type) return Advance();
        _diags.Error("E004", $"Expected {what} but found '{Current.Text}'.", Current.Line, Current.Column);
        return new Token(TokenType.Bad, Current.Text, Current.Line, Current.Column);
    }

    // ---- script ----------------------------------------------------------

    private ScriptNode ParseScript()
    {
        var script = new ScriptNode();

        // version header: `pushscript 1.0`
        if (Current.Type == TokenType.Identifier && Current.Text == "pushscript")
        {
            var kw = Advance();
            if (Current.Type is TokenType.VersionNumber or TokenType.Integer)
            {
                var v = Advance();
                script.Version = v.Text;
                script.VersionLine = kw.Line;
                script.VersionColumn = kw.Column;
                if (!KnownVersions.Contains(v.Text))
                    _diags.Error("E001", $"Unknown PushScript version '{v.Text}'. Supported: {string.Join(", ", KnownVersions)}.", v.Line, v.Column);
            }
            else
            {
                _diags.Error("E001", "Missing version number after 'pushscript'.", kw.Line, kw.Column);
            }
        }
        else
        {
            _diags.Error("E001", "Script must start with a version header, e.g. 'pushscript 1.0'.", Current.Line, Current.Column);
        }

        var seen = new HashSet<string>();
        while (Current.Type != TokenType.EndOfFile)
        {
            if (Current.Type != TokenType.Identifier)
            {
                _diags.Error("E004", $"Expected a block name but found '{Current.Text}'.", Current.Line, Current.Column);
                Advance();
                continue;
            }

            var block = ParseBlock();
            if (block is null) continue;

            if (!seen.Add(block.Name))
                _diags.Error("E002", $"Duplicate block '{block.Name}'. Each block may appear at most once.", block.Line, block.Column);
            script.Blocks.Add(block);
        }
        return script;
    }

    // ---- blocks ----------------------------------------------------------

    private BlockNode? ParseBlock()
    {
        var name = Advance(); // identifier
        var block = new BlockNode { Name = name.Text, Line = name.Line, Column = name.Column };

        if (!Match(TokenType.LBrace))
        {
            _diags.Error("E004", $"Expected '{{' after block name '{name.Text}'.", Current.Line, Current.Column);
            SkipToBlockBoundary();
            return null;
        }

        while (Current.Type != TokenType.RBrace && Current.Type != TokenType.EndOfFile)
        {
            Match(TokenType.Comma); // tolerate comma-separated statements inside blocks

            if (Current.Type != TokenType.Identifier)
            {
                _diags.Error("E004", $"Expected a statement but found '{Current.Text}'.", Current.Line, Current.Column);
                Advance();
                continue;
            }

            switch (Current.Text)
            {
                case "commit":
                    block.Commits.Add(ParseCommit());
                    break;
                case "on":
                    block.DayRules.Add(ParseDayRule());
                    break;
                case "skip":
                    // bare `skip` only makes sense inside day rules; validator flags it here
                    _diags.Error("E004", "'skip' is only valid inside an 'on' day rule.", Current.Line, Current.Column);
                    Advance();
                    break;
                default:
                    if (Peek().Type == TokenType.LBrace)
                    {
                        var sub = ParseBlock();
                        if (sub is not null) block.SubBlocks.Add(sub);
                    }
                    else
                    {
                        block.Assignments.Add(ParseAssignment());
                    }
                    break;
            }
        }

        Expect(TokenType.RBrace, "'}'");
        return block;
    }

    private void SkipToBlockBoundary()
    {
        int depth = 0;
        while (Current.Type != TokenType.EndOfFile)
        {
            if (Current.Type == TokenType.LBrace) depth++;
            if (Current.Type == TokenType.RBrace && depth-- == 0) { Advance(); return; }
            Advance();
        }
    }

    // ---- statements ------------------------------------------------------

    private AssignmentNode ParseAssignment()
    {
        var key = Advance(); // identifier
        Expect(TokenType.Equals, $"'=' after '{key.Text}'");
        var value = ParseValue();
        return new AssignmentNode { Key = key.Text, Value = value, Line = key.Line, Column = key.Column };
    }

    private CommitNode ParseCommit()
    {
        var kw = Advance(); // 'commit'
        var msg = Expect(TokenType.String, "a commit message string after 'commit'");
        var commit = new CommitNode { Message = msg.Text, Line = kw.Line, Column = kw.Column };

        Expect(TokenType.LBrace, "'{' after commit message");
        while (Current.Type != TokenType.RBrace && Current.Type != TokenType.EndOfFile)
        {
            if (Current.Type != TokenType.Identifier)
            {
                _diags.Error("E004", $"Expected a key inside commit block but found '{Current.Text}'.", Current.Line, Current.Column);
                Advance();
                continue;
            }
            commit.Assignments.Add(ParseAssignment());
        }
        Expect(TokenType.RBrace, "'}'");
        return commit;
    }

    private DayRuleNode ParseDayRule()
    {
        var kw = Advance(); // 'on'
        var selector = ParseDaySelector();

        Expect(TokenType.LBrace, "'{' after day selector");
        bool isSkip = false;
        var assignments = new List<AssignmentNode>();

        while (Current.Type != TokenType.RBrace && Current.Type != TokenType.EndOfFile)
        {
            if (Current.Type == TokenType.Identifier && Current.Text == "skip")
            {
                isSkip = true;
                Advance();
            }
            else if (Current.Type == TokenType.Identifier)
            {
                assignments.Add(ParseAssignment());
            }
            else
            {
                _diags.Error("E004", $"Expected 'skip' or a key inside day rule but found '{Current.Text}'.", Current.Line, Current.Column);
                Advance();
            }
        }
        Expect(TokenType.RBrace, "'}'");

        var rule = new DayRuleNode { Selector = selector, Line = kw.Line, Column = kw.Column, IsSkip = isSkip };
        rule.Assignments.AddRange(assignments);
        return rule;
    }

    private DaySelector ParseDaySelector()
    {
        // date or date range
        if (Current.Type == TokenType.Date)
        {
            var from = ParseDateToken(Advance());
            if (Match(TokenType.DotDot))
            {
                var toTok = Expect(TokenType.Date, "an end date after '..'");
                var to = toTok.Type == TokenType.Date ? ParseDateToken(toTok) : from;
                return new DaySelector { Kind = DaySelectorKind.DateRange, Date = from, DateEnd = to };
            }
            return new DaySelector { Kind = DaySelectorKind.SingleDate, Date = from };
        }

        if (Current.Type == TokenType.Identifier)
        {
            if (Current.Text == "weekdays") { Advance(); return new DaySelector { Kind = DaySelectorKind.Weekdays }; }
            if (Current.Text == "weekends") { Advance(); return new DaySelector { Kind = DaySelectorKind.Weekends }; }

            if (WeekdayNames.ContainsKey(Current.Text))
            {
                var sel = new DaySelector { Kind = DaySelectorKind.WeekdayList };
                sel.Weekdays.Add(WeekdayNames[Advance().Text]);
                while (Match(TokenType.Comma))
                {
                    if (Current.Type == TokenType.Identifier && WeekdayNames.TryGetValue(Current.Text, out var wd))
                    {
                        sel.Weekdays.Add(wd);
                        Advance();
                    }
                    else
                    {
                        _diags.Error("E004", $"Expected a weekday name but found '{Current.Text}'.", Current.Line, Current.Column);
                        break;
                    }
                }
                return sel;
            }
        }

        _diags.Error("E004", $"Expected a day selector (weekday list, date, date range, 'weekdays' or 'weekends') but found '{Current.Text}'.", Current.Line, Current.Column);
        Advance();
        return new DaySelector { Kind = DaySelectorKind.Weekdays };
    }

    // ---- values ----------------------------------------------------------

    private AstValue ParseValue()
    {
        var t = Current;
        switch (t.Type)
        {
            case TokenType.String:
                Advance();
                return new AstValue { Kind = AstValueKind.String, Str = t.Text, Line = t.Line, Column = t.Column };

            case TokenType.Integer:
            {
                Advance();
                int n = int.Parse(t.Text, CultureInfo.InvariantCulture);
                return new AstValue { Kind = AstValueKind.Integer, Int = n, Line = t.Line, Column = t.Column };
            }

            case TokenType.Percent:
            {
                Advance();
                int p = int.Parse(t.Text, CultureInfo.InvariantCulture);
                return new AstValue { Kind = AstValueKind.Percent, Int = p, Line = t.Line, Column = t.Column };
            }

            case TokenType.Date:
            {
                Advance();
                var d = ParseDateToken(t);
                if (Match(TokenType.DotDot))
                {
                    var end = Expect(TokenType.Date, "an end date after '..'");
                    var d2 = end.Type == TokenType.Date ? ParseDateToken(end) : d;
                    return new AstValue { Kind = AstValueKind.DateRange, Date = d, TimeEnd = null, Line = t.Line, Column = t.Column, Str = d2.ToString("yyyy-MM-dd") };
                }
                return new AstValue { Kind = AstValueKind.Date, Date = d, Line = t.Line, Column = t.Column };
            }

            case TokenType.Time:
            {
                Advance();
                var time = TimeOnly.ParseExact(t.Text, "HH:mm", CultureInfo.InvariantCulture);
                if (Match(TokenType.DotDot))
                {
                    var end = Expect(TokenType.Time, "an end time after '..'");
                    var endTime = end.Type == TokenType.Time
                        ? TimeOnly.ParseExact(end.Text, "HH:mm", CultureInfo.InvariantCulture)
                        : time;
                    return new AstValue { Kind = AstValueKind.TimeRange, Time = time, TimeEnd = endTime, Line = t.Line, Column = t.Column };
                }
                return new AstValue { Kind = AstValueKind.Time, Time = time, Line = t.Line, Column = t.Column };
            }

            case TokenType.Duration:
            {
                Advance();
                int amount = int.Parse(t.Text[..^1], CultureInfo.InvariantCulture);
                int minutes = t.Text[^1] switch { 'h' => amount * 60, 'd' => amount * 1440, _ => amount };
                return new AstValue { Kind = AstValueKind.Duration, DurationMinutes = minutes, Line = t.Line, Column = t.Column };
            }

            case TokenType.LBracket:
                return ParseList();

            case TokenType.LBrace:
                return ParseObject();

            case TokenType.Identifier:
                return ParseIdentifierValue();

            default:
                _diags.Error("E004", $"Expected a value but found '{t.Text}'.", t.Line, t.Column);
                Advance();
                return new AstValue { Kind = AstValueKind.Enum, EnumName = t.Text, Line = t.Line, Column = t.Column };
        }
    }

    private AstValue ParseIdentifierValue()
    {
        var t = Advance();

        if (t.Text is "true" or "false")
            return new AstValue { Kind = AstValueKind.Bool, Bool = t.Text == "true", Line = t.Line, Column = t.Column };

        // `first n` — commit-count selector
        if (t.Text == "first" && Current.Type == TokenType.Integer)
        {
            var n = Advance();
            var call = new AstValue { Kind = AstValueKind.FuncCall, FuncName = "first", Line = t.Line, Column = t.Column };
            call.Args.Add(new FuncArg { Value = new AstValue { Kind = AstValueKind.Integer, Int = int.Parse(n.Text, CultureInfo.InvariantCulture), Line = n.Line, Column = n.Column } });
            return call;
        }

        // function call: random(1,3), weighted([...]), groups(size: 5)
        if (Current.Type == TokenType.LParen)
        {
            Advance(); // (
            var call = new AstValue { Kind = AstValueKind.FuncCall, FuncName = t.Text, Line = t.Line, Column = t.Column };
            if (Current.Type != TokenType.RParen)
            {
                do
                {
                    // named arg: ident ':' value
                    if (Current.Type == TokenType.Identifier && Peek().Type == TokenType.Colon)
                    {
                        var argName = Advance();
                        Advance(); // :
                        call.Args.Add(new FuncArg { Name = argName.Text, Value = ParseValue() });
                    }
                    else
                    {
                        call.Args.Add(new FuncArg { Value = ParseValue() });
                    }
                } while (Match(TokenType.Comma));
            }
            Expect(TokenType.RParen, "')'");
            return call;
        }

        // bare identifier → enum literal (manifest, private, today, immediate, ...)
        return new AstValue { Kind = AstValueKind.Enum, EnumName = t.Text, Line = t.Line, Column = t.Column };
    }

    private AstValue ParseList()
    {
        var open = Advance(); // [
        var list = new AstValue { Kind = AstValueKind.List, Line = open.Line, Column = open.Column };
        if (Current.Type != TokenType.RBracket)
        {
            do
            {
                if (Current.Type == TokenType.RBracket) break; // trailing comma
                var item = ParseValue();
                // pair entry: `value : value` (weighted lists)
                if (Match(TokenType.Colon))
                {
                    var pair = new AstValue { Kind = AstValueKind.Pair, Line = item.Line, Column = item.Column };
                    pair.Items.Add(item);
                    pair.Items.Add(ParseValue());
                    item = pair;
                }
                list.Items.Add(item);
            } while (Match(TokenType.Comma));
        }
        Expect(TokenType.RBracket, "']'");
        return list;
    }

    private AstValue ParseObject()
    {
        var open = Advance(); // {
        var obj = new AstValue { Kind = AstValueKind.Object, Line = open.Line, Column = open.Column };
        while (Current.Type != TokenType.RBrace && Current.Type != TokenType.EndOfFile)
        {
            Match(TokenType.Comma);
            if (Current.Type == TokenType.RBrace) break;
            if (Current.Type != TokenType.Identifier)
            {
                _diags.Error("E004", $"Expected a key inside object but found '{Current.Text}'.", Current.Line, Current.Column);
                Advance();
                continue;
            }
            obj.Fields.Add(ParseAssignment());
        }
        Expect(TokenType.RBrace, "'}'");
        return obj;
    }

    private DateOnly ParseDateToken(Token t)
    {
        if (DateOnly.TryParseExact(t.Text, "yyyy-MM-dd", CultureInfo.InvariantCulture, DateTimeStyles.None, out var d))
            return d;
        _diags.Error("E004", $"'{t.Text}' is not a valid calendar date.", t.Line, t.Column);
        return DateOnly.MinValue;
    }
}
