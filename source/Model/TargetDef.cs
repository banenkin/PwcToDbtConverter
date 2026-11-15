namespace PwcToDbtConverter.Model;

public sealed record TargetDef(
    string Name,
    string? DatabaseType,
    IReadOnlyList<FieldDef> Fields);
