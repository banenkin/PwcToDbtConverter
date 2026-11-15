using PwcToDbtConverter.Model;

namespace PwcToDbtConverter.Translation;

internal sealed class ExpressionTokenizer
{
    private readonly string _source;
    private readonly List<Diagnostic> _diagnostics;
    private readonly string _mapping;
    private readonly string? _instance;
    private int _index;

    public ExpressionTokenizer(string source, List<Diagnostic> diagnostics, string mapping, string? instance)
    {
        _source = source;
        _diagnostics = diagnostics;
        _mapping = mapping;
        _instance = instance;
    }

    public IReadOnlyList<ExpressionToken> Tokenize()
    {
        var tokens = new List<ExpressionToken>();
        while (_index < _source.Length)
        {
            char current = _source[_index];
            if (char.IsWhiteSpace(current))
            {
                _index++;
                continue;
            }

            if (current == '\'')
            {
                tokens.Add(ReadString());
                continue;
            }

            if (current == '$')
            {
                tokens.Add(ReadVariable());
                continue;
            }

            if (char.IsDigit(current))
            {
                tokens.Add(ReadNumber());
                continue;
            }

            if (char.IsLetter(current) || current == '_')
            {
                tokens.Add(ReadIdentifier());
                continue;
            }

            ExpressionToken? symbol = ReadSymbol();
            if (symbol is null)
            {
                int start = _index;
                _index++;
                _diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Warning,
                    _mapping,
                    _instance,
                    $"Unexpected character '{current}' in expression."));
                tokens.Add(new ExpressionToken(ExpressionTokenKind.Bad, _source.Substring(start, 1), start));
                continue;
            }

            tokens.Add(symbol.Value);
        }

        tokens.Add(new ExpressionToken(ExpressionTokenKind.End, string.Empty, _source.Length));
        return tokens;
    }

    private ExpressionToken ReadString()
    {
        int start = _index;
        _index++;
        while (_index < _source.Length)
        {
            if (_source[_index] != '\'')
            {
                _index++;
                continue;
            }

            if (_index + 1 < _source.Length && _source[_index + 1] == '\'')
            {
                _index += 2;
                continue;
            }

            _index++;
            return Slice(ExpressionTokenKind.String, start);
        }

        _diagnostics.Add(new Diagnostic(
            DiagnosticSeverity.Warning,
            _mapping,
            _instance,
            "Expression has an unterminated string."));
        return Slice(ExpressionTokenKind.Bad, start);
    }

    private ExpressionToken ReadVariable()
    {
        int start = _index;
        if (_index + 1 >= _source.Length || _source[_index + 1] != '$')
        {
            _index++;
            _diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Warning,
                _mapping,
                _instance,
                "Expression has a stray '$'."));
            return Slice(ExpressionTokenKind.Bad, start);
        }

        _index += 2;
        if (_index >= _source.Length || !(char.IsLetter(_source[_index]) || _source[_index] == '_'))
        {
            _diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Warning,
                _mapping,
                _instance,
                "Mapping variable is missing a name."));
            return Slice(ExpressionTokenKind.Bad, start);
        }

        while (_index < _source.Length && (char.IsLetterOrDigit(_source[_index]) || _source[_index] == '_'))
        {
            _index++;
        }

        return Slice(ExpressionTokenKind.Variable, start);
    }

    private ExpressionToken ReadNumber()
    {
        int start = _index;
        while (_index < _source.Length && char.IsDigit(_source[_index]))
        {
            _index++;
        }

        if (_index < _source.Length && _source[_index] == '.' && _index + 1 < _source.Length && char.IsDigit(_source[_index + 1]))
        {
            _index++;
            while (_index < _source.Length && char.IsDigit(_source[_index]))
            {
                _index++;
            }
        }

        return Slice(ExpressionTokenKind.Number, start);
    }

    private ExpressionToken ReadIdentifier()
    {
        int start = _index;
        while (_index < _source.Length && (char.IsLetterOrDigit(_source[_index]) || _source[_index] == '_'))
        {
            _index++;
        }

        ExpressionToken token = Slice(ExpressionTokenKind.Identifier, start);
        ExpressionTokenKind keyword = token.Text.ToUpperInvariant() switch
        {
            "AND" => ExpressionTokenKind.And,
            "OR" => ExpressionTokenKind.Or,
            "NOT" => ExpressionTokenKind.Not,
            _ => ExpressionTokenKind.Identifier,
        };
        return keyword == ExpressionTokenKind.Identifier
            ? token
            : new ExpressionToken(keyword, token.Text, token.Start);
    }

    private ExpressionToken? ReadSymbol()
    {
        int start = _index;
        char current = _source[_index];
        char next = _index + 1 < _source.Length ? _source[_index + 1] : '\0';
        if (current == '<' && next == '>')
        {
            _index += 2;
            return new ExpressionToken(ExpressionTokenKind.NotEqual, "<>", start);
        }

        if (current == '!' && next == '=')
        {
            _index += 2;
            return new ExpressionToken(ExpressionTokenKind.NotEqual, "!=", start);
        }

        if (current == '>' && next == '=')
        {
            _index += 2;
            return new ExpressionToken(ExpressionTokenKind.GreaterOrEqual, ">=", start);
        }

        if (current == '<' && next == '=')
        {
            _index += 2;
            return new ExpressionToken(ExpressionTokenKind.LessOrEqual, "<=", start);
        }

        ExpressionTokenKind? kind = current switch
        {
            '=' => ExpressionTokenKind.Equal,
            '>' => ExpressionTokenKind.Greater,
            '<' => ExpressionTokenKind.Less,
            '+' => ExpressionTokenKind.Plus,
            '-' => ExpressionTokenKind.Minus,
            '*' => ExpressionTokenKind.Star,
            '/' => ExpressionTokenKind.Slash,
            '(' => ExpressionTokenKind.LeftParen,
            ')' => ExpressionTokenKind.RightParen,
            ',' => ExpressionTokenKind.Comma,
            _ => null,
        };
        if (kind is null)
        {
            return null;
        }

        _index++;
        return new ExpressionToken(kind.Value, current.ToString(), start);
    }

    private ExpressionToken Slice(ExpressionTokenKind kind, int start)
    {
        return new ExpressionToken(kind, _source.Substring(start, _index - start), start);
    }
}
