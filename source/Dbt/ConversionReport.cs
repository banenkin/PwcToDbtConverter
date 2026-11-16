using System.Text;
using PwcToDbtConverter.Model;

namespace PwcToDbtConverter.Dbt;

/// <summary>
/// Counts and diagnostics from one conversion. The text is what the CLI prints.
/// </summary>
public sealed record ConversionReport(
    int MappingCount,
    int TargetCount,
    int ModelCount,
    string OutputDirectory,
    IReadOnlyList<Diagnostic> Diagnostics)
{
    public string Format()
    {
        var builder = new StringBuilder();
        builder.Append("Mappings: ").Append(MappingCount).Append('\n');
        builder.Append("Targets: ").Append(TargetCount).Append('\n');
        builder.Append("Models: ").Append(ModelCount).Append('\n');
        builder.Append("Output: ").Append(OutputDirectory).Append('\n');

        int errors = Diagnostics.Count(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        int warnings = Diagnostics.Count(diagnostic => diagnostic.Severity == DiagnosticSeverity.Warning);
        int infos = Diagnostics.Count(diagnostic => diagnostic.Severity == DiagnosticSeverity.Info);
        builder.Append("Errors: ").Append(errors).Append('\n');
        builder.Append("Warnings: ").Append(warnings).Append('\n');
        builder.Append("Info: ").Append(infos).Append('\n');

        foreach (Diagnostic diagnostic in Diagnostics.Where(diagnostic => diagnostic.Severity != DiagnosticSeverity.Info))
        {
            builder.Append("  ");
            if (diagnostic.Mapping.Length > 0)
            {
                builder.Append(diagnostic.Mapping);
                builder.Append(" / ");
            }

            if (diagnostic.Instance is not null)
            {
                builder.Append(diagnostic.Instance);
                builder.Append(": ");
            }

            builder.Append(diagnostic.Message).Append('\n');
        }

        return builder.ToString();
    }
}
