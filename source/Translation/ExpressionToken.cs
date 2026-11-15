namespace PwcToDbtConverter.Translation;

internal readonly struct ExpressionToken
{
    public ExpressionToken(ExpressionTokenKind kind, string text, int start)
    {
        Kind = kind;
        Text = text;
        Start = start;
    }

    public ExpressionTokenKind Kind { get; }

    public string Text { get; }

    public int Start { get; }

    public int Length => Text.Length;
}
