namespace PwcToDbtConverter.Model;

public sealed record Diagnostic(
    DiagnosticSeverity Severity,
    string Mapping,
    string? Instance,
    string Message,
    string Code = "");
