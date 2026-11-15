using PwcToDbtConverter.Model;

namespace PwcToDbtConverter.Translation;

/// <summary>
/// DuckDB text for one Informatica expression, plus any warnings raised while translating it.
/// </summary>
public sealed record ExpressionTranslation(string Sql, IReadOnlyList<Diagnostic> Diagnostics);
