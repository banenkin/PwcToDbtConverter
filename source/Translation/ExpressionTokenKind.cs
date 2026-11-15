namespace PwcToDbtConverter.Translation;

internal enum ExpressionTokenKind
{
    End,
    Identifier,
    Number,
    String,
    Variable,
    And,
    Or,
    Not,
    Equal,
    NotEqual,
    Greater,
    GreaterOrEqual,
    Less,
    LessOrEqual,
    Plus,
    Minus,
    Star,
    Slash,
    LeftParen,
    RightParen,
    Comma,
    Bad,
}
