# Architecture: PowerCenter XML to dbt converter

Status: steps 1 through 5 are implemented. A source written by another mapping is `{{ ref() }}` and is omitted from `sources.yml`. `dbt build` against jaffle shop is still a manual check. The C# in section 3 matches the records in `source/Model`. Expression golden strings live in `docs/expression-rules.md`. The graph contract lives in `docs/graph-spec.md`.

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

Cross-mapping dependency: the `STG_*` tables are targets of `m_FL_JS_STAGING_FULL` and sources of the two mart mappings. Those reads are `{{ ref('stg_...') }}`, so dbt builds staging before the marts. `RAW_*` stays an external source.

Output count: 16 target instances, so 16 dbt models if one model is produced per target instance.

---

## 2. XML element to domain model

| XML | Domain model | Notes |
|---|---|---|
| `SOURCE` + `SOURCEFIELD` | `SourceDef` + `FieldDef` | `OWNERNAME` gives the schema, `DBDNAME` the connection. `KEYTYPE` (PRIMARY / FOREIGN KEY) and `NULLABLE` feed dbt tests. `REFERENCEDDBD`, `REFERENCEDTABLE` and `REFERENCEDFIELD` stay on the field so a foreign key can become a relationship test. |
| `TARGET` + `TARGETFIELD` | `TargetDef` + `FieldDef` | Load settings (`Target load type`, `Truncate`, `Update as Update`) live in `ATTRIBUTE`, not here. |
| `MAPPING` | `MappingDef` | `ISVALID="NO"` becomes a diagnostic. |
| `INSTANCE` | `InstanceDef` | Name is the node key. `TRANSFORMATION_NAME` may differ (`ORDER_FINANCIALS1` is an instance of `ORDER_FINANCIALS`). `ASSOCIATED_SOURCE_INSTANCE` and instance `TABLEATTRIBUTE` values (for example `Pre SQL`) stay on the instance. |
| `TRANSFORMATION` | `TransformationDef` | `TYPE` plus `TEMPLATENAME` decide the kind. |
| `TRANSFORMFIELD` | `PortDef` | Port type is parsed into flags. `EXPRESSION` may be absent. |
| `GROUP` | `GroupDef` | Router groups carry `EXPRESSION`, `OUTPUT/DEFAULT` is the fallback. |
| `TABLEATTRIBUTE` | `Attributes` dictionary | Empty `VALUE=""` becomes `null`. Known keys get typed accessors. |
| `CONNECTOR` | `ConnectorDef` | `FROMINSTANCE.FROMFIELD -> TOINSTANCE.TOFIELD`. |
| `MAPPINGVARIABLE` | `MappingVariableDef` | Stored on the mapping that declares it. `$$RUN_DATE` is declared on `m_FL_JS_MARTS_ENRICHED`, which is also where the expressions use it. |
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
    Router, Lookup, Union, Unsupported
}

public enum InstanceKind { Source, Target, Transformation }

public enum KeyType { None, Primary, Foreign }

public sealed record FieldDef(
    string Name,
    string DataType,
    int? Precision,
    int? Scale,
    bool IsNullable,
    KeyType KeyType,
    string? ReferencedDbd,
    string? ReferencedTable,
    string? ReferencedField);

public sealed record SourceDef(
    string Name,
    string? OwnerName,
    string? DbdName,
    string? DatabaseType,
    IReadOnlyList<FieldDef> Fields);

public sealed record TargetDef(
    string Name,
    string? DatabaseType,
    IReadOnlyList<FieldDef> Fields);

public sealed record PortDef(
    string Name,
    PortType Type,
    int Position,                 // 0-based document order: local variables depend on it
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

public sealed record InstanceDef(
    string Name,
    string TransformationName,
    InstanceKind Kind,
    IReadOnlyList<string> AssociatedSourceInstances,
    IReadOnlyDictionary<string, string?> Attributes);

public sealed record ConnectorDef(string FromInstance, string FromField, string ToInstance, string ToField);

public sealed record MappingVariableDef(string Name, string? DataType, string? DefaultValue, bool IsParameter);

public sealed record Diagnostic(DiagnosticSeverity Severity, string Mapping, string? Instance, string Message);

public enum DiagnosticSeverity { Info, Warning, Error }

public sealed record MappingDef(
    string Name,
    bool IsValid,
    IReadOnlyList<InstanceDef> Instances,
    IReadOnlyList<TransformationDef> Transformations,
    IReadOnlyList<ConnectorDef> Connectors,
    IReadOnlyList<MappingVariableDef> Variables);

public sealed record PowerMartDocument(
    string FolderName,
    IReadOnlyList<SourceDef> Sources,
    IReadOnlyList<TargetDef> Targets,
    IReadOnlyList<MappingDef> Mappings,
    IReadOnlyList<Diagnostic> Diagnostics);
```

Lists keep document order. Attribute dictionaries use ordinal comparison. An empty attribute is stored as `null`, the same as a missing one. The parser hands collections out as arrays and frozen dictionaries.

The graph is the class in `docs/graph-spec.md`: nodes keyed by instance name, edges are the valid connectors, `TopologicalOrder` is null on a cycle, and `UpstreamOf` is the ancestor set in that order. Keys are always instance names, never transformation names. The same table can appear several times in one mapping.

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
  Sql/                       SqlGenerator.cs, SqlModel.cs
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
```

`XmlAttributeReader.Read` returns null for a missing attribute and for an empty or whitespace value. The parser does not use `.Value` or a null-forgiving operator on attributes.

Encoding: the declaration says Windows-1252. .NET does not register that code page by default, so the parser registers `System.Text.Encoding.CodePages` before reading. The inventory test loads the sample, so that code page is exercised.

### 4.2 Transformation to SQL rules

| Kind | SQL shape | Notes |
|---|---|---|
| Source Qualifier | `select ... from {{ source(owner, table) }}` or `{{ ref(model) }}` | `owner` is `OWNERNAME`, then `DBDNAME`. `table` is the source definition name. When another mapping writes that table, the read is `ref`. A non-empty `Sql Query`, `Source Filter` or `User Defined Join` is a `-- TODO [UNSUPPORTED]` comment and the plain select is still emitted. |
| Expression | `select <expr> as <port>` | Ports with no expression pass through. |
| Local variable | Nested `select *, <expr> as <name>` | Variables are evaluated in `Position` order. A `v_PREV_` variable, or a variable whose expression reads its own name, is previous-row state: the expression is kept, with a `-- TODO [UNSUPPORTED]` comment, because a window function would invent an `ORDER BY`. |
| Filter | `where <condition>` | |
| Joiner | `join` on `Join Condition` | `Normal Join` is inner. `Master Outer Join` is `master left join detail`. `Detail Outer Join` is `detail left join master`. `Full Outer Join` is a full outer join. |
| Sorter | Pass-through | No `ORDER BY`. A comment says the sorter does not change the row set. |
| Aggregator | `group by` for `GroupBy` ports, aggregate functions for the rest | Aggregates are allowed only in this shape. |
| Lookup | `left join {{ source(owner, table) }}` or `{{ ref(model) }}` | Connected outputs come from the upstream input. Unconnected outputs come from the lookup table. A lookup table written by another mapping is a `ref`. |
| Router, Union, anything else | Pass-through select plus `-- TODO [UNSUPPORTED]` | Output ports with an incoming connector keep that column. Other outputs are `null`. A warning is recorded. The logic is not dropped. |

### 4.3 Expression translation

| Informatica | DuckDB (target dialect) | Note |
|---|---|---|
| `IIF(c, a, b)` | `case when c then a else b end` | Nested IIF is common. |
| `ISNULL(x)` | `x is null` | |
| `LTRIM`, `RTRIM` | `ltrim`, `rtrim` | Nesting stays. `LTRIM(RTRIM(x))` does not become `trim(x)`. |
| `UPPER`, `TRUNC(date)` | `upper`, `cast(x as date)` | `TRUNC` in this export has no format argument. |
| `DATE_DIFF(a, b, 'DD')` | `date_diff('day', b, a)` | Informatica returns `a - b`. DuckDB takes the start first, so the argument order flips. |
| `$$RUN_DATE` | `{{ var('run_date', '2025-12-31') }}` | Default comes from `MAPPINGVARIABLE` `12/31/2025 00:00:00`. |
| `COUNT(*)`, `SUM`, `MAX`, `MIN` | same, lowercased | Valid only inside an Aggregator. Elsewhere the call is kept and a warning is recorded. |

Golden strings and the rest of the function table are in `docs/expression-rules.md`. Those strings are the translation before any cast to the port type.

---

## 5. Edge cases

| Case | Where in the sample | Handling |
|---|---|---|
| Declared encoding Windows-1252 | Line 1 | Register the code-page provider before reading. The sample load covers this. |
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
| Unknown transformation type | Any new type | Emit a warning and a `-- TODO [UNSUPPORTED]` comment in SQL. Never drop the logic silently. |
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
7. **Mapping variables stay on the mapping.** `$$RUN_DATE` is declared on `m_FL_JS_MARTS_ENRICHED`, and that mapping's expressions are the ones that use it. A document-level dictionary would hide the owner.
8. **Instance links are parsed, not reconstructed.** `InstanceDef` keeps `ASSOCIATED_SOURCE_INSTANCE` names and instance table attributes. That is how `RAW_PRODUCTS` and `RAW_PRODUCTS1` stay attached to the right source qualifier, and how `Pre SQL` truncate statements remain available to flag. Generation still does not infer incremental models from them (decision 2).
9. **Foreign-key columns keep their reference.** `FieldDef` stores `REFERENCEDDBD`, `REFERENCEDTABLE` and `REFERENCEDFIELD` so relationship tests (decision 6) can be generated from the export.
10. **Core SQL first.** Within the timebox, Source Qualifier, Expression, Filter, Joiner and Aggregator are translated before Router and Union. Sorter stays a no-op (decision 3). Anything not translated is still parsed, then emitted as a diagnostic and a `-- UNSUPPORTED` comment (decision 4). This export contains no Lookup.
11. **Expression golden strings come from `docs/expression-rules.md`.** The translator prints those strings and does not yet cast the result to the port type. Integer division is unverified, so that cast waits. Identifiers are copied as written, including `TYPE`, because the checked strings leave it unquoted. An unmapped function or format token stays in the SQL, with a warning and a `/* UNSUPPORTED: ... */` comment. `SUM`, `MAX`, `MIN` and `COUNT` outside an Aggregator are kept and produce a warning.
12. **Joiner outer join preserves the named side.** `Master Outer Join` keeps every master row (`master left join detail`). `Detail Outer Join` keeps every detail row. Informatica's joiner guide defines it this way. External source instances are read with `{{ source(owner, table) }}`, using `OWNERNAME` and the source definition name. A model-level gap is a `-- TODO [UNSUPPORTED]` comment.
13. **Previous-row variables are marked, not rewritten.** A variable named `v_PREV_*`, or one whose expression reads its own name, assumes the previous row. `SqlGenerator` keeps the translated expression, adds `-- TODO [UNSUPPORTED]`, and records a warning. Guessing `row_number()` would invent a sort key the expression does not state. Sorter stays a pass-through with no `ORDER BY` (decision 3).
14. **The dbt project is laid out from the mapping name.** A mapping whose name contains `STAGING` is written under `models/staging`. One that contains `CORE` goes under `models/marts/core`. One that contains `MART` goes under `models/marts`. Anything else stays in `models/`. The model file name is the target instance name, lowercased, so `ORDER_FINANCIALS1` does not collide with a definition named `ORDER_FINANCIALS`. `schema.yml` adds `not_null` and `unique` only for `KEYTYPE="PRIMARY KEY"`. Relationship tests from foreign keys are not written yet.
15. **A source that another mapping writes is a `ref`.** The graph stays inside one mapping. When a source definition name equals a target instance's transformation name in a different mapping, the SQL reads `{{ ref('model_name') }}` and that table is left out of `sources.yml`. `RAW_*` stays `{{ source('RAW', ...) }}` because nothing in the export writes those tables. If several models write the same target, the one whose instance name matches the definition is used.

Jaffle shop has no direct counterpart for the 10 mart tables. The best comparison points are its `customers` and `orders` models. The other outputs can only be checked by reading the expressions.

---

## 7. Build order

1. **Parse and inventory.** Tests assert the counts in section 1: 3 mappings, 16 targets, 12 sources, 98 transformations, 142 instances, 1066 connectors.
2. **Graph.** Topological order, upstream closure, dangling-edge and cycle diagnostics.
3. **Expression translator.** Golden-string tests, one per row in section 4.3.
4. **SQL generator.** One test per transformation kind, then one full target per mapping.
5. **dbt project writer.** Run the output with `dbt build` and compare the customers and orders models with jaffle shop's.

Where the converter is most likely wrong: join direction in the enriched mapping, row-order logic in the customer sequence, and the `DATE_DIFF` argument order.
