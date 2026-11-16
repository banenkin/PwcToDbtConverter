using PwcToDbtConverter.Graph;
using PwcToDbtConverter.Model;
using PwcToDbtConverter.Parsing;
using PwcToDbtConverter.Sql;

namespace PwcToDbtConverter.Dbt;

/// <summary>
/// Parses one export, generates a model per target instance, and writes the dbt project.
/// </summary>
public sealed class DbtProjectConverter
{
    private readonly IPowerMartParser _parser;
    private readonly IDataFlowGraphBuilder _graphs;
    private readonly IModelSqlGenerator _sql;
    private readonly IDbtProjectWriter _writer;

    public DbtProjectConverter(
        IPowerMartParser parser,
        IDataFlowGraphBuilder graphs,
        IModelSqlGenerator sql,
        IDbtProjectWriter writer)
    {
        _parser = parser;
        _graphs = graphs;
        _sql = sql;
        _writer = writer;
    }

    public ConversionReport Convert(string inputPath, string outputDirectory)
    {
        PowerMartDocument document = _parser.Parse(inputPath);
        var diagnostics = new List<Diagnostic>(document.Diagnostics);
        var models = new List<DbtModel>();
        foreach (MappingDef mapping in document.Mappings)
        {
            GraphBuildResult built = _graphs.Build(mapping, document);
            diagnostics.AddRange(built.Diagnostics);
            foreach (InstanceDef instance in mapping.Instances)
            {
                if (instance.Kind != InstanceKind.Target)
                {
                    continue;
                }

                SqlModel sql = _sql.Generate(document, mapping, built, instance.Name);
                models.Add(new DbtModel(mapping.Name, instance.Name, instance.TransformationName, sql.Sql, sql.IsBlocked));
                foreach (Diagnostic diagnostic in sql.Diagnostics)
                {
                    if (diagnostic.Code.StartsWith('G'))
                    {
                        continue;
                    }

                    diagnostics.Add(diagnostic);
                }
            }
        }

        _writer.Write(outputDirectory, document, models);
        return new ConversionReport(
            document.Mappings.Count,
            document.Targets.Count,
            models.Count,
            outputDirectory,
            Distinct(diagnostics));
    }

    private static IReadOnlyList<Diagnostic> Distinct(List<Diagnostic> diagnostics)
    {
        var seen = new HashSet<string>(StringComparer.Ordinal);
        var unique = new List<Diagnostic>();
        foreach (Diagnostic diagnostic in diagnostics)
        {
            string key = diagnostic.Severity
                + "\n" + diagnostic.Mapping
                + "\n" + diagnostic.Instance
                + "\n" + diagnostic.Code
                + "\n" + diagnostic.Message;
            if (seen.Add(key))
            {
                unique.Add(diagnostic);
            }
        }

        return unique
            .OrderBy(diagnostic => diagnostic.Severity)
            .ThenBy(diagnostic => diagnostic.Mapping, StringComparer.Ordinal)
            .ThenBy(diagnostic => diagnostic.Instance, StringComparer.Ordinal)
            .ThenBy(diagnostic => diagnostic.Message, StringComparer.Ordinal)
            .ToArray();
    }
}
