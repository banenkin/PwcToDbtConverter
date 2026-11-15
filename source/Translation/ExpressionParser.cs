using PwcToDbtConverter.Model;

namespace PwcToDbtConverter.Translation;

internal sealed class ExpressionParser
{
    private readonly string _source;
    private readonly IReadOnlyList<ExpressionToken> _tokens;
    private readonly List<Diagnostic> _diagnostics;
    private readonly string _mapping;
    private readonly string? _instance;
    private int _index;

    public ExpressionParser(
        string source,
        IReadOnlyList<ExpressionToken> tokens,
        List<Diagnostic> diagnostics,
        string mapping,
        string? instance)
    {
        _source = source;
        _tokens = tokens;
        _diagnostics = diagnostics;
        _mapping = mapping;
        _instance = instance;
    }

    public Expr Parse()
    {
        Expr expression = ParseOr();
        if (Current.Kind != ExpressionTokenKind.End)
        {
            Warn($"Unexpected '{Current.Text}' in expression.");
            return new UnsupportedExpr(0, _source.Length);
        }

        return expression;
    }

    private Expr ParseOr()
    {
        Expr left = ParseAnd();
        while (Match(ExpressionTokenKind.Or))
        {
            Expr right = ParseAnd();
            left = Join(left, "or", right);
        }

        return left;
    }

    private Expr ParseAnd()
    {
        Expr left = ParseNot();
        while (Match(ExpressionTokenKind.And))
        {
            Expr right = ParseNot();
            left = Join(left, "and", right);
        }

        return left;
    }

    private Expr ParseNot()
    {
        if (!Check(ExpressionTokenKind.Not))
        {
            return ParseComparison();
        }

        ExpressionToken token = Advance();
        Expr inner = ParseNot();
        return new UnaryExpr("not", inner, token.Start, EndOf(inner) - token.Start);
    }

    private Expr ParseComparison()
    {
        Expr left = ParseSum();
        if (!TryComparison(Current.Kind, out string op))
        {
            return left;
        }

        Advance();
        Expr right = ParseSum();
        return Join(left, op, right);
    }

    private Expr ParseSum()
    {
        Expr left = ParseProduct();
        while (Check(ExpressionTokenKind.Plus) || Check(ExpressionTokenKind.Minus))
        {
            string op = Current.Kind == ExpressionTokenKind.Plus ? "+" : "-";
            Advance();
            Expr right = ParseProduct();
            left = Join(left, op, right);
        }

        return left;
    }

    private Expr ParseProduct()
    {
        Expr left = ParseUnary();
        while (Check(ExpressionTokenKind.Star) || Check(ExpressionTokenKind.Slash))
        {
            string op = Current.Kind == ExpressionTokenKind.Star ? "*" : "/";
            Advance();
            Expr right = ParseUnary();
            left = Join(left, op, right);
        }

        return left;
    }

    private Expr ParseUnary()
    {
        if (!Check(ExpressionTokenKind.Plus) && !Check(ExpressionTokenKind.Minus))
        {
            return ParsePrimary();
        }

        ExpressionToken token = Advance();
        string op = token.Kind == ExpressionTokenKind.Plus ? "+" : "-";
        Expr inner = ParseUnary();
        return new UnaryExpr(op, inner, token.Start, EndOf(inner) - token.Start);
    }

    private Expr ParsePrimary()
    {
        if (Check(ExpressionTokenKind.Identifier) && NextIs(ExpressionTokenKind.LeftParen))
        {
            ExpressionToken name = Advance();
            return ParseCall(name);
        }

        if (Check(ExpressionTokenKind.Identifier))
        {
            ExpressionToken token = Advance();
            return new NameExpr(token.Text, token.Start, token.Length);
        }

        if (Check(ExpressionTokenKind.Number) || Check(ExpressionTokenKind.String))
        {
            ExpressionToken token = Advance();
            return new LiteralExpr(token.Text, token.Start, token.Length);
        }

        if (Check(ExpressionTokenKind.Variable))
        {
            ExpressionToken token = Advance();
            return new VariableExpr(token.Text, token.Start, token.Length);
        }

        if (Match(ExpressionTokenKind.LeftParen))
        {
            int start = Previous.Start;
            Expr inner = ParseOr();
            if (!Match(ExpressionTokenKind.RightParen))
            {
                Warn("Expression is missing ')'.");
                return new UnsupportedExpr(start, Math.Max(1, EndOf(inner) - start));
            }

            return new ParenExpr(inner, start, CurrentStartAfter(start));
        }

        Warn(Current.Kind == ExpressionTokenKind.End
            ? "Expression ended early."
            : $"Unexpected '{Current.Text}' in expression.");
        if (Current.Kind == ExpressionTokenKind.End)
        {
            return new UnsupportedExpr(_source.Length, 0);
        }

        ExpressionToken bad = Advance();
        return new UnsupportedExpr(bad.Start, Math.Max(1, bad.Length));
    }

    private Expr ParseCall(ExpressionToken name)
    {
        Advance();
        var arguments = new List<Expr>();
        bool star = false;
        if (!Check(ExpressionTokenKind.RightParen))
        {
            if (Match(ExpressionTokenKind.Star))
            {
                star = true;
            }
            else
            {
                arguments.Add(ParseOr());
            }

            while (Match(ExpressionTokenKind.Comma))
            {
                if (Match(ExpressionTokenKind.Star))
                {
                    star = true;
                    continue;
                }

                arguments.Add(ParseOr());
            }
        }

        if (!Match(ExpressionTokenKind.RightParen))
        {
            Warn($"Call '{name.Text}' is missing ')'.");
        }

        int length = (_index > 0 ? Previous.Start + Previous.Length : name.Start + name.Length) - name.Start;
        return new CallExpr(name.Text, arguments, star, name.Start, Math.Max(name.Length, length));
    }

    private static BinaryExpr Join(Expr left, string op, Expr right)
    {
        return new BinaryExpr(left, op, right, left.Start, EndOf(right) - left.Start);
    }

    private static int EndOf(Expr expression)
    {
        return expression.Start + expression.Length;
    }

    private int CurrentStartAfter(int start)
    {
        int end = _index > 0 ? Previous.Start + Previous.Length : start;
        return Math.Max(1, end - start);
    }

    private static bool TryComparison(ExpressionTokenKind kind, out string op)
    {
        switch (kind)
        {
            case ExpressionTokenKind.Equal:
                op = "=";
                return true;
            case ExpressionTokenKind.NotEqual:
                op = "<>";
                return true;
            case ExpressionTokenKind.Greater:
                op = ">";
                return true;
            case ExpressionTokenKind.GreaterOrEqual:
                op = ">=";
                return true;
            case ExpressionTokenKind.Less:
                op = "<";
                return true;
            case ExpressionTokenKind.LessOrEqual:
                op = "<=";
                return true;
            default:
                op = string.Empty;
                return false;
        }
    }

    private bool NextIs(ExpressionTokenKind kind)
    {
        return _index + 1 < _tokens.Count && _tokens[_index + 1].Kind == kind;
    }

    private bool Check(ExpressionTokenKind kind)
    {
        return Current.Kind == kind;
    }

    private bool Match(ExpressionTokenKind kind)
    {
        if (!Check(kind))
        {
            return false;
        }

        Advance();
        return true;
    }

    private ExpressionToken Advance()
    {
        ExpressionToken current = Current;
        if (Current.Kind != ExpressionTokenKind.End)
        {
            _index++;
        }

        return current;
    }

    private ExpressionToken Current => _tokens[_index];

    private ExpressionToken Previous => _tokens[_index - 1];

    private void Warn(string message)
    {
        _diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, _mapping, _instance, message));
    }
}
