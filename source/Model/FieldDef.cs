namespace PwcToDbtConverter.Model;

public sealed record FieldDef(
    string Name,
    string DataType,
    int? Precision,
    int? Scale,
    bool IsNullable,
    KeyType KeyType,
    string? ReferencedDbd,
    string? ReferencedTable,
    string? ReferencedField);
