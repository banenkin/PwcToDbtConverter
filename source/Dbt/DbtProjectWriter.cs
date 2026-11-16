using System.Text;
using PwcToDbtConverter.Model;
using PwcToDbtConverter.Sql;

namespace PwcToDbtConverter.Dbt;

/// <summary>
/// Writes <c>dbt_project.yml</c>, <c>models/sources.yml</c>, <c>models/schema.yml</c>, and one SQL file per target.
/// Staging mappings go under <c>models/staging</c>, core mappings under <c>models/marts/core</c>, and other mart mappings under <c>models/marts</c>.
/// </summary>
public sealed class DbtProjectWriter : IDbtProjectWriter
{
    private static readonly Encoding Utf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false);

    public IReadOnlyList<string> Write(string outputDirectory, PowerMartDocument document, IReadOnlyList<DbtModel> models)
    {
        IReadOnlyList<NamedModel> named = NameModels(models);
        var written = new List<string>();
        string projectName = ProjectName(document.FolderName);
        written.Add(WriteText(outputDirectory, "dbt_project.yml", ProjectFile(projectName)));
        written.Add(WriteText(outputDirectory, Path.Combine("models", "sources.yml"), SourcesFile(document)));
        written.Add(WriteText(outputDirectory, Path.Combine("models", "schema.yml"), SchemaFile(document, named)));

        foreach (NamedModel model in named)
        {
            written.Add(WriteText(outputDirectory, ModelPath(model.Model.MappingName, model.Name), Normalize(model.Model.Sql)));
        }

        return written;
    }

    private static IReadOnlyList<NamedModel> NameModels(IReadOnlyList<DbtModel> models)
    {
        IReadOnlyDictionary<string, string> names = DbtModelNames.Assign(
            models.Select(model => (model.MappingName, model.InstanceName)));
        return models
            .OrderBy(model => DbtModelNames.Sanitize(model.InstanceName), StringComparer.Ordinal)
            .ThenBy(model => model.MappingName, StringComparer.Ordinal)
            .ThenBy(model => model.InstanceName, StringComparer.Ordinal)
            .Select(model => new NamedModel(model, names[DbtModelNames.Key(model.MappingName, model.InstanceName)]))
            .ToArray();
    }

    private static string ProjectFile(string projectName)
    {
        return "name: " + projectName + "\n"
            + "version: \"1.0.0\"\n"
            + "config-version: 2\n"
            + "profile: " + projectName + "\n"
            + "\n"
            + "model-paths: [\"models\"]\n"
            + "\n"
            + "models:\n"
            + "  " + projectName + ":\n"
            + "    +materialized: table\n";
    }

    private static string SourcesFile(PowerMartDocument document)
    {
        SourceDef[] sources = document.Sources
            .Where(source => !DbtModelNames.IsProducedTarget(document, source.Name))
            .ToArray();
        var builder = new StringBuilder();
        builder.Append("version: 2\n\nsources:\n");
        if (sources.Length == 0)
        {
            builder.Append("  []\n");
            return builder.ToString();
        }

        foreach (IGrouping<string, SourceDef> group in sources
            .GroupBy(SourceName, StringComparer.Ordinal)
            .OrderBy(group => group.Key, StringComparer.Ordinal))
        {
            builder.Append("  - name: ").Append(Quote(group.Key)).Append('\n');
            builder.Append("    tables:\n");
            foreach (SourceDef table in group.OrderBy(source => source.Name, StringComparer.Ordinal))
            {
                builder.Append("      - name: ").Append(Quote(table.Name)).Append('\n');
                if (table.Fields.Count == 0)
                {
                    continue;
                }

                builder.Append("        columns:\n");
                foreach (FieldDef field in table.Fields.OrderBy(field => field.Name, StringComparer.Ordinal))
                {
                    builder.Append("          - name: ").Append(Quote(field.Name)).Append('\n');
                    builder.Append("            description: ").Append(Quote(Describe(field))).Append('\n');
                }
            }
        }

        return builder.ToString();
    }

    private static string SchemaFile(PowerMartDocument document, IReadOnlyList<NamedModel> models)
    {
        var builder = new StringBuilder();
        builder.Append("version: 2\n\nmodels:\n");
        if (models.Count == 0)
        {
            builder.Append("  []\n");
            return builder.ToString();
        }

        foreach (NamedModel named in models)
        {
            DbtModel model = named.Model;
            TargetDef? target = document.Targets.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, model.TargetName, StringComparison.Ordinal));
            builder.Append("  - name: ").Append(Quote(named.Name)).Append('\n');
            builder.Append("    description: ").Append(Quote(ModelDescription(model))).Append('\n');
            if (target is null || target.Fields.Count == 0)
            {
                continue;
            }

            builder.Append("    columns:\n");
            foreach (FieldDef field in target.Fields.OrderBy(field => field.Name, StringComparer.Ordinal))
            {
                builder.Append("      - name: ").Append(Quote(field.Name)).Append('\n');
                builder.Append("        description: ").Append(Quote(Describe(field))).Append('\n');
                if (field.KeyType != KeyType.Primary)
                {
                    continue;
                }

                builder.Append("        tests:\n");
                builder.Append("          - not_null\n");
                builder.Append("          - unique\n");
            }
        }

        return builder.ToString();
    }

    private static string ModelDescription(DbtModel model)
    {
        string text = "Target " + model.TargetName + " from mapping " + model.MappingName + ".";
        if (!string.Equals(model.InstanceName, model.TargetName, StringComparison.Ordinal))
        {
            text += " Instance " + model.InstanceName + ".";
        }

        if (model.IsBlocked)
        {
            text += " Generation was blocked by a graph error.";
        }

        return text;
    }

    private static string Describe(FieldDef field)
    {
        string type = field.DataType.Length == 0 ? "unknown" : field.DataType;
        if (field.Precision is null || type.Contains('(', StringComparison.Ordinal))
        {
            return type;
        }

        if (field.Scale is null)
        {
            return type + "(" + field.Precision.Value + ")";
        }

        return type + "(" + field.Precision.Value + "," + field.Scale.Value + ")";
    }

    private static string SourceName(SourceDef source)
    {
        if (source.OwnerName is not null)
        {
            return source.OwnerName;
        }

        return source.DbdName ?? "unknown";
    }

    private static string ModelPath(string mappingName, string modelName)
    {
        string file = modelName + ".sql";
        if (mappingName.Contains("STAGING", StringComparison.OrdinalIgnoreCase))
        {
            return Path.Combine("models", "staging", file);
        }

        if (mappingName.Contains("CORE", StringComparison.OrdinalIgnoreCase))
        {
            return Path.Combine("models", "marts", "core", file);
        }

        if (mappingName.Contains("MART", StringComparison.OrdinalIgnoreCase))
        {
            return Path.Combine("models", "marts", file);
        }

        return Path.Combine("models", file);
    }

    private static string ProjectName(string folderName)
    {
        return DbtModelNames.Sanitize(folderName);
    }

    private static string Quote(string value)
    {
        return "\"" + value.Replace("\\", "\\\\", StringComparison.Ordinal).Replace("\"", "\\\"", StringComparison.Ordinal) + "\"";
    }

    private static string Normalize(string sql)
    {
        string text = sql.Replace("\r\n", "\n", StringComparison.Ordinal).Replace('\r', '\n');
        if (!text.EndsWith('\n'))
        {
            text += "\n";
        }

        return text;
    }

    private static string WriteText(string outputDirectory, string relativePath, string contents)
    {
        string path = Path.Combine(outputDirectory, relativePath);
        string? directory = Path.GetDirectoryName(path);
        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        File.WriteAllText(path, contents, Utf8);
        return relativePath;
    }

    private sealed record NamedModel(DbtModel Model, string Name);
}
