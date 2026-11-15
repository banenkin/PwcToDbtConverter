namespace PwcToDbtConverter.Model;

public sealed record PortDef(
    string Name,
    PortType Type,
    int Position,
    string DataType,
    int? Precision,
    int? Scale,
    string? Expression,
    ExpressionKind ExpressionKind,
    string? Group,
    string? RefField,
    bool IsSortKey,
    string? SortDirection);
