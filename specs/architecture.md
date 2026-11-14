# Architecture: PowerCenter XML to dbt converter

Status: design only. No C# is written yet. The C# shown here is the proposed shape, not existing code.

Input: `sample-data/FLOWLINE_DEMO_JAFFLESHOP.xml` (728 KB, declares `encoding="Windows-1252"`, has a `<!DOCTYPE POWERMART SYSTEM "powrmart.dtd">`).
Reference target: `dbt-labs/jaffle_shop_duckdb`, used to sanity-check output. Nothing in the converter may hard-code jaffle shop SQL.

---

## 1. What is inside the sample XML

### 1.1 Inventory

| Element | Count | Meaning |
|---|---|---|
| `POWERMART` / `REPOSITORY` / `FOLDER` | 1 / 1 / 1 | Folder `FLOWLINE_DEMO_JAFFLESHOP`, repository `REPO_Jaffleshop` |
| `SOURCE` | 12 | 6 `RAW_*` tables (owner `RAW`), 6 `STG_*` tables (owner `STG`) |
| `TARGET` | 16 | 6 `STG_*` loads, 10 mart tables |
| `MAPPING` | 3 | `m_FL_JS_STAGING_FULL`, `m_FL_JS_MARTS_ENRICHED`, `m_FL_JS_MARTS_CORE` |
| `TRANSFORMATION` | 98 | Source Qualifier 28, Expression 25, Joiner 16, Sorter 15, Aggregator 9, Router 2, Filter 1, Custom (Union template) 2 |
| `INSTANCE` | 142 | Placements of sources, targets and transformations inside mappings |
| `CONNECTOR` | 1066 | Edges: 165 (staging), 624 (enriched), 277 (core) |
| `TRANSFORMFIELD` | 1183 | Ports. `PORTTYPE`: INPUT 212, INPUT/OUTPUT 582, INPUT/OUTPUT/MASTER 102, OUTPUT 250, LOCAL VARIABLE 31, INPUT/MASTER 6 |
| `GROUP` | 16 | Router and Union groups |
| `TABLEATTRIBUTE` | 766 | Per-transformation settings: join condition, filter condition, SQL override, sort options |
| `SESSION` / `WORKFLOW` | 3 / 1 | One session per mapping, one workflow `wf_FL_JAFFLESHOP_DEMO` |
| `MAPPINGVARIABLE` | 1 | `$$RUN_DATE`, default `12/31/2025 00:00:00` |

### 1.2 Mappings

| Mapping | Reads | Writes | Logic |
|---|---|---|---|
| `m_FL_JS_STAGING_FULL` | `RAW_CUSTOMERS`, `RAW_ITEMS`, `RAW_ORDERS`, `RAW_PRODUCTS` (read twice, as `RAW_PRODUCTS1`), `RAW_STORES`, `RAW_SUPPLIES` | `STG_CUSTOMERS`, `STG_LOCATIONS`, `STG_ORDERS`, `STG_ORDER_ITEMS`, `STG_PRODUCTS`, `STG_SUPPLIES` | Null-default expressions (`IIF(ISNULL(x), 0, x)`), router on `REGION` into APAC/AMER/EMEA, rename expressions, union back, joins between supplies and products, sorters |
| `m_FL_JS_MARTS_ENRICHED` | `STG_*` (several aliased instances) | `CUSTOMER_360`, `ORDER_FINANCIALS`, `ORDER_ITEM_ENRICHED`, `PRODUCT_DAILY_PERFORMANCE`, `STORE_DAILY_KPIS` | Multi-way joins, router on `ORDER_TOTAL >= 50` (HIGH_VALUE / STANDARD) then union, aggregates by location-day and product-day, RFM recency/frequency/monetary scoring, `FIL_LOCS` filter `NOT ISNULL(LOCATION_ID)` |
| `m_FL_JS_MARTS_CORE` | `STG_*` (several aliased instances) | `CUSTOMERS`, `ORDERS`, `ORDER_ITEMS`, `PRODUCTS`, `SUPPLIES` | Joins, aggregates by order and by customer, customer order sequencing (`EXP_CUSTOMER_ORDER_NUM`), days since last order based on `$$RUN_DATE` |

Cross-mapping dependency: the `STG_*` tables are targets of `m_FL_JS_STAGING_FULL` and sources of the two mart mappings. In dbt this becomes `ref()`/`source()` chaining, so staging models must build before marts.

Output count: 16 target instances, so 16 dbt models if one model is produced per target instance.

---

## 2. XML element to domain model

| XML | Domain model | Notes |
|---|---|---|
| `SOURCE` + `SOURCEFIELD` | `SourceDef` + `FieldDef` | `OWNERNAME` gives the schema, `DBDNAME` the connection. `KEYTYPE` (PRIMARY / FOREIGN KEY) and `NULLABLE` feed dbt tests. |
| `TARGET` + `TARGETFIELD` | `TargetDef` + `FieldDef` | Load settings (`Target load type`, `Truncate`, `Update as Update`) live in `ATTRIBUTE`, not here. |
| `MAPPING` | `MappingDef` | `ISVALID="NO"` becomes a diagnostic. |
| `INSTANCE` | `InstanceDef` | Name is the node key. `TRANSFORMATION_NAME` may differ (`ORDER_FINANCIALS1` is an instance of `ORDER_FINANCIALS`). |
| `TRANSFORMATION` | `TransformationDef` | `TYPE` plus `TEMPLATENAME` decide the kind. |
| `TRANSFORMFIELD` | `PortDef` | Port type is parsed into flags. `EXPRESSION` may be absent. |
| `GROUP` | `GroupDef` | Router groups carry `EXPRESSION`, `OUTPUT/DEFAULT` is the fallback. |
| `TABLEATTRIBUTE` | `Attributes` dictionary | Empty `VALUE=""` becomes `null`. Known keys get typed accessors. |
| `CONNECTOR` | `ConnectorDef` | `FROMINSTANCE.FROMFIELD -> TOINSTANCE.TOFIELD`. |
| `MAPPINGVARIABLE` | `MappingVariableDef` | Used to fill `$$RUN_DATE` defaults. |
| `SESSION`, `WORKFLOW`, `WORKFLOWLINK` | Not modelled in v1 | Only needed later, to order mappings. |

---

## 3. Proposed C# models

Namespace root: `PwcToDbtConverter`. Models are immutable `record`s with no XML types inside them.

```csharp
namespace PwcToDbtConverter.Model;

[Flags]
public enum PortType { None = 0, Input = 1, Output = 2, Master = 4, Variable = 8 }

public enum ExpressionKind { None, General, GroupBy }

public enum TransformationKind
{
    SourceQualifier, Expression, Filter, Joiner, Sorter, Aggregator,
    Router, Union, Unsupported
}

public enum InstanceKind { Source, Target, Transformation }

public sealed record PortDef(
    string Name,
    PortType Type,
    int Position,                 // document order: local variables depend on it
    string DataType,
    int? Precision,
    int? Scale,
    string? Expression,
    ExpressionKind ExpressionKind,
    string? Group,
    string? RefField,             // e.g. LOCATION_ID1 -> LOCATION_ID
    bool IsSortKey,
    string? SortDirection);

public sealed record GroupDef(string Name, string Type, int Order, string? Expression);

public sealed record TransformationDef(
    string Name,
    TransformationKind Kind,
    string? TemplateName,
    IReadOnlyList<PortDef> Ports,
    IReadOnlyList<GroupDef> Groups,
    IReadOnlyDictionary<string, string?> Attributes);

public sealed record InstanceDef(string Name, string TransformationName, InstanceKind Kind);

public sealed record ConnectorDef(string FromInstance, string FromField, string ToInstance, string ToField);

public sealed record Diagnostic(DiagnosticSeverity Severity, string Mapping, string? Instance, string Message);

public enum DiagnosticSeverity { Info, Warning, Error }

public sealed record MappingDef(
    string Name,
    bool IsValid,
    IReadOnlyList<InstanceDef> Instances,
    IReadOnlyList<TransformationDef> Transformations,
    IReadOnlyList<ConnectorDef> Connectors);

public sealed record PowerMartDocument(
    string FolderName,
    IReadOnlyList<SourceDef> Sources,
    IReadOnlyList<TargetDef> Targets,
    IReadOnlyList<MappingDef> Mappings,
    IReadOnlyDictionary<string, string?> MappingVariables,   // "$$RUN_DATE" -> default
    IReadOnlyList<Diagnostic> Diagnostics);
```

`SourceDef` and `TargetDef` each hold their `FieldDef` list (name, data type, precision, scale, nullable, key type).

Graph model:

```csharp
namespace PwcToDbtConverter.Graph;

public sealed record DataFlowGraph(
    IReadOnlyDictionary<string, InstanceDef> Nodes,          // key = instance name
    IReadOnlyList<ConnectorDef> Edges)
{
    public IReadOnlyList<string> TopologicalOrder() => /* Kahn's algorithm, ties broken by name */;
    public IReadOnlyList<string> UpstreamOf(string instanceName) => /* transitive closure */;
}
```

Keys are always instance names, never transformation names. The same table can appear several times in one mapping.

---

## 4. Services

Each service is an interface with one implementation. Dependencies are passed through constructors. No static state.

| Service | Input | Output | Responsibility |
|---|---|---|---|
| `IPowerMartParser` | XML path | `PowerMartDocument` | LINQ to XML. Reads and types all elements. Collects diagnostics, never throws for unknown content. |
| `IDataFlowGraphBuilder` | `MappingDef` | `DataFlowGraph` | Builds nodes and edges. Reports dangling connectors, cycles and unconnected targets. |
| `IExpressionTranslator` | Informatica expression text + port scope | SQL fragment or diagnostic | Tokenizer and recursive-descent parser. No regex replacement, since nested `IIF` breaks it. |
| `IModelSqlGenerator` | `DataFlowGraph` + target name | dbt model SQL | One CTE per transformation, in topological order. |
| `IDbtProjectWriter` | list of models, source defs | files on disk | Writes `models/`, `sources.yml`, `schema.yml`. Deterministic output: sorted names, stable whitespace. |

Proposed project layout under `source/`:

```
source/
  Program.cs                 CLI entry point (replaces the Hello World stub)
  Parsing/                   PowerMartXmlParser.cs, XmlAttributeReader.cs
  Model/                     records and enums above
  Graph/                     DataFlowGraph.cs, DataFlowGraphBuilder.cs
  Translation/               Tokenizer, Parser, ExpressionTranslator.cs, DuckDb dialect
  Sql/                       ModelSqlGenerator.cs, one class per transformation kind
  Dbt/                       DbtProjectWriter.cs
tests/PwcToDbtConverterTests/
  Parsing/, Graph/, Translation/, Sql/   (xUnit, Fixtures point at sample-data)
```

### 4.1 Safe parsing with LINQ to XML

```csharp
var settings = new XmlReaderSettings
{
    DtdProcessing = DtdProcessing.Ignore,   // Prohibit would throw on the DOCTYPE
    XmlResolver = null,                     // never fetch powrmart.dtd
};
using var reader = XmlReader.Create(path, settings);
XDocument doc = XDocument.Load(reader);

// Attribute helper: missing and empty both become null
static string? Attr(XElement e, string name) =>
    string.IsNullOrWhiteSpace((string?)e.Attribute(name)) ? null : (string)e.Attribute(name)!;
```

Encoding: the declaration says Windows-1252. .NET does not register that code page by default, so `System.Text.Encoding.CodePages` must be registered before reading. This still needs to be verified in Phase 1.

### 4.2 Transformation to SQL rules

| Kind | SQL shape | Notes |
|---|---|---|
| Source Qualifier | `select ... from {{ source('RAW', 'CUSTOMERS') }}` | If `Sql Query` or `Source Filter` is non-empty, flag it as unsupported and fall back to the plain select. |
| Expression | `select <passthrough>, <expr> as <port>` | Pass-through is the default for ports with no expression. |
| Local variable | Staged CTE column | Variables are evaluated in `Position` order and may reference each other (`v_R_SCORE`). |
| Filter | `where <condition>` | |
| Joiner | `join` on `Join Condition` | `Normal Join` is inner. `Master Outer Join` keeps all detail rows, so the detail side is the preserved side. Check against the Informatica Joiner docs before coding. |
| Sorter | Pass-through | SQL has no ordering semantics here. Logic that depends on row order is turned into window functions. |
| Aggregator | `group by` for `GroupBy` ports, aggregate functions for the rest | |
| Router | One CTE per group, `where <group expression>`. Default group = `not (g1 or g2 or ...)` | A row can match several groups in Informatica, so each group is its own CTE. |
| Union | `union all` of the input groups | Input groups have rename expressions first. Columns match by name. |

### 4.3 Expression translation

| Informatica | DuckDB (target dialect) | Note |
|---|---|---|
| `IIF(c, a, b)` | `case when c then a else b end` | Nested IIF is common. |
| `ISNULL(x)` | `x is null` | |
| `LTRIM(RTRIM(x))` | `trim(x)` | |
| `UPPER`, `TRUNC(date)` | `upper`, `cast(x as date)` | |
| `DATE_DIFF(a, b, 'DD')` | `date_diff('day', b, a)` | Informatica returns `a - b`. DuckDB takes the start first, so the argument order flips. Classic bug source. |
| `$$RUN_DATE` | `{{ var('run_date', '2025-12-31') }}` | Default comes from `MAPPINGVARIABLE`. |
| `COUNT(*)`, `SUM(...)` in aggregators | same | Only valid under an Aggregator. |

---

## 5. Edge cases

| Case | Where in the sample | Handling |
|---|---|---|
| Declared encoding Windows-1252 | Line 1 | Register the code-page provider. Verify in Phase 1. |
| External DTD `powrmart.dtd` | Line 2 | `DtdProcessing.Ignore`, `XmlResolver = null`. Missing DTD must not fail the load. |
| Empty attribute values | 1,253 `VALUE=""` on table attributes | Treat as `null`, same as missing. |
| Missing `EXPRESSION` on a port | 799 ports | Pass-through. The port takes the value of the connected input of the same name. It is not an error. |
| Missing `GROUP` on a port | Most ports | Only Router and Union ports use it. Ignore when absent. |
| Instance alias (name differs from transformation) | `ORDER_FINANCIALS1`, `STG_CUSTOMERS1`, `RAW_PRODUCTS1` | Key the graph by instance name. Never by transformation name. |
| Same table read more than once in one mapping | `RAW_PRODUCTS` and `RAW_PRODUCTS1`; `STG_*` repeated in mart mappings | Each read is a separate CTE with a distinct alias. |
| Renamed ports on connectors | `CUSTOMER_ID` connected from `ID` | Use `FROMFIELD` and `TOFIELD` as written. Do not assume names match. |
| Duplicate-name port suffix `1` | `CUSTOMER_ID1`, `SKU1`, `LOCATION_ID1` (REF_FIELD set) | Use `REF_FIELD` to recover the original name. Never strip digits from names, because real names like `ITEM_LINES_1` may exist. |
| Joiner master/detail | `MASTER` in `PORTTYPE` on 108 ports (`INPUT/OUTPUT/MASTER` 102, `INPUT/MASTER` 6) | Master flag decides the join side. Verify the outer-join direction. |
| Router with DEFAULT group | `OUTPUT/DEFAULT` in both routers | Default = rows matching no group. |
| Union as Custom Transformation | `TEMPLATENAME="Union Transformation"`, `TYPE="Custom Transformation"` | Detect by template name, not by `TYPE`. |
| Local variables with order dependency | `v_*` ports, 31 total | Preserve `Position`. Keep dependencies in order. |
| Row-order logic | `EXP_CUSTOMER_ORDER_NUM`, `EXP_MARK_FIRST_CUSTOMER`, `SRT_FOR_CUSTOMER_SEQ` | Likely depends on sorted input. Express as `row_number()` / `lag()`. Mark as "verify" until checked against the outputs. |
| Sorter flagged as sorted input | `Sorted Input` attribute on 25 transformations | Does not change set results in SQL. Ignored, but it is a documented assumption. |
| Expression with newlines and XML entities | `&#xD;&#xA;`, `&apos;`, `&gt;` | XML decodes the entities. Normalise whitespace before translation. |
| Mapping parameter in expression | `$$RUN_DATE` | Replaced with a dbt var. Missing parameter = diagnostic. |
| Non-empty SQL override or Source Filter | Currently all empty | If non-empty, flag as unsupported and do not silently ignore it. |
| Load settings | `Truncate target table option = NO`, `Target load type = Bulk` | Ignored for v1. Models are tables. Incremental is not inferred from the XML. |
| Unknown transformation type | Any new type | Emit an `Unsupported` diagnostic and a `-- UNSUPPORTED` comment in SQL. Never drop the logic silently. |
| Connector to an unknown port or instance | Dangling edges | Diagnostic, not an exception. |
| Type mapping | `varchar2`, `number`, `date/time`, `string` | Map to DuckDB types through one table. Keep precision and scale when present. |

---

## 6. Decisions I made, to confirm

1. **Target dialect: DuckDB.** The jaffle shop reference runs on DuckDB, so output can be checked by running it.
2. **Materialization: table for all models.** The XML has no CDC or watermark information.
3. **Sorter is a no-op.** Row-order logic is rewritten with window functions instead.
4. **Unsupported logic is flagged, never dropped.** Each unsupported item produces a diagnostic and an SQL comment.
5. **dbt sources come from `SOURCE` definitions.** The schema comes from `OWNERNAME`, the connection from `DBDNAME`. Nothing is hard-coded to jaffle shop.
6. **Tests from keys.** `PRIMARY KEY` gives `unique` and `not_null`. `FOREIGN KEY` gives `relationships`.

Jaffle shop has no direct counterpart for the 10 mart tables. The best comparison points are its `customers` and `orders` models. The other outputs can only be checked by reading the expressions.

---

## 7. Build order

1. **Parse and inventory.** Tests assert the counts in section 1: 3 mappings, 16 targets, 12 sources, 98 transformations, 142 instances, 1066 connectors.
2. **Graph.** Topological order, upstream closure, dangling-edge and cycle diagnostics.
3. **Expression translator.** Golden-string tests, one per row in section 4.3.
4. **SQL generator.** One test per transformation kind, then one full target per mapping.
5. **dbt project writer.** Run the output with `dbt build` and compare the customers and orders models with jaffle shop's.

Where the converter is most likely wrong: join direction in the enriched mapping, row-order logic in the customer sequence, and the `DATE_DIFF` argument order.
