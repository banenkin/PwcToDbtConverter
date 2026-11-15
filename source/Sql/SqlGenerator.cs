using System.Text;
using PwcToDbtConverter.Graph;
using PwcToDbtConverter.Model;
using PwcToDbtConverter.Translation;

namespace PwcToDbtConverter.Sql;

/// <summary>
/// Turns the upstream closure of one target into a dbt model: one CTE per instance, in topological order,
/// then a final select of the target columns. Source instances are read with <c>{{ source(...) }}</c>.
/// </summary>
public sealed class SqlGenerator : IModelSqlGenerator
{
    private readonly IExpressionTranslator _translator;
    private readonly List<Diagnostic> _diagnostics = [];
    private readonly List<string> _comments = [];

    private PowerMartDocument _document = null!;
    private MappingDef _mapping = null!;
    private DataFlowGraph _graph = null!;
    private Dictionary<string, string?> _variables = new(StringComparer.Ordinal);

    public SqlGenerator(IExpressionTranslator translator)
    {
        _translator = translator;
    }

    public SqlModel Generate(PowerMartDocument document, MappingDef mapping, GraphBuildResult built, string targetInstance)
    {
        _document = document;
        _mapping = mapping;
        _graph = built.Graph;
        _diagnostics.Clear();
        _comments.Clear();
        _variables = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (MappingVariableDef variable in mapping.Variables)
        {
            _variables.TryAdd(variable.Name, variable.DefaultValue);
        }

        if (!_graph.Nodes.ContainsKey(targetInstance))
        {
            string message = "Target instance '" + targetInstance + "' is not in mapping '" + mapping.Name + "'.";
            _diagnostics.Add(Warning(targetInstance, message));
            return Finish("-- TODO [UNSUPPORTED]: " + message, blocked: true);
        }

        var ancestors = new HashSet<string>(_graph.UpstreamOf(targetInstance), StringComparer.Ordinal);
        var relevant = new HashSet<string>(ancestors, StringComparer.Ordinal) { targetInstance };
        foreach (Diagnostic diagnostic in built.Diagnostics)
        {
            if (diagnostic.Instance is null || relevant.Contains(diagnostic.Instance))
            {
                _diagnostics.Add(diagnostic);
            }
        }

        if (!_graph.IsAcyclic || _graph.TopologicalOrder is null)
        {
            _diagnostics.Add(Warning(targetInstance, "Mapping '" + mapping.Name + "' has a cycle, so '" + targetInstance + "' was not generated."));
            return Finish("-- TODO [UNSUPPORTED]: mapping '" + mapping.Name + "' has a cycle.", blocked: true);
        }

        Diagnostic? blocking = _diagnostics.FirstOrDefault(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        if (blocking is not null)
        {
            return Finish("-- TODO [UNSUPPORTED]: " + blocking.Message, blocked: true);
        }

        var steps = new List<(string Name, string Body)>();
        foreach (string instanceName in _graph.TopologicalOrder)
        {
            if (!ancestors.Contains(instanceName))
            {
                continue;
            }

            steps.Add((CteName(instanceName), Emit(instanceName)));
        }

        string sql = Compose(steps, EmitTarget(targetInstance));
        return Finish(sql, blocked: false);
    }

    private SqlModel Finish(string sql, bool blocked)
    {
        if (!sql.EndsWith('\n'))
        {
            sql += "\n";
        }

        return new SqlModel(sql, _diagnostics.ToArray(), blocked);
    }

    private string Emit(string instanceName)
    {
        _comments.Clear();
        InstanceDef instance = _graph.Nodes[instanceName];
        string body = instance.Kind switch
        {
            InstanceKind.Source => EmitSource(instance),
            InstanceKind.Transformation => EmitTransformation(instance),
            _ => EmitUnsupported(instance.Name, "Instance '" + instance.Name + "' is a target used as an upstream step."),
        };
        return Prefix(_comments, body);
    }

    private string EmitSource(InstanceDef instance)
    {
        SourceDef? source = _document.Sources.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, instance.TransformationName, StringComparison.Ordinal));
        if (source is null)
        {
            return EmitUnsupported(instance.Name, "Source '" + instance.Name + "' has no source definition.");
        }

        var connected = _graph.Edges
            .Where(edge => string.Equals(edge.FromInstance, instance.Name, StringComparison.Ordinal))
            .Select(edge => edge.FromField)
            .ToHashSet(StringComparer.Ordinal);
        string[] fields = source.Fields
            .Where(field => connected.Contains(field.Name))
            .Select(field => Id(field.Name))
            .ToArray();
        if (fields.Length == 0)
        {
            fields = source.Fields.Select(field => Id(field.Name)).ToArray();
        }

        if (fields.Length == 0)
        {
            fields = ["null as missing"];
        }

        string schema = source.OwnerName ?? source.DbdName ?? "unknown";
        if (source.OwnerName is null && source.DbdName is null)
        {
            Unsupported(instance.Name, "Source '" + instance.Name + "' has no owner or connection name.");
        }

        return Select(fields) + "\nfrom " + SourceCall(schema, source.Name);
    }

    private string EmitTransformation(InstanceDef instance)
    {
        TransformationDef? transformation = _mapping.Transformations.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, instance.TransformationName, StringComparison.Ordinal));
        if (transformation is null)
        {
            return EmitUnsupported(instance.Name, "Instance '" + instance.Name + "' has no transformation definition.");
        }

        return transformation.Kind switch
        {
            TransformationKind.SourceQualifier => EmitQualifier(instance, transformation),
            TransformationKind.Expression => EmitExpression(instance, transformation),
            TransformationKind.Filter => EmitFilter(instance, transformation),
            TransformationKind.Joiner => EmitJoiner(instance, transformation),
            TransformationKind.Sorter => EmitSorter(instance, transformation),
            TransformationKind.Aggregator => EmitAggregator(instance, transformation),
            TransformationKind.Lookup => EmitLookup(instance, transformation),
            TransformationKind.Router => EmitUnsupported(instance.Name, "Router '" + instance.Name + "' is not translated.", transformation),
            TransformationKind.Union => EmitUnsupported(instance.Name, "Union '" + instance.Name + "' is not translated.", transformation),
            _ => EmitUnsupported(instance.Name, "Transformation '" + instance.Name + "' (" + transformation.Kind + ") is not translated.", transformation),
        };
    }

    private string EmitQualifier(InstanceDef instance, TransformationDef transformation)
    {
        foreach (string attribute in new[] { "Sql Query", "Source Filter", "User Defined Join" })
        {
            if (Attribute(transformation, attribute) is not null)
            {
                Unsupported(instance.Name, attribute + " on '" + instance.Name + "' is not translated.");
            }
        }

        bool distinct = string.Equals(Attribute(transformation, "Select Distinct"), "YES", StringComparison.Ordinal);
        return Select(PassThrough(instance, transformation, allowAggregates: false), distinct)
            + "\nfrom "
            + Relation(instance.Name, warnOnMany: true);
    }

    private string EmitExpression(InstanceDef instance, TransformationDef transformation)
    {
        string scope = Relation(instance.Name, warnOnMany: true);
        foreach (PortDef variable in Variables(transformation))
        {
            if (IsRowOrder(variable))
            {
                Unsupported(
                    instance.Name,
                    "Row-order variable " + variable.Name + " on '" + instance.Name + "' depends on the previous row.");
            }

            string expression = string.IsNullOrWhiteSpace(variable.Expression)
                ? "null"
                : Translate(variable.Expression, allowAggregates: false, instance.Name);
            scope = "(\n" + Select(["*", Project(expression, variable.Name)]) + "\nfrom " + scope + "\n)";
        }

        return Select(PassThrough(instance, transformation, allowAggregates: false)) + "\nfrom " + scope;
    }

    private string EmitFilter(InstanceDef instance, TransformationDef transformation)
    {
        string body = Select(PassThrough(instance, transformation, allowAggregates: false))
            + "\nfrom "
            + Relation(instance.Name, warnOnMany: true);
        string? condition = Attribute(transformation, "Filter Condition");
        if (condition is null)
        {
            Unsupported(instance.Name, "Filter '" + instance.Name + "' has no filter condition.");
            return body;
        }

        return body + "\nwhere " + Translate(condition, allowAggregates: false, instance.Name);
    }

    private string EmitSorter(InstanceDef instance, TransformationDef transformation)
    {
        _comments.Add("-- Sorter '" + instance.Name + "' does not change the row set.");
        return Select(PassThrough(instance, transformation, allowAggregates: false))
            + "\nfrom "
            + Relation(instance.Name, warnOnMany: true);
    }

    private string EmitAggregator(InstanceDef instance, TransformationDef transformation)
    {
        var items = new List<string>();
        var groups = new List<string>();
        foreach (PortDef port in Outputs(transformation))
        {
            string expression = string.IsNullOrWhiteSpace(port.Expression)
                ? Id(port.Name)
                : Translate(port.Expression, allowAggregates: true, instance.Name);
            items.Add(Project(expression, port.Name));
            if (port.ExpressionKind == ExpressionKind.GroupBy)
            {
                groups.Add(expression);
                continue;
            }

            if (string.IsNullOrWhiteSpace(port.Expression))
            {
                Unsupported(instance.Name, "Aggregator output '" + port.Name + "' is neither grouped nor aggregated.");
            }
        }

        if (items.Count == 0)
        {
            items.Add("null as missing");
        }

        string body = Select(items) + "\nfrom " + Relation(instance.Name, warnOnMany: true);
        if (groups.Count == 0)
        {
            Unsupported(instance.Name, "Aggregator '" + instance.Name + "' has no group-by port.");
            return body;
        }

        return body + "\ngroup by " + string.Join(", ", groups);
    }

    private string EmitJoiner(InstanceDef instance, TransformationDef transformation)
    {
        IReadOnlyList<ConnectorDef> incoming = Incoming(instance.Name);
        var masterPorts = transformation.Ports
            .Where(port => port.Type.HasFlag(PortType.Master))
            .Select(port => port.Name)
            .ToHashSet(StringComparer.Ordinal);
        string? master = incoming.FirstOrDefault(connector => masterPorts.Contains(connector.ToField))?.FromInstance;
        string? detail = incoming
            .Select(connector => connector.FromInstance)
            .FirstOrDefault(name => !string.Equals(name, master, StringComparison.Ordinal));
        if (master is null || detail is null)
        {
            return EmitUnsupported(instance.Name, "Joiner '" + instance.Name + "' needs a master side and a detail side.", transformation);
        }

        int sides = incoming.Select(connector => connector.FromInstance).Distinct(StringComparer.Ordinal).Count();
        if (sides > 2)
        {
            Unsupported(instance.Name, "Joiner '" + instance.Name + "' has more than two upstream instances.");
        }

        string type = Attribute(transformation, "Join Type") ?? "Normal Join";
        string keyword;
        string left;
        string right;
        if (string.Equals(type, "Master Outer Join", StringComparison.Ordinal))
        {
            keyword = "left join";
            left = master;
            right = detail;
        }
        else if (string.Equals(type, "Detail Outer Join", StringComparison.Ordinal))
        {
            keyword = "left join";
            left = detail;
            right = master;
        }
        else if (string.Equals(type, "Full Outer Join", StringComparison.Ordinal))
        {
            keyword = "full outer join";
            left = master;
            right = detail;
        }
        else if (string.Equals(type, "Normal Join", StringComparison.Ordinal))
        {
            keyword = "inner join";
            left = master;
            right = detail;
        }
        else
        {
            Unsupported(instance.Name, "Join type '" + type + "' on '" + instance.Name + "' is not translated.");
            keyword = "inner join";
            left = master;
            right = detail;
        }

        string? condition = Attribute(transformation, "Join Condition");
        string on = condition is null
            ? "1 = 1"
            : Translate(condition, allowAggregates: false, instance.Name);
        if (condition is null)
        {
            Unsupported(instance.Name, "Joiner '" + instance.Name + "' has no join condition.");
        }

        var items = new List<string>();
        foreach (PortDef port in Outputs(transformation))
        {
            string side = masterPorts.Contains(port.Name) ? "master" : "detail";
            string expression = string.IsNullOrWhiteSpace(port.Expression)
                ? side + "." + Id(port.Name)
                : Translate(port.Expression, allowAggregates: false, instance.Name);
            items.Add(Project(expression, port.Name));
        }

        if (items.Count == 0)
        {
            items.Add("null as missing");
        }

        return Select(items)
            + "\nfrom "
            + Side(transformation, incoming, left, string.Equals(left, master, StringComparison.Ordinal) ? "master" : "detail")
            + "\n"
            + keyword
            + " "
            + Side(transformation, incoming, right, string.Equals(right, master, StringComparison.Ordinal) ? "master" : "detail")
            + "\n    on "
            + on;
    }

    private string EmitLookup(InstanceDef instance, TransformationDef transformation)
    {
        string? table = Attribute(transformation, "Lookup table name");
        string? owner = Attribute(transformation, "Lookup source name");
        string? condition = Attribute(transformation, "Lookup condition");
        if (table is null || condition is null)
        {
            return EmitUnsupported(
                instance.Name,
                "Lookup '" + instance.Name + "' is missing a lookup table or condition.",
                transformation);
        }

        if (owner is null)
        {
            SourceDef? source = _document.Sources.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, table, StringComparison.Ordinal));
            owner = source?.OwnerName ?? source?.DbdName;
        }

        if (owner is null)
        {
            Unsupported(instance.Name, "Lookup '" + instance.Name + "' has no source owner.");
            owner = "unknown";
        }

        IReadOnlyList<ConnectorDef> incoming = Incoming(instance.Name);
        var items = new List<string>();
        foreach (PortDef port in Outputs(transformation))
        {
            bool fromLookup = !incoming.Any(connector => string.Equals(connector.ToField, port.Name, StringComparison.Ordinal));
            string column = fromLookup ? port.RefField ?? port.Name : port.Name;
            string expression = (fromLookup ? "lookup." : "input.") + Id(column);
            items.Add(Project(expression, port.Name));
        }

        if (items.Count == 0)
        {
            items.Add("null as missing");
        }

        string on = Translate(condition, allowAggregates: false, instance.Name);
        return Select(items)
            + "\nfrom "
            + Relation(instance.Name, warnOnMany: true)
            + " input\nleft join "
            + SourceCall(owner, table)
            + " lookup\n    on "
            + on;
    }

    private string EmitUnsupported(string instanceName, string message, TransformationDef? transformation = null)
    {
        Unsupported(instanceName, message);
        IReadOnlyList<ConnectorDef> incoming = Incoming(instanceName);
        var items = new List<string>();
        if (transformation is not null)
        {
            foreach (PortDef port in Outputs(transformation))
            {
                bool connected = incoming.Any(connector => string.Equals(connector.ToField, port.Name, StringComparison.Ordinal));
                items.Add(connected ? Id(port.Name) : "null as " + Id(port.Name));
            }
        }

        if (items.Count == 0)
        {
            items.Add("null as missing");
        }

        string sql = Select(items);
        if (incoming.Count > 0)
        {
            sql += "\nfrom " + Relation(instanceName, warnOnMany: false);
        }

        return sql;
    }

    private string EmitTarget(string targetName)
    {
        var comments = new List<string>();
        InstanceDef target = _graph.Nodes[targetName];
        TargetDef? definition = _document.Targets.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, target.TransformationName, StringComparison.Ordinal));
        if (definition is null)
        {
            comments.Add("-- TODO [UNSUPPORTED]: target '" + targetName + "' has no target definition.");
            _diagnostics.Add(Warning(targetName, "Target '" + targetName + "' has no target definition."));
            return Prefix(comments, "select\n    null as missing");
        }

        IReadOnlyList<ConnectorDef> incoming = Incoming(targetName);
        string? fromInstance = incoming.FirstOrDefault()?.FromInstance;
        int sources = incoming.Select(connector => connector.FromInstance).Distinct(StringComparer.Ordinal).Count();
        if (fromInstance is null)
        {
            comments.Add("-- TODO [UNSUPPORTED]: target '" + targetName + "' has no upstream instance.");
            _diagnostics.Add(Warning(targetName, "Target '" + targetName + "' has no upstream instance."));
        }
        else if (sources > 1)
        {
            comments.Add("-- TODO [UNSUPPORTED]: target '" + targetName + "' has more than one upstream instance.");
            _diagnostics.Add(Warning(targetName, "Target '" + targetName + "' has more than one upstream instance."));
        }

        var items = new List<string>();
        foreach (FieldDef field in definition.Fields)
        {
            ConnectorDef? connector = _graph.InputOf(new PortRef(targetName, field.Name));
            if (connector is null || fromInstance is null || !string.Equals(connector.FromInstance, fromInstance, StringComparison.Ordinal))
            {
                if (connector is null)
                {
                    _diagnostics.Add(Warning(targetName, "Target field '" + field.Name + "' has no incoming connector."));
                }

                items.Add("null as " + Id(field.Name));
                continue;
            }

            items.Add(Project(Id(connector.FromField), field.Name));
        }

        if (items.Count == 0)
        {
            items.Add("null as missing");
        }

        string sql = Select(items);
        if (fromInstance is not null)
        {
            sql += "\nfrom " + CteName(fromInstance);
        }

        return Prefix(comments, sql);
    }

    private string Relation(string instanceName, bool warnOnMany)
    {
        IReadOnlyList<ConnectorDef> incoming = Incoming(instanceName);
        if (incoming.Count == 0)
        {
            Unsupported(instanceName, "Transformation '" + instanceName + "' has no upstream instance.");
            return "(select null as missing)";
        }

        string fromInstance = incoming[0].FromInstance;
        ConnectorDef[] mine = incoming
            .Where(connector => string.Equals(connector.FromInstance, fromInstance, StringComparison.Ordinal))
            .ToArray();
        if (warnOnMany && incoming.Any(connector => !string.Equals(connector.FromInstance, fromInstance, StringComparison.Ordinal)))
        {
            Unsupported(instanceName, "Transformation '" + instanceName + "' has more than one upstream instance.");
        }

        bool rename = mine.Any(connector => !string.Equals(connector.FromField, connector.ToField, StringComparison.Ordinal));
        if (!rename)
        {
            return CteName(fromInstance);
        }

        string[] items = mine.Select(connector => Project(Id(connector.FromField), connector.ToField)).ToArray();
        return "(\n" + Select(items) + "\nfrom " + CteName(fromInstance) + "\n)";
    }

    private string Side(TransformationDef transformation, IReadOnlyList<ConnectorDef> incoming, string instanceName, string alias)
    {
        ConnectorDef[] mine = incoming
            .Where(connector => string.Equals(connector.FromInstance, instanceName, StringComparison.Ordinal))
            .OrderBy(connector => PortPosition(transformation, connector.ToField))
            .ToArray();
        string[] items = mine.Length == 0
            ? ["null as missing"]
            : mine.Select(connector => Project(Id(connector.FromField), connector.ToField)).ToArray();
        return "(\n" + Select(items) + "\nfrom " + CteName(instanceName) + "\n) " + alias;
    }

    private IReadOnlyList<string> PassThrough(InstanceDef instance, TransformationDef transformation, bool allowAggregates)
    {
        IReadOnlyList<ConnectorDef> incoming = Incoming(instance.Name);
        var items = new List<string>();
        foreach (PortDef port in Outputs(transformation))
        {
            if (!string.IsNullOrWhiteSpace(port.Expression))
            {
                string sql = Translate(port.Expression, allowAggregates, instance.Name);
                items.Add(Project(sql, port.Name));
                continue;
            }

            if (incoming.Any(connector => string.Equals(connector.ToField, port.Name, StringComparison.Ordinal)))
            {
                items.Add(Id(port.Name));
                continue;
            }

            Unsupported(instance.Name, "Output '" + port.Name + "' on '" + instance.Name + "' has no expression and no incoming connector.");
            items.Add("null as " + Id(port.Name));
        }

        if (items.Count == 0)
        {
            items.Add("null as missing");
        }

        return items;
    }

    private string Translate(string expression, bool allowAggregates, string instance)
    {
        ExpressionTranslation translated = _translator.Translate(
            expression,
            new ExpressionContext(allowAggregates, _variables, _mapping.Name, instance));
        _diagnostics.AddRange(translated.Diagnostics);
        if (translated.Sql.Contains("/* UNSUPPORTED:", StringComparison.Ordinal))
        {
            string line = "-- TODO [UNSUPPORTED]: expression on '" + instance + "' has unmapped logic.";
            if (!_comments.Contains(line))
            {
                _comments.Add(line);
            }
        }

        return translated.Sql;
    }

    private IReadOnlyList<ConnectorDef> Incoming(string instanceName)
    {
        return _graph.Edges
            .Where(edge => string.Equals(edge.ToInstance, instanceName, StringComparison.Ordinal))
            .ToArray();
    }

    private static IEnumerable<PortDef> Outputs(TransformationDef transformation)
    {
        return transformation.Ports
            .Where(port => port.Type.HasFlag(PortType.Output))
            .OrderBy(port => port.Position)
            .ThenBy(port => port.Name, StringComparer.Ordinal);
    }

    private static IEnumerable<PortDef> Variables(TransformationDef transformation)
    {
        return transformation.Ports
            .Where(port => port.Type.HasFlag(PortType.Variable)
                && !port.Type.HasFlag(PortType.Output)
                && !port.Type.HasFlag(PortType.Input))
            .OrderBy(port => port.Position)
            .ThenBy(port => port.Name, StringComparer.Ordinal);
    }

    private static bool IsRowOrder(PortDef port)
    {
        if (string.IsNullOrWhiteSpace(port.Expression))
        {
            return false;
        }

        if (port.Name.StartsWith("v_PREV_", StringComparison.Ordinal))
        {
            return true;
        }

        return ContainsIdentifier(port.Expression, port.Name);
    }

    private static bool ContainsIdentifier(string expression, string name)
    {
        int index = 0;
        while ((index = expression.IndexOf(name, index, StringComparison.Ordinal)) >= 0)
        {
            bool left = index == 0 || !IsIdentifierChar(expression[index - 1]);
            int end = index + name.Length;
            bool right = end >= expression.Length || !IsIdentifierChar(expression[end]);
            if (left && right)
            {
                return true;
            }

            index = end;
        }

        return false;
    }

    private static bool IsIdentifierChar(char value)
    {
        return char.IsLetterOrDigit(value) || value == '_';
    }

    private static int PortPosition(TransformationDef transformation, string port)
    {
        PortDef? found = transformation.Ports.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, port, StringComparison.Ordinal));
        return found?.Position ?? int.MaxValue;
    }

    private static string? Attribute(TransformationDef transformation, string name)
    {
        return transformation.Attributes.TryGetValue(name, out string? value) ? value : null;
    }

    private void Unsupported(string instance, string message)
    {
        string line = "-- TODO [UNSUPPORTED]: " + message;
        if (!_comments.Contains(line))
        {
            _comments.Add(line);
        }

        _diagnostics.Add(Warning(instance, message));
    }

    private Diagnostic Warning(string? instance, string message)
    {
        return new Diagnostic(DiagnosticSeverity.Warning, _mapping.Name, instance, message, "S001");
    }

    private static string Compose(IReadOnlyList<(string Name, string Body)> steps, string finalSelect)
    {
        if (steps.Count == 0)
        {
            return finalSelect;
        }

        var builder = new StringBuilder();
        builder.Append("with ");
        for (int index = 0; index < steps.Count; index++)
        {
            if (index > 0)
            {
                builder.Append(",\n\n");
            }

            builder.Append(steps[index].Name);
            builder.Append(" as (\n");
            builder.Append(Indent(steps[index].Body));
            builder.Append("\n)");
        }

        builder.Append("\n\n");
        builder.Append(finalSelect);
        return builder.ToString();
    }

    private static string Select(IReadOnlyList<string> items, bool distinct = false)
    {
        string keyword = distinct ? "select distinct" : "select";
        return keyword + "\n    " + string.Join(",\n    ", items);
    }

    private static string Project(string expression, string alias)
    {
        string name = Id(alias);
        return string.Equals(expression, name, StringComparison.Ordinal) ? name : expression + " as " + name;
    }

    private static string Prefix(IReadOnlyList<string> comments, string sql)
    {
        if (comments.Count == 0)
        {
            return sql;
        }

        return string.Join("\n", comments) + "\n" + sql;
    }

    private static string Indent(string sql)
    {
        string[] lines = sql.Split('\n');
        return string.Join("\n", lines.Select(line => line.Length == 0 ? line : "    " + line));
    }

    private static string CteName(string instanceName)
    {
        return Id(instanceName.ToLowerInvariant());
    }

    private static string Id(string name)
    {
        if (name.Length > 0 && name.All(value => char.IsLetterOrDigit(value) || value == '_'))
        {
            return name;
        }

        return "\"" + name.Replace("\"", "\"\"", StringComparison.Ordinal) + "\"";
    }

    private static string SourceCall(string schema, string table)
    {
        return "{{ source('" + Escape(schema) + "', '" + Escape(table) + "') }}";
    }

    private static string Escape(string value)
    {
        return value.Replace("'", "''", StringComparison.Ordinal);
    }
}
