# Graph builder spec (Step 2)

Status: design for implementation in Cursor. Builds on the Step 1 code in `source/Model` and `source/Parsing`, and on decisions 7–10 in `specs/architecture.md`.

What this step does: turn one `MappingDef` into a graph, then answer three questions for the SQL generator:

1. In what order can instances be evaluated? (topological order)
2. Which instances feed a given target, directly or indirectly? (upstream closure)
3. Is the export broken in a way that makes SQL wrong? (diagnostics: cycles, dangling connectors, and so on)

The builder never throws for bad content. It returns a graph and a list of diagnostics.

---

## 1. Scope

- One graph per mapping. Each `MappingDef` gives one `DataFlowGraph`.
- Nodes are `InstanceDef`, keyed by `InstanceDef.Name`. Never key by `TransformationName`: the same definition can appear under several instance names, for example `ORDER_FINANCIALS1`.
- Edges are `ConnectorDef`, which are port-level. One upstream instance usually has many connectors into one downstream instance, one per column.
- Cross-mapping links are not edges. `STG_*` are targets in `m_FL_JS_STAGING_FULL` and `SOURCE` instances in the mart mappings. Linking them across mappings is the dbt `ref()` layer's job, not this graph's.

---

## 2. Inputs and port resolution

`MappingDef` does not carry source or target field lists, and the builder needs them to check connector endpoints. So the signature is:

```csharp
public interface IDataFlowGraphBuilder
{
    GraphBuildResult Build(MappingDef mapping, PowerMartDocument document);
}
```

Port sets per instance:

| Instance kind | Ports that can be a connector **from** | Ports that can be a connector **to** |
|---|---|---|
| `Source` (`TransformationName` → `SourceDef`) | every `FieldDef.Name` | none |
| `Target` (`TransformationName` → `TargetDef`) | none | every `FieldDef.Name` |
| `Transformation` (→ `TransformationDef`) | `PortDef` with `PortType.Output` set | `PortDef` with `PortType.Input` set |

- `LOCAL VARIABLE` ports have only the `Variable` flag, so they are never connectable.
- `INPUT/OUTPUT/MASTER` ports have `Input`, `Output` and `Master`, so they can be both.
- Name matching is ordinal and case-sensitive. The XML uses uppercase names throughout.

---

## 3. Data structures

```csharp
namespace PwcToDbtConverter.Graph;

public sealed record PortRef(string Instance, string Port);

public sealed record GraphBuildResult(DataFlowGraph Graph, IReadOnlyList<Diagnostic> Diagnostics);

public sealed class DataFlowGraph
{
    // Built only by DataFlowGraphBuilder. Everything is immutable after construction.
    internal DataFlowGraph(
        string mappingName,
        IReadOnlyDictionary<string, InstanceDef> nodes,
        IReadOnlyList<ConnectorDef> edges,
        IReadOnlyDictionary<string, IReadOnlyList<string>> upstream,
        IReadOnlyDictionary<string, IReadOnlyList<string>> downstream,
        IReadOnlyDictionary<PortRef, ConnectorDef> inputs,
        IReadOnlyList<string>? topologicalOrder);

    public string MappingName { get; }

    // All instances in the mapping, keyed by instance name (ordinal).
    public IReadOnlyDictionary<string, InstanceDef> Nodes { get; }

    // Valid connectors only, in document order. Dropped connectors are reported, not stored.
    public IReadOnlyList<ConnectorDef> Edges { get; }

    // Null when the graph has a cycle.
    public IReadOnlyList<string>? TopologicalOrder { get; }

    public bool IsAcyclic => TopologicalOrder is not null;

    // Distinct upstream instance names, sorted ordinal.
    public IReadOnlyList<string> Upstream(string instance);

    public IReadOnlyList<string> Downstream(string instance);

    // The single connector feeding an input port, or null if the port is unconnected.
    public ConnectorDef? InputOf(PortRef input);

    // Transitive upstream instances, excluding the instance itself.
    // Ordered by TopologicalOrder when acyclic, otherwise sorted ordinal.
    public IReadOnlyList<string> UpstreamOf(string instance);

    // Position of each instance in TopologicalOrder. Built once from it; null when cyclic.
    private readonly IReadOnlyDictionary<string, int>? TopologicalIndex;
}
```

Notes:

- `PortRef` is a record, so it works as a dictionary key with value equality. Use it for `inputs`.
- `DataFlowGraph` is a class with an internal constructor, not a record. Records would expose the dictionaries' contents in equality checks, which is not useful here.
- `Upstream` and `Downstream` are de-duplicated at instance level. Connector counts are not used for them.

---

## 4. Build algorithm

Run in this order. Diagnostics go into one list, in the order they are found.

**Step 1. Index instances.**
- Build an ordinal dictionary from `mapping.Instances`.
- A duplicate instance name is `G017` (Error). Keep the first, drop the rest.
- For each instance, resolve its port set (section 2). If `TransformationName` matches no source, target or transformation, report `G011` (Error). Connectors touching that instance then fail in step 2 and are dropped.

**Step 2. Validate each connector**, in document order. The first failing check decides the code, and a failing connector is dropped:

| Check | Failure code |
|---|---|
| `FromInstance` exists | `G002` |
| `ToInstance` exists | `G003` |
| `FromField` is a connectable output of `FromInstance` | `G004` |
| `ToField` is a connectable input of `ToInstance` | `G005` |
| Direction: nothing flows **out of** a target or **into** a source | `G006` |
| The input port is not already fed by an earlier connector | `G007` (keep the first, drop this one) |

**Step 3. Build indexes** from the valid edges:
- `upstream[i]` = distinct `FromInstance` values feeding `i`.
- `downstream[i]` = distinct `ToInstance` values fed by `i`.
- `inputs[PortRef(i, port)]` = the connector feeding that input port.

**Step 4. Topological order** (Kahn's algorithm):
- **In-degree counts distinct upstream instances, not connectors.** If it counted connectors, an instance with 15 connectors from one upstream would never become ready. This is the most likely bug in this step.
- Use a `SortedSet<string>` with ordinal comparison as the ready queue. The order is then deterministic, which keeps the generated files stable.
- If every node is emitted, `TopologicalOrder` is that list.
- If not, the graph has a cycle (`G001`). Nodes left with in-degree > 0 are either on a cycle or downstream of one. To report a path, start from any leftover node and walk upstream through leftover nodes until one repeats. Report that path in data-flow order, for example `A -> B -> C -> A`. Set `TopologicalOrder` to `null`.

**Step 5. Node-level checks** (none of these drop anything):

| Check | Code | Severity |
|---|---|---|
| Target has no incoming connector | `G008` | Warning |
| Source instance has no outgoing connector | `G009` | Info |
| Joiner has fewer than 2 upstream instances | `G012` | Warning |
| Union (custom template) has fewer than 2 upstream instances | `G013` | Warning |
| Source Qualifier has an upstream instance | `G014` | Warning |
| Non-source transformation has no upstream instance | `G015` | Warning |
| Transformation has no downstream instance (dead end) | `G016` | Info |

---

## 5. Upstream closure

`UpstreamOf(target)` is a backward walk over `upstream` lists. Use an explicit stack, not recursion, so a deep chain cannot overflow the call stack:

```csharp
public IReadOnlyList<string> UpstreamOf(string instance)
{
    var seen = new HashSet<string>(StringComparer.Ordinal);
    var stack = new Stack<string>(upstream[instance]);
    while (stack.TryPop(out string? current))
    {
        if (seen.Add(current))
        {
            foreach (string next in upstream[current])
            {
                stack.Push(next);
            }
        }
    }

    seen.Remove(instance);   // a self-loop would otherwise add the instance to its own closure
    return IsAcyclic
        ? seen.OrderBy(name => TopologicalIndex[name]).ToArray()
        : seen.OrderBy(name => name, StringComparer.Ordinal).ToArray();
}
```

- The `seen` set makes it terminate on a cyclic graph, so `UpstreamOf` is safe to call even when the graph is broken.
- The returned list is in evaluation order, so the SQL generator can emit CTEs straight from it.

---

## 6. Diagnostics

Each diagnostic has the mapping name in `Mapping`. For connector problems, `Instance` is the `ToInstance`, or the `FromInstance` when the `ToInstance` is the one that is unknown.

| Code | Severity | Meaning |
|---|---|---|
| `G001` | Error | Cycle. Message contains the path. |
| `G002` | Error | Connector's `FROMINSTANCE` is not in the mapping. |
| `G003` | Error | Connector's `TOINSTANCE` is not in the mapping. |
| `G004` | Error | `FROMFIELD` is not an output port of its instance. The message says whether the port is input-only, a local variable, or missing. |
| `G005` | Error | `TOFIELD` is not an input port of its instance. |
| `G006` | Error | Connector goes out of a target or into a source. |
| `G007` | Error | Input port already has a connector. The second is dropped. |
| `G008` | Warning | Target has no incoming connector. |
| `G009` | Info | Source has no outgoing connector. |
| `G011` | Error | Instance's `TRANSFORMATION_NAME` matches no source, target or transformation. |
| `G012` | Warning | Joiner with fewer than 2 upstream instances. |
| `G013` | Warning | Union with fewer than 2 upstream instances. |
| `G014` | Warning | Source Qualifier has an upstream instance. |
| `G015` | Warning | Transformation other than a Source Qualifier has no upstream instance. |
| `G016` | Info | Transformation has no downstream instance. |
| `G017` | Error | Duplicate instance name in the mapping. The first is kept. |
| `G018` | Error | Duplicate port name within one transformation. Port lookup would be ambiguous. |

**Effect on SQL generation** (this rule is the contract between this step and the generator):
- An **Error** in a target's upstream closure blocks that target. No model file is written, and the error is reported.
- Warnings and Infos never block. They are reported and generation continues.
- An unsupported transformation type is not an Error here. It is parsed, passed through, and emitted as `-- UNSUPPORTED` by the generator (decision 4).

---

## 7. Edge cases

| Case | Where it shows up | Expected behaviour |
|---|---|---|
| Many connectors between the same two instances | Every multi-column link, for example `JNR_ADD_LOCATION_INFO` → `FIL_LOCS` has 15 | Valid. Each connector is an edge. In-degree counts the distinct upstream once. |
| Multi-input instances | 18 instances in the sample are Joiners (`JNR_*`) or the Union template (`UN_REGIONS`, `UN_ORDER_BANDS`). Nothing else has more than one upstream. | Valid. No other kind may silently gain a second upstream. Those would trigger `G015` or be a bug upstream. |
| Source instances in mart mappings | `STG_*` as `SOURCE` in `m_FL_JS_MARTS_*` | Valid roots of the graph. No cross-mapping edges. |
| Instance alias | `ORDER_FINANCIALS1` (target, instance of `ORDER_FINANCIALS`) | Keyed by the instance name. Its `TransformationName` decides the field list. |
| Self-loop `X → X` | Not in the sample. Possible in a broken export. | Cycle, `G001`, path `X -> X`. `UpstreamOf` excludes `X` itself. |
| Cycle with a tail | A cycle plus instances downstream of it | Kahn leaves all of them unordered. The path reported is the cycle. `TopologicalOrder` is `null`. |
| Two sources of one input port | Not in the sample (verified) | `G007`. The first connector in document order is kept. |
| Connector missing attributes | Handled by the parser (it drops the connector and warns) | The graph never sees it. Do not re-check it here. |
| Port name repeated within one instance | Not in the sample (verified: no duplicate port names in any transformation) | `G018`, and the instance's ports are not indexed by name. |
| Router output ports across groups | Ports sit in one flat list, and `GROUP` says which group they belong to | Port identity is name only. Group handling belongs to the SQL generator, not the graph. |
| Empty mapping (no connectors) | Not in the sample | Valid. Every target gets `G008`. |
| Disconnected component | A target with no path to any source | Valid. `UpstreamOf` returns just what is reachable. |
| Very deep chain | Sample chains are shallow, but the rule should hold | Iterative walk, no recursion. |

Verified on the sample export: no dangling instance references, no unknown fields, no direction errors, no duplicate input ports, no duplicate port names, no cycles, and every target has one incoming connector.

---

## 8. Tests

**Unit tests** build `MappingDef` and `PowerMartDocument` records directly in code. They do not go through XML. One test per diagnostic code, plus:
- Fan-in: a joiner with two upstreams gets its in-degree right, and 15 connectors from one upstream still produce one in-degree.
- Self-loop and multi-node cycle report `G001` with the right path.
- A cycle with a downstream tail yields `TopologicalOrder == null`, and `UpstreamOf` still terminates.
- `UpstreamOf` on a diamond counts the shared ancestor once.

**Sample tests** on the three real mappings:
- Zero Error diagnostics in each mapping.
- `IsAcyclic` is true for all three.
- `TopologicalOrder` contains each node exactly once, and every upstream appears before its consumer.
- Golden upstream counts for the 16 targets, below.

Golden values were computed with a script from the XML, counting instance names transitively. Cursor should reproduce them. If an implementation disagrees, investigate the implementation first, and only update a golden value after confirming the script was right.

| Mapping | Target | Transitive upstream instances |
|---|---|---|
| staging | `STG_CUSTOMERS` | 2 |
| staging | `STG_LOCATIONS` | 13 |
| staging | `STG_ORDERS` | 4 |
| staging | `STG_ORDER_ITEMS` | 3 |
| staging | `STG_PRODUCTS` | 3 |
| staging | `STG_SUPPLIES` | 6 |
| enriched | `CUSTOMER_360` | 42 |
| enriched | `ORDER_FINANCIALS1` | 33 |
| enriched | `ORDER_ITEM_ENRICHED` | 21 |
| enriched | `PRODUCT_DAILY_PERFORMANCE` | 24 |
| enriched | `STORE_DAILY_KPIS` | 42 |
| core | `CUSTOMERS` | 7 |
| core | `ORDERS` | 16 |
| core | `ORDER_ITEMS` | 14 |
| core | `PRODUCTS` | 2 |
| core | `SUPPLIES` | 2 |

---

## 9. Decisions for you to confirm

1. **The builder takes `PowerMartDocument`.** It needs source and target field lists, and `MappingDef` does not carry them. Alternative: move those lists onto `MappingDef`. I recommend the parameter, because it keeps `MappingDef` a plain description of one mapping.
2. **Add a `Code` to `Diagnostic`.** Tests should match codes, not message text. The change is small: `Diagnostic` gets a `string Code`, and the existing parser messages get codes later. I recommend it.
3. **Duplicate input is "keep first, drop the rest" (`G007`).** The alternative is to fail the whole mapping. Keeping the first lets the rest of the mapping generate, and the error is still reported.
4. **Cycles block their targets, not the whole run.** The other targets in the same export still generate.
5. **Name matching is ordinal and case-sensitive.** This follows the XML. If Informatica ever disagrees with that, it will show up as `G004` or `G005` on a real export, which is the right place to find it.
