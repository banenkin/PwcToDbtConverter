using System.Globalization;
using System.Text;
using PwcToDbtConverter.Model;

namespace PwcToDbtConverter.Translation;

/// <summary>
/// Translates an Informatica expression into DuckDB SQL with a tokenizer and a recursive-descent parser.
/// The printed text matches the golden strings, which are the translation before a cast to the port type.
/// </summary>
public sealed class ExpressionTranslator : IExpressionTranslator
{
    private static readonly string[] DateFormats =
    [
        "MM/dd/yyyy HH:mm:ss",
        "M/d/yyyy HH:mm:ss",
        "MM/dd/yyyy",
        "yyyy-MM-dd HH:mm:ss",
        "yyyy-MM-dd",
    ];

    private string _source = string.Empty;
    private ExpressionContext _context = new(false, new Dictionary<string, string?>());
    private Dictionary<string, string?> _variables = new(StringComparer.Ordinal);
    private List<Diagnostic> _diagnostics = [];

    public ExpressionTranslation Translate(string expression, ExpressionContext context)
    {
        _source = expression ?? string.Empty;
        _context = context;
        _variables = new Dictionary<string, string?>(context.MappingVariables, StringComparer.Ordinal);
        _diagnostics = [];

        if (string.IsNullOrWhiteSpace(_source))
        {
            Warn("Expression is empty.");
            return new ExpressionTranslation("/* UNSUPPORTED: empty expression */", Freeze());
        }

        var tokenizer = new ExpressionTokenizer(_source, _diagnostics, context.Mapping, context.Instance);
        IReadOnlyList<ExpressionToken> tokens = tokenizer.Tokenize();
        var parser = new ExpressionParser(_source, tokens, _diagnostics, context.Mapping, context.Instance);
        Expr expressionTree = parser.Parse();
        return new ExpressionTranslation(Render(expressionTree), Freeze());
    }

    private string Render(Expr expression)
    {
        return expression switch
        {
            LiteralExpr literal => literal.Text,
            NameExpr name => name.Name,
            VariableExpr variable => RenderVariable(variable),
            ParenExpr paren => "(" + Render(paren.Inner) + ")",
            UnaryExpr unary => RenderUnary(unary),
            BinaryExpr binary => Render(binary.Left) + " " + binary.Operator + " " + Render(binary.Right),
            CallExpr call => RenderCall(call),
            UnsupportedExpr unsupported => Comment(Span(unsupported)),
            _ => Comment(Span(expression)),
        };
    }

    private string RenderUnary(UnaryExpr unary)
    {
        if (unary.Operator == "not"
            && unary.Inner is CallExpr call
            && Same(call.Name, "ISNULL")
            && call.Arguments.Count == 1
            && !call.StarArgument)
        {
            return IsNull(call.Arguments[0], negated: true);
        }

        string inner = Render(unary.Inner);
        if (unary.Inner is BinaryExpr)
        {
            inner = "(" + inner + ")";
        }

        return unary.Operator + (unary.Operator == "not" ? " " : string.Empty) + inner;
    }

    private string RenderCall(CallExpr call)
    {
        if (Same(call.Name, "IIF"))
        {
            return call.Arguments.Count == 3 && !call.StarArgument
                ? "case when " + Render(call.Arguments[0]) + " then " + Render(call.Arguments[1]) + " else " + Render(call.Arguments[2]) + " end"
                : Flag(Span(call), "IIF expects 3 arguments.");
        }

        if (Same(call.Name, "ISNULL"))
        {
            return call.Arguments.Count == 1 && !call.StarArgument
                ? IsNull(call.Arguments[0], negated: false)
                : Flag(Span(call), "ISNULL expects 1 argument.");
        }

        if (Same(call.Name, "SUM") || Same(call.Name, "MAX") || Same(call.Name, "MIN"))
        {
            return Aggregate(call, call.Name.ToLowerInvariant());
        }

        if (Same(call.Name, "COUNT"))
        {
            return Aggregate(call, "count");
        }

        if (Same(call.Name, "TRUNC"))
        {
            return call.Arguments.Count == 1 && !call.StarArgument
                ? "cast(" + Render(call.Arguments[0]) + " as date)"
                : Flag(Span(call), "TRUNC with a format argument is not supported.");
        }

        if (Same(call.Name, "DATE_DIFF"))
        {
            return RenderDateDiff(call);
        }

        if (Same(call.Name, "TO_CHAR"))
        {
            return RenderToChar(call);
        }

        if (Same(call.Name, "TO_DATE"))
        {
            return RenderToDate(call);
        }

        if (Same(call.Name, "UPPER") || Same(call.Name, "LTRIM") || Same(call.Name, "RTRIM"))
        {
            return call.Arguments.Count == 1 && !call.StarArgument
                ? call.Name.ToLowerInvariant() + "(" + Render(call.Arguments[0]) + ")"
                : Flag(Span(call), call.Name + " expects 1 argument.");
        }

        return Flag(Span(call), "Function " + call.Name + " is not supported.");
    }

    private string Aggregate(CallExpr call, string sqlName)
    {
        if (!_context.AllowAggregates)
        {
            Warn("Aggregate " + call.Name + " is valid only inside an Aggregator.");
        }

        if (sqlName == "count" && call.StarArgument && call.Arguments.Count == 0)
        {
            return "count(*)";
        }

        if (call.StarArgument || call.Arguments.Count != 1)
        {
            return Flag(Span(call), call.Name + " expects 1 argument.");
        }

        return sqlName + "(" + Render(call.Arguments[0]) + ")";
    }

    private string RenderDateDiff(CallExpr call)
    {
        if (call.StarArgument || call.Arguments.Count != 3)
        {
            return Flag(Span(call), "DATE_DIFF expects 3 arguments.");
        }

        string? unit = StringLiteral(call.Arguments[2]);
        if (!string.Equals(unit, "DD", StringComparison.OrdinalIgnoreCase))
        {
            return Flag(Span(call), "DATE_DIFF unit '" + (unit ?? Span(call.Arguments[2])) + "' is not supported.");
        }

        // Informatica returns a - b. DuckDB's date_diff('day', start, end) takes the start first.
        return "date_diff('day', " + Render(call.Arguments[1]) + ", " + Render(call.Arguments[0]) + ")";
    }

    private string RenderToChar(CallExpr call)
    {
        if (!TryFormat(call, out string format, out string reason))
        {
            return Flag(Span(call), reason);
        }

        return "strftime(" + Render(call.Arguments[0]) + ", '" + format + "')";
    }

    private string RenderToDate(CallExpr call)
    {
        if (!TryFormat(call, out string format, out string reason))
        {
            return Flag(Span(call), reason);
        }

        return "cast(strptime(" + Render(call.Arguments[0]) + ", '" + format + "') as date)";
    }

    private bool TryFormat(CallExpr call, out string format, out string reason)
    {
        format = string.Empty;
        if (call.StarArgument || call.Arguments.Count != 2)
        {
            reason = call.Name + " expects a value and a format.";
            return false;
        }

        string? pattern = StringLiteral(call.Arguments[1]);
        if (pattern is null)
        {
            reason = call.Name + " format must be a string literal.";
            return false;
        }

        if (!TryTranslateFormat(pattern, out format, out string? token))
        {
            reason = "Format token '" + token + "' is not supported.";
            return false;
        }

        reason = string.Empty;
        return true;
    }

    private static bool TryTranslateFormat(string format, out string duck, out string? badToken)
    {
        var builder = new StringBuilder();
        int index = 0;
        while (index < format.Length)
        {
            if (!char.IsLetter(format[index]))
            {
                builder.Append(format[index]);
                index++;
                continue;
            }

            int start = index;
            while (index < format.Length && char.IsLetter(format[index]))
            {
                index++;
            }

            string token = format.Substring(start, index - start);
            string? mapped = token switch
            {
                "DD" => "%d",
                "MM" => "%m",
                "YY" => "%y",
                _ => null,
            };
            if (mapped is null)
            {
                duck = string.Empty;
                badToken = token;
                return false;
            }

            builder.Append(mapped);
        }

        duck = builder.ToString();
        badToken = null;
        return true;
    }

    private string RenderVariable(VariableExpr variable)
    {
        if (!_variables.TryGetValue(variable.Name, out string? raw) || string.IsNullOrWhiteSpace(raw))
        {
            return Flag(variable.Name, "Mapping variable " + variable.Name + " is not defined.");
        }

        string? isoDate = ToIsoDate(raw);
        if (isoDate is null)
        {
            return Flag(variable.Name, "Mapping variable " + variable.Name + " default '" + raw + "' is not a recognized date.");
        }

        string key = variable.Name.TrimStart('$').ToLowerInvariant();
        return "{{ var('" + key + "', '" + isoDate + "') }}";
    }

    private string IsNull(Expr argument, bool negated)
    {
        string value = Render(argument);
        if (argument is BinaryExpr or UnaryExpr)
        {
            value = "(" + value + ")";
        }

        return value + (negated ? " is not null" : " is null");
    }

    private static string? StringLiteral(Expr expression)
    {
        if (expression is not LiteralExpr literal || literal.Text.Length < 2 || literal.Text[0] != '\'')
        {
            return null;
        }

        return literal.Text[1..^1].Replace("''", "'", StringComparison.Ordinal);
    }

    private static string? ToIsoDate(string raw)
    {
        if (!DateTime.TryParseExact(raw, DateFormats, CultureInfo.InvariantCulture, DateTimeStyles.None, out DateTime parsed))
        {
            return null;
        }

        return parsed.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture);
    }

    private static bool Same(string name, string expected)
    {
        return string.Equals(name, expected, StringComparison.OrdinalIgnoreCase);
    }

    private string Flag(Expr expression, string reason)
    {
        return Flag(Span(expression), reason);
    }

    private string Flag(string original, string reason)
    {
        Warn(reason);
        return Comment(original);
    }

    private static string Comment(string original)
    {
        string text = Sanitize(original);
        return "/* UNSUPPORTED: " + text + " */ " + text;
    }

    private string Span(Expr expression)
    {
        if (expression.Start < 0 || expression.Start > _source.Length || expression.Length <= 0)
        {
            return "expression";
        }

        int length = Math.Min(expression.Length, _source.Length - expression.Start);
        return Collapse(_source.Substring(expression.Start, length));
    }

    private static string Collapse(string text)
    {
        var builder = new StringBuilder(text.Length);
        bool pendingSpace = false;
        foreach (char character in text)
        {
            if (char.IsWhiteSpace(character))
            {
                pendingSpace = builder.Length > 0;
                continue;
            }

            if (pendingSpace)
            {
                builder.Append(' ');
                pendingSpace = false;
            }

            builder.Append(character);
        }

        return builder.ToString();
    }

    private static string Sanitize(string text)
    {
        return text.Replace("*/", "* /", StringComparison.Ordinal);
    }

    private void Warn(string message)
    {
        _diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, _context.Mapping, _context.Instance, message));
    }

    private Diagnostic[] Freeze()
    {
        return _diagnostics.Count == 0 ? [] : _diagnostics.ToArray();
    }
}
