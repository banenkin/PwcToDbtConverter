using System.Collections.Frozen;
using PwcToDbtConverter.Model;

namespace PwcToDbtConverter.Graph;

/// <summary>
/// Indexes one mapping by instance name and checks every connector before it becomes an edge.
/// In-degree counts distinct upstream instances, not connectors.
/// </summary>
public sealed class DataFlowGraphBuilder : IDataFlowGraphBuilder
{
    public GraphBuildResult Build(MappingDef mapping, PowerMartDocument document)
    {
        var diagnostics = new List<Diagnostic>();
        Dictionary<string, InstanceDef> nodes = IndexInstances(mapping, diagnostics);
        Dictionary<string, PortSet> ports = IndexPorts(mapping, document, nodes, diagnostics);
        List<ConnectorDef> edges = ValidateConnectors(mapping, nodes, ports, diagnostics);
        Dictionary<string, List<string>> upstream = BuildLinks(nodes.Keys, edges, outgoing: false);
        Dictionary<string, List<string>> downstream = BuildLinks(nodes.Keys, edges, outgoing: true);
        Dictionary<PortRef, ConnectorDef> inputs = IndexInputs(edges);
        IReadOnlyList<string>? order = TopologicalOrder(nodes.Keys, upstream, downstream, mapping.Name, diagnostics);
        CheckNodes(mapping, nodes, upstream, downstream, diagnostics);

        var graph = new DataFlowGraph(
            mapping.Name,
            nodes.ToFrozenDictionary(StringComparer.Ordinal),
            edges,
            Freeze(upstream),
            Freeze(downstream),
            inputs.ToFrozenDictionary(),
            order);
        return new GraphBuildResult(graph, diagnostics);
    }

    private static Dictionary<string, InstanceDef> IndexInstances(MappingDef mapping, List<Diagnostic> diagnostics)
    {
        var nodes = new Dictionary<string, InstanceDef>(StringComparer.Ordinal);
        foreach (InstanceDef instance in mapping.Instances)
        {
            if (nodes.ContainsKey(instance.Name))
            {
                diagnostics.Add(Error(mapping.Name, instance.Name, "G017", $"Duplicate instance name '{instance.Name}'. The first instance is kept."));
                continue;
            }

            nodes.Add(instance.Name, instance);
        }

        return nodes;
    }

    private static Dictionary<string, PortSet> IndexPorts(
        MappingDef mapping,
        PowerMartDocument document,
        Dictionary<string, InstanceDef> nodes,
        List<Diagnostic> diagnostics)
    {
        var ports = new Dictionary<string, PortSet>(StringComparer.Ordinal);
        foreach ((string name, InstanceDef instance) in nodes)
        {
            ports.Add(name, ResolvePorts(mapping, document, instance, diagnostics));
        }

        return ports;
    }

    private static PortSet ResolvePorts(
        MappingDef mapping,
        PowerMartDocument document,
        InstanceDef instance,
        List<Diagnostic> diagnostics)
    {
        IReadOnlyList<PortFact> facts = instance.Kind switch
        {
            InstanceKind.Source => SourcePorts(document, instance),
            InstanceKind.Target => TargetPorts(document, instance),
            _ => TransformationPorts(mapping, instance),
        };

        if (facts.Count == 0 && instance.Kind == InstanceKind.Transformation
            && mapping.Transformations.All(transformation => !string.Equals(transformation.Name, instance.TransformationName, StringComparison.Ordinal)))
        {
            diagnostics.Add(Error(mapping.Name, instance.Name, "G011", $"Instance '{instance.Name}' has no definition named '{instance.TransformationName}'."));
        }

        if (instance.Kind == InstanceKind.Source
            && document.Sources.All(source => !string.Equals(source.Name, instance.TransformationName, StringComparison.Ordinal)))
        {
            diagnostics.Add(Error(mapping.Name, instance.Name, "G011", $"Instance '{instance.Name}' has no source named '{instance.TransformationName}'."));
        }

        if (instance.Kind == InstanceKind.Target
            && document.Targets.All(target => !string.Equals(target.Name, instance.TransformationName, StringComparison.Ordinal)))
        {
            diagnostics.Add(Error(mapping.Name, instance.Name, "G011", $"Instance '{instance.Name}' has no target named '{instance.TransformationName}'."));
        }

        var indexed = new Dictionary<string, PortFact>(StringComparer.Ordinal);
        foreach (IGrouping<string, PortFact> group in facts.GroupBy(fact => fact.Name, StringComparer.Ordinal))
        {
            if (group.Count() > 1)
            {
                diagnostics.Add(Error(mapping.Name, instance.Name, "G018", $"Duplicate port name '{group.Key}' on '{instance.Name}'."));
                continue;
            }

            indexed.Add(group.Key, group.First());
        }

        return new PortSet(indexed);
    }

    private static IReadOnlyList<PortFact> SourcePorts(PowerMartDocument document, InstanceDef instance)
    {
        SourceDef? source = document.Sources.FirstOrDefault(candidate => string.Equals(candidate.Name, instance.TransformationName, StringComparison.Ordinal));
        if (source is null)
        {
            return [];
        }

        return source.Fields.Select(field => new PortFact(field.Name, true, false, false)).ToArray();
    }

    private static IReadOnlyList<PortFact> TargetPorts(PowerMartDocument document, InstanceDef instance)
    {
        TargetDef? target = document.Targets.FirstOrDefault(candidate => string.Equals(candidate.Name, instance.TransformationName, StringComparison.Ordinal));
        if (target is null)
        {
            return [];
        }

        return target.Fields.Select(field => new PortFact(field.Name, false, true, false)).ToArray();
    }

    private static IReadOnlyList<PortFact> TransformationPorts(MappingDef mapping, InstanceDef instance)
    {
        TransformationDef? transformation = mapping.Transformations.FirstOrDefault(candidate =>
            string.Equals(candidate.Name, instance.TransformationName, StringComparison.Ordinal));
        if (transformation is null)
        {
            return [];
        }

        return transformation.Ports.Select(port => new PortFact(
            port.Name,
            port.Type.HasFlag(PortType.Output),
            port.Type.HasFlag(PortType.Input),
            port.Type.HasFlag(PortType.Variable) && !port.Type.HasFlag(PortType.Output) && !port.Type.HasFlag(PortType.Input))).ToArray();
    }

    private static List<ConnectorDef> ValidateConnectors(
        MappingDef mapping,
        Dictionary<string, InstanceDef> nodes,
        Dictionary<string, PortSet> ports,
        List<Diagnostic> diagnostics)
    {
        var edges = new List<ConnectorDef>();
        var fed = new HashSet<PortRef>();
        foreach (ConnectorDef connector in mapping.Connectors)
        {
            if (!nodes.ContainsKey(connector.FromInstance))
            {
                diagnostics.Add(Error(mapping.Name, connector.ToInstance, "G002", $"Connector from unknown instance '{connector.FromInstance}'."));
                continue;
            }

            if (!nodes.ContainsKey(connector.ToInstance))
            {
                diagnostics.Add(Error(mapping.Name, connector.FromInstance, "G003", $"Connector to unknown instance '{connector.ToInstance}'."));
                continue;
            }

            if (!ports[connector.FromInstance].IsOutput(connector.FromField, out string fromReason))
            {
                diagnostics.Add(Error(mapping.Name, connector.ToInstance, "G004", $"Field '{connector.FromField}' on '{connector.FromInstance}' is {fromReason}."));
                continue;
            }

            if (!ports[connector.ToInstance].IsInput(connector.ToField, out string toReason))
            {
                diagnostics.Add(Error(mapping.Name, connector.ToInstance, "G005", $"Field '{connector.ToField}' on '{connector.ToInstance}' is {toReason}."));
                continue;
            }

            if (nodes[connector.FromInstance].Kind == InstanceKind.Target || nodes[connector.ToInstance].Kind == InstanceKind.Source)
            {
                diagnostics.Add(Error(mapping.Name, connector.ToInstance, "G006", $"Connector flows out of a target or into a source ({connector.FromInstance} -> {connector.ToInstance})."));
                continue;
            }

            var input = new PortRef(connector.ToInstance, connector.ToField);
            if (!fed.Add(input))
            {
                diagnostics.Add(Error(mapping.Name, connector.ToInstance, "G007", $"Input '{connector.ToInstance}.{connector.ToField}' already has a connector."));
                continue;
            }

            edges.Add(connector);
        }

        return edges;
    }

    private static Dictionary<string, List<string>> BuildLinks(IEnumerable<string> names, List<ConnectorDef> edges, bool outgoing)
    {
        var links = names.ToDictionary(name => name, _ => new List<string>(), StringComparer.Ordinal);
        foreach (ConnectorDef edge in edges)
        {
            string key = outgoing ? edge.FromInstance : edge.ToInstance;
            string value = outgoing ? edge.ToInstance : edge.FromInstance;
            List<string> list = links[key];
            if (!list.Contains(value, StringComparer.Ordinal))
            {
                list.Add(value);
            }
        }

        foreach (List<string> list in links.Values)
        {
            list.Sort(StringComparer.Ordinal);
        }

        return links;
    }

    private static Dictionary<PortRef, ConnectorDef> IndexInputs(List<ConnectorDef> edges)
    {
        var inputs = new Dictionary<PortRef, ConnectorDef>();
        foreach (ConnectorDef edge in edges)
        {
            inputs[new PortRef(edge.ToInstance, edge.ToField)] = edge;
        }

        return inputs;
    }

    private static IReadOnlyList<string>? TopologicalOrder(
        IEnumerable<string> names,
        Dictionary<string, List<string>> upstream,
        Dictionary<string, List<string>> downstream,
        string mapping,
        List<Diagnostic> diagnostics)
    {
        var indegree = names.ToDictionary(name => name, name => upstream[name].Count, StringComparer.Ordinal);
        var ready = new SortedSet<string>(indegree.Where(pair => pair.Value == 0).Select(pair => pair.Key), StringComparer.Ordinal);
        var order = new List<string>();
        while (ready.Count > 0)
        {
            string? nextName = ready.Min;
            if (nextName is null)
            {
                break;
            }

            ready.Remove(nextName);
            string name = nextName;
            order.Add(name);
            foreach (string next in downstream[name])
            {
                indegree[next]--;
                if (indegree[next] == 0)
                {
                    ready.Add(next);
                }
            }
        }

        if (order.Count == indegree.Count)
        {
            return order;
        }

        string path = CyclePath(indegree, upstream);
        diagnostics.Add(Error(mapping, path.Split(" -> ")[0], "G001", "Cycle " + path + "."));
        return null;
    }

    private static string CyclePath(Dictionary<string, int> indegree, Dictionary<string, List<string>> upstream)
    {
        var leftover = new HashSet<string>(indegree.Where(pair => pair.Value > 0).Select(pair => pair.Key), StringComparer.Ordinal);
        string start = leftover.OrderBy(name => name, StringComparer.Ordinal).First();
        var walked = new List<string>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        string current = start;
        while (leftover.Contains(current) && seen.Add(current))
        {
            walked.Add(current);
            string? next = upstream[current].FirstOrDefault(name => leftover.Contains(name));
            if (next is null)
            {
                break;
            }

            current = next;
        }

        walked.Add(current);
        walked.Reverse();
        return string.Join(" -> ", walked);
    }

    private static void CheckNodes(
        MappingDef mapping,
        Dictionary<string, InstanceDef> nodes,
        Dictionary<string, List<string>> upstream,
        Dictionary<string, List<string>> downstream,
        List<Diagnostic> diagnostics)
    {
        foreach ((string name, InstanceDef instance) in nodes)
        {
            int incoming = upstream[name].Count;
            int outgoing = downstream[name].Count;
            if (instance.Kind == InstanceKind.Target && incoming == 0)
            {
                diagnostics.Add(Warning(mapping.Name, name, "G008", $"Target '{name}' has no incoming connector."));
            }

            if (instance.Kind == InstanceKind.Source && outgoing == 0)
            {
                diagnostics.Add(Info(mapping.Name, name, "G009", $"Source '{name}' has no outgoing connector."));
            }

            if (instance.Kind != InstanceKind.Transformation)
            {
                continue;
            }

            TransformationDef? transformation = mapping.Transformations.FirstOrDefault(candidate =>
                string.Equals(candidate.Name, instance.TransformationName, StringComparison.Ordinal));
            if (transformation is null)
            {
                continue;
            }

            if (transformation.Kind == TransformationKind.Joiner && incoming < 2)
            {
                diagnostics.Add(Warning(mapping.Name, name, "G012", $"Joiner '{name}' has {incoming} upstream instances."));
            }

            if (transformation.Kind == TransformationKind.Union && incoming < 2)
            {
                diagnostics.Add(Warning(mapping.Name, name, "G013", $"Union '{name}' has {incoming} upstream instances."));
            }

            if (transformation.Kind == TransformationKind.SourceQualifier
                && upstream[name].Any(upstreamName => nodes[upstreamName].Kind != InstanceKind.Source))
            {
                diagnostics.Add(Warning(mapping.Name, name, "G014", $"Source qualifier '{name}' has an upstream instance that is not a source."));
            }

            if (transformation.Kind != TransformationKind.SourceQualifier && incoming == 0)
            {
                diagnostics.Add(Warning(mapping.Name, name, "G015", $"Transformation '{name}' has no upstream instance."));
            }

            if (outgoing == 0)
            {
                diagnostics.Add(Info(mapping.Name, name, "G016", $"Transformation '{name}' has no downstream instance."));
            }
        }
    }

    private static IReadOnlyDictionary<string, IReadOnlyList<string>> Freeze(Dictionary<string, List<string>> links)
    {
        return links.ToFrozenDictionary(
            pair => pair.Key,
            pair => (IReadOnlyList<string>)pair.Value.ToArray(),
            StringComparer.Ordinal);
    }

    private static Diagnostic Error(string mapping, string? instance, string code, string message)
    {
        return new Diagnostic(DiagnosticSeverity.Error, mapping, instance, message, code);
    }

    private static Diagnostic Warning(string mapping, string? instance, string code, string message)
    {
        return new Diagnostic(DiagnosticSeverity.Warning, mapping, instance, message, code);
    }

    private static Diagnostic Info(string mapping, string? instance, string code, string message)
    {
        return new Diagnostic(DiagnosticSeverity.Info, mapping, instance, message, code);
    }

    private sealed class PortSet
    {
        private readonly IReadOnlyDictionary<string, PortFact> _ports;

        public PortSet(IReadOnlyDictionary<string, PortFact> ports)
        {
            _ports = ports;
        }

        public bool IsOutput(string name, out string reason)
        {
            return Allows(name, output: true, out reason);
        }

        public bool IsInput(string name, out string reason)
        {
            return Allows(name, output: false, out reason);
        }

        private bool Allows(string name, bool output, out string reason)
        {
            if (!_ports.TryGetValue(name, out PortFact? fact) || fact is null)
            {
                reason = "missing";
                return false;
            }

            if (output ? fact.Output : fact.Input)
            {
                reason = string.Empty;
                return true;
            }

            reason = fact.Variable ? "a local variable" : output ? "input-only" : "output-only";
            return false;
        }
    }

    private sealed record PortFact(string Name, bool Output, bool Input, bool Variable);
}
