namespace PwcToDbtConverter.Model;

public sealed record MappingVariableDef(string Name, string? DataType, string? DefaultValue, bool IsParameter);
