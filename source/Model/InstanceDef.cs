namespace PwcToDbtConverter.Model;

public sealed record InstanceDef(
    string Name,
    string TransformationName,
    InstanceKind Kind,
    IReadOnlyList<string> AssociatedSourceInstances,
    IReadOnlyDictionary<string, string?> Attributes);
