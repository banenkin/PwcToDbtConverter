namespace PwcToDbtConverter.Model;

public sealed record GroupDef(string Name, string Type, int Order, string? Expression);
