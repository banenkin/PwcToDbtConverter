namespace PwcToDbtConverter.Model;

public sealed record TransformationDef(
    string Name,
    TransformationKind Kind,
    string? TemplateName,
    IReadOnlyList<PortDef> Ports,
    IReadOnlyList<GroupDef> Groups,
    IReadOnlyDictionary<string, string?> Attributes);
