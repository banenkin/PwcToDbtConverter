namespace PwcToDbtConverter.Model;

public sealed record SourceDef(
    string Name,
    string? OwnerName,
    string? DbdName,
    string? DatabaseType,
    IReadOnlyList<FieldDef> Fields);
