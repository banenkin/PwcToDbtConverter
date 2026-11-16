using PwcToDbtConverter.Model;

namespace PwcToDbtConverter.Sql;

/// <summary>
/// Model names shared by SQL generation and the project writer, so a <c>ref()</c> matches the file that is written.
/// </summary>
public static class DbtModelNames
{
    public static string Key(string mapping, string instance)
    {
        return mapping + "\n" + instance;
    }

    public static string Sanitize(string name)
    {
        var chars = new System.Text.StringBuilder(name.Length);
        foreach (char value in name.ToLowerInvariant())
        {
            if (char.IsLetterOrDigit(value))
            {
                chars.Append(value);
                continue;
            }

            if (chars.Length > 0 && chars[^1] != '_')
            {
                chars.Append('_');
            }
        }

        string sanitized = chars.ToString().Trim('_');
        if (sanitized.Length == 0 || !char.IsLetter(sanitized[0]))
        {
            return "model";
        }

        return sanitized;
    }

    public static IReadOnlyDictionary<string, string> Assign(IEnumerable<(string Mapping, string Instance)> models)
    {
        var used = new HashSet<string>(StringComparer.Ordinal);
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach ((string Mapping, string Instance) model in models
            .OrderBy(model => Sanitize(model.Instance), StringComparer.Ordinal)
            .ThenBy(model => model.Mapping, StringComparer.Ordinal)
            .ThenBy(model => model.Instance, StringComparer.Ordinal))
        {
            names[Key(model.Mapping, model.Instance)] = Unique(Sanitize(model.Instance), used);
        }

        return names;
    }

    /// <summary>
    /// Model name for a source definition that another mapping writes as a target. Null when the table is external.
    /// </summary>
    public static string? RefFor(PowerMartDocument document, string currentMapping, string sourceDefinitionName)
    {
        var producers = new List<(string Mapping, string Instance)>();
        foreach (MappingDef mapping in document.Mappings)
        {
            if (string.Equals(mapping.Name, currentMapping, StringComparison.Ordinal))
            {
                continue;
            }

            foreach (InstanceDef instance in mapping.Instances)
            {
                if (instance.Kind == InstanceKind.Target
                    && string.Equals(instance.TransformationName, sourceDefinitionName, StringComparison.Ordinal))
                {
                    producers.Add((mapping.Name, instance.Name));
                }
            }
        }

        if (producers.Count == 0)
        {
            return null;
        }

        (string Mapping, string Instance) chosen = producers
            .OrderBy(producer => string.Equals(producer.Instance, sourceDefinitionName, StringComparison.Ordinal) ? 0 : 1)
            .ThenBy(producer => producer.Mapping, StringComparer.Ordinal)
            .ThenBy(producer => producer.Instance, StringComparer.Ordinal)
            .First();
        IReadOnlyDictionary<string, string> names = Assign(TargetInstances(document));
        return names[Key(chosen.Mapping, chosen.Instance)];
    }

    public static bool IsProducedTarget(PowerMartDocument document, string sourceDefinitionName)
    {
        return document.Mappings.Any(mapping => mapping.Instances.Any(instance =>
            instance.Kind == InstanceKind.Target
            && string.Equals(instance.TransformationName, sourceDefinitionName, StringComparison.Ordinal)));
    }

    private static IEnumerable<(string Mapping, string Instance)> TargetInstances(PowerMartDocument document)
    {
        foreach (MappingDef mapping in document.Mappings)
        {
            foreach (InstanceDef instance in mapping.Instances)
            {
                if (instance.Kind == InstanceKind.Target)
                {
                    yield return (mapping.Name, instance.Name);
                }
            }
        }
    }

    private static string Unique(string name, HashSet<string> used)
    {
        string candidate = name;
        int suffix = 2;
        while (!used.Add(candidate))
        {
            candidate = name + "_" + suffix;
            suffix++;
        }

        return candidate;
    }
}
