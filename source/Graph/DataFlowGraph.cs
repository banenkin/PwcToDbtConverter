using PwcToDbtConverter.Model;

namespace PwcToDbtConverter.Graph;

public sealed class DataFlowGraph
{
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _upstream;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _downstream;
    private readonly IReadOnlyDictionary<PortRef, ConnectorDef> _inputs;
    private readonly IReadOnlyDictionary<string, int>? _topologicalIndex;

    internal DataFlowGraph(
        string mappingName,
        IReadOnlyDictionary<string, InstanceDef> nodes,
        IReadOnlyList<ConnectorDef> edges,
        IReadOnlyDictionary<string, IReadOnlyList<string>> upstream,
        IReadOnlyDictionary<string, IReadOnlyList<string>> downstream,
        IReadOnlyDictionary<PortRef, ConnectorDef> inputs,
        IReadOnlyList<string>? topologicalOrder)
    {
        MappingName = mappingName;
        Nodes = nodes;
        Edges = edges;
        TopologicalOrder = topologicalOrder;
        _upstream = upstream;
        _downstream = downstream;
        _inputs = inputs;
        _topologicalIndex = topologicalOrder is null
            ? null
            : topologicalOrder.Select((name, index) => (name, index)).ToDictionary(pair => pair.name, pair => pair.index, StringComparer.Ordinal);
    }

    public string MappingName { get; }

    public IReadOnlyDictionary<string, InstanceDef> Nodes { get; }

    public IReadOnlyList<ConnectorDef> Edges { get; }

    public IReadOnlyList<string>? TopologicalOrder { get; }

    public bool IsAcyclic => TopologicalOrder is not null;

    public IReadOnlyList<string> Upstream(string instance)
    {
        return _upstream.TryGetValue(instance, out IReadOnlyList<string>? names) ? names : [];
    }

    public IReadOnlyList<string> Downstream(string instance)
    {
        return _downstream.TryGetValue(instance, out IReadOnlyList<string>? names) ? names : [];
    }

    public ConnectorDef? InputOf(PortRef input)
    {
        return _inputs.TryGetValue(input, out ConnectorDef? connector) ? connector : null;
    }

    public IReadOnlyList<string> UpstreamOf(string instance)
    {
        if (!_upstream.ContainsKey(instance))
        {
            return [];
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        var stack = new Stack<string>(_upstream[instance]);
        while (stack.TryPop(out string? current))
        {
            if (!seen.Add(current))
            {
                continue;
            }

            if (_upstream.TryGetValue(current, out IReadOnlyList<string>? next))
            {
                foreach (string name in next)
                {
                    stack.Push(name);
                }
            }
        }

        seen.Remove(instance);
        if (!IsAcyclic || _topologicalIndex is null)
        {
            return seen.OrderBy(name => name, StringComparer.Ordinal).ToArray();
        }

        return seen.OrderBy(name => _topologicalIndex.TryGetValue(name, out int index) ? index : int.MaxValue).ToArray();
    }
}
