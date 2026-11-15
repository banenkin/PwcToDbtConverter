namespace PwcToDbtConverter.Model;

public sealed record MappingDef(
    string Name,
    bool IsValid,
    IReadOnlyList<InstanceDef> Instances,
    IReadOnlyList<TransformationDef> Transformations,
    IReadOnlyList<ConnectorDef> Connectors,
    IReadOnlyList<MappingVariableDef> Variables);
