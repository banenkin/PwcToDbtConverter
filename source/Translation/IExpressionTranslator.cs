namespace PwcToDbtConverter.Translation;

/// <summary>
/// Translates one Informatica expression into a DuckDB expression.
/// An unmapped function is kept in the SQL and marked with a warning and an inline comment.
/// </summary>
public interface IExpressionTranslator
{
    ExpressionTranslation Translate(string expression, ExpressionContext context);
}
