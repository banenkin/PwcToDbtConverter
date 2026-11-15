namespace PwcToDbtConverter.Model;

public sealed record PowerMartDocument(
    string FolderName,
    IReadOnlyList<SourceDef> Sources,
    IReadOnlyList<TargetDef> Targets,
    IReadOnlyList<MappingDef> Mappings,
    IReadOnlyList<Diagnostic> Diagnostics);
