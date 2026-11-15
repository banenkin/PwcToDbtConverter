namespace PwcToDbtConverter.Translation;

internal abstract record Expr(int Start, int Length);

internal sealed record LiteralExpr(string Text, int Start, int Length) : Expr(Start, Length);

internal sealed record NameExpr(string Name, int Start, int Length) : Expr(Start, Length);

internal sealed record VariableExpr(string Name, int Start, int Length) : Expr(Start, Length);

internal sealed record ParenExpr(Expr Inner, int Start, int Length) : Expr(Start, Length);

internal sealed record UnaryExpr(string Operator, Expr Inner, int Start, int Length) : Expr(Start, Length);

internal sealed record BinaryExpr(Expr Left, string Operator, Expr Right, int Start, int Length) : Expr(Start, Length);

internal sealed record CallExpr(string Name, IReadOnlyList<Expr> Arguments, bool StarArgument, int Start, int Length) : Expr(Start, Length);

internal sealed record UnsupportedExpr(int Start, int Length) : Expr(Start, Length);
