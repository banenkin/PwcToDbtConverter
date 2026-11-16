using PwcToDbtConverter.Graph;
using PwcToDbtConverter.Model;
using PwcToDbtConverter.Parsing;
using PwcToDbtConverter.Sql;
using PwcToDbtConverter.Translation;

namespace PwcToDbtConverterTests.Sql;

public class SqlGeneratorTests
{
    private const string CustomersSql = """
        with raw_customers as (
            select
                ID,
                NAME
            from {{ source('RAW', 'RAW_CUSTOMERS') }}
        ),

        sq_raw_customers as (
            select
                ID,
                NAME
            from raw_customers
        )

        select
            ID as CUSTOMER_ID,
            NAME as CUSTOMER_NAME
        from sq_raw_customers

        """;
    [Fact]
    public void Generate_SourceQualifier_ReadsSourceAndRenamesTargetColumns()
    {
        SqlModel model = Generate(Customers());

        Assert.False(model.IsBlocked);
        Assert.DoesNotContain(model.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Warning);
        Assert.Equal(CustomersSql, model.Sql);
    }

    [Fact]
    public void Generate_Expression_TranslatesIif()
    {
        PortDef amount = In("AMOUNT", 0);
        PortDef total = Out("TOTAL", 1, "IIF(ISNULL(AMOUNT), 0, AMOUNT)");
        SqlModel model = Generate(Pipeline(
            "RAW_ORDERS",
            ["AMOUNT"],
            "EXP_TOTAL",
            TransformationKind.Expression,
            [amount, total],
            Empty(),
            [("AMOUNT", "AMOUNT"), ("TOTAL", "TOTAL")],
            ["TOTAL"]));

        Assert.False(model.IsBlocked);
        Assert.DoesNotContain(model.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Warning);
        Assert.Contains("case when AMOUNT is null then 0 else AMOUNT end as TOTAL", model.Sql, StringComparison.Ordinal);
        Assert.Contains("from {{ source('RAW', 'RAW_ORDERS') }}", model.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_Filter_BecomesWhere()
    {
        SqlModel model = Generate(Pipeline(
            "RAW_STORES",
            ["LOCATION_ID"],
            "FIL_LOCS",
            TransformationKind.Filter,
            [Io("LOCATION_ID", 0)],
            Attrs(("Filter Condition", "NOT ISNULL(LOCATION_ID)")),
            [("LOCATION_ID", "LOCATION_ID"), ("LOCATION_ID", "LOCATION_ID")],
            ["LOCATION_ID"]));

        Assert.False(model.IsBlocked);
        Assert.DoesNotContain(model.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Warning);
        Assert.Contains("where LOCATION_ID is not null", model.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("TODO [UNSUPPORTED]", model.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_Joiner_NormalJoinIsInner()
    {
        SqlModel model = Generate(Joiner("Normal Join"));

        Assert.False(model.IsBlocked);
        Assert.DoesNotContain(model.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Warning);
        Assert.Contains(") master\n    inner join (", model.Sql, StringComparison.Ordinal);
        Assert.Contains("on SKU1 = SKU", model.Sql, StringComparison.Ordinal);
        Assert.Contains("SKU as SKU1", model.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_Joiner_MasterOuterKeepsDetailRows()
    {
        SqlModel model = Generate(Joiner("Master Outer Join"));

        Assert.False(model.IsBlocked);
        Assert.Contains(") detail\n    left join (", model.Sql, StringComparison.Ordinal);
        Assert.Contains(") master\n", model.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain(") master\n    left join (", model.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_Joiner_DetailOuterKeepsMasterRows()
    {
        SqlModel model = Generate(Joiner("Detail Outer Join"));

        Assert.False(model.IsBlocked);
        Assert.Contains(") master\n    left join (", model.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain(") detail\n    left join (", model.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_Aggregator_GroupsAndAggregates()
    {
        PortDef id = new(
            "ID",
            PortType.Input | PortType.Output,
            0,
            "string",
            10,
            0,
            "ID",
            ExpressionKind.GroupBy,
            null,
            null,
            false,
            null);
        PortDef amount = In("AMOUNT", 1);
        PortDef total = Out("TOTAL", 2, "SUM(AMOUNT)");
        SqlModel model = Generate(Pipeline(
            "RAW_ORDERS",
            ["ID", "AMOUNT"],
            "AGG_ORDERS",
            TransformationKind.Aggregator,
            [id, amount, total],
            Empty(),
            [("ID", "ID"), ("AMOUNT", "AMOUNT"), ("ID", "ID"), ("TOTAL", "TOTAL")],
            ["ID", "TOTAL"]));

        Assert.False(model.IsBlocked);
        Assert.DoesNotContain(model.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Warning);
        Assert.Contains("sum(AMOUNT) as TOTAL", model.Sql, StringComparison.Ordinal);
        Assert.Contains("group by ID", model.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_Lookup_LeftJoinsSource()
    {
        PortDef orderId = Io("ORDER_ID", 0);
        PortDef name = Out("CUSTOMER_NAME", 1);
        var attributes = Attrs(
            ("Lookup table name", "RAW_CUSTOMERS"),
            ("Lookup source name", "RAW"),
            ("Lookup condition", "ORDER_ID = ID"));
        SqlModel model = Generate(Pipeline(
            "RAW_ORDERS",
            ["ORDER_ID"],
            "LKP_CUSTOMER",
            TransformationKind.Lookup,
            [orderId, name],
            attributes,
            [("ORDER_ID", "ORDER_ID"), ("ORDER_ID", "ORDER_ID"), ("CUSTOMER_NAME", "CUSTOMER_NAME")],
            ["ORDER_ID", "CUSTOMER_NAME"]));

        Assert.False(model.IsBlocked);
        Assert.DoesNotContain(model.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Warning);
        Assert.Contains("left join {{ source('RAW', 'RAW_CUSTOMERS') }} lookup", model.Sql, StringComparison.Ordinal);
        Assert.Contains("on ORDER_ID = ID", model.Sql, StringComparison.Ordinal);
        Assert.Contains("lookup.CUSTOMER_NAME as CUSTOMER_NAME", model.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_Sorter_IsPassThroughWithoutOrderBy()
    {
        SqlModel model = Generate(Pipeline(
            "RAW_ORDERS",
            ["ORDER_ID"],
            "SRT_ORDERS",
            TransformationKind.Sorter,
            [Io("ORDER_ID", 0)],
            Empty(),
            [("ORDER_ID", "ORDER_ID"), ("ORDER_ID", "ORDER_ID")],
            ["ORDER_ID"]));

        Assert.False(model.IsBlocked);
        Assert.DoesNotContain(model.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Warning);
        Assert.Contains("-- Sorter 'SRT_ORDERS' does not change the row set.", model.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("order by", model.Sql, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("TODO [UNSUPPORTED]", model.Sql, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_RowOrderVariable_KeepsExpressionAndWarns()
    {
        PortDef customer = Io("CUSTOMER_ID", 0, expression: "CUSTOMER_ID");
        PortDef previous = Var("v_PREV_CUSTOMER_ID", 1, "CUSTOMER_ID");
        PortDef sequence = Var("v_ORDER_SEQUENCE", 2, "IIF(v_PREV_CUSTOMER_ID = CUSTOMER_ID, v_ORDER_SEQUENCE + 1, 1)");
        PortDef number = Out("CUSTOMER_ORDER_NUMBER", 3, "v_ORDER_SEQUENCE");
        SqlModel model = Generate(Pipeline(
            "RAW_ORDERS",
            ["CUSTOMER_ID"],
            "EXP_CUSTOMER_ORDER_NUM",
            TransformationKind.Expression,
            [customer, previous, sequence, number],
            Empty(),
            [("CUSTOMER_ID", "CUSTOMER_ID"), ("CUSTOMER_ORDER_NUMBER", "CUSTOMER_ORDER_NUMBER")],
            ["CUSTOMER_ORDER_NUMBER"]));

        Assert.False(model.IsBlocked);
        Assert.Contains("-- TODO [UNSUPPORTED]:", model.Sql, StringComparison.Ordinal);
        Assert.Contains("v_ORDER_SEQUENCE + 1", model.Sql, StringComparison.Ordinal);
        Assert.Contains(model.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Warning);
    }

    [Fact]
    public void Generate_Router_EmitsUnsupportedCommentAndWarning()
    {
        SqlModel model = Generate(Pipeline(
            "RAW_STORES",
            ["REGION"],
            "RTR_REGION",
            TransformationKind.Router,
            [Io("REGION", 0)],
            Empty(),
            [("REGION", "REGION"), ("REGION", "REGION")],
            ["REGION"]));

        Assert.False(model.IsBlocked);
        Assert.Contains("-- TODO [UNSUPPORTED]: Router 'RTR_REGION' is not translated.", model.Sql, StringComparison.Ordinal);
        Assert.Contains("REGION", model.Sql, StringComparison.Ordinal);
        Diagnostic warning = Assert.Single(model.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Warning);
        Assert.Contains("RTR_REGION", warning.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_StagingCustomers_MatchesSourceRename()
    {
        SqlModel model = GenerateSample("m_FL_JS_STAGING_FULL", "STG_CUSTOMERS");

        Assert.False(model.IsBlocked);
        Assert.Equal(2, Ancestors("m_FL_JS_STAGING_FULL", "STG_CUSTOMERS"));
        Assert.Equal(CustomersSql, model.Sql);
    }

    [Fact]
    public void Generate_CoreProducts_ReadsStagingSource()
    {
        SqlModel model = GenerateSample("m_FL_JS_MARTS_CORE", "PRODUCTS");

        Assert.False(model.IsBlocked);
        Assert.Equal(2, Ancestors("m_FL_JS_MARTS_CORE", "PRODUCTS"));
        Assert.Contains("{{ ref('stg_products') }}", model.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("source('STG'", model.Sql, StringComparison.Ordinal);
        Assert.Contains("sq_stg_products", model.Sql, StringComparison.Ordinal);
        Assert.DoesNotContain("TODO [UNSUPPORTED]", model.Sql, StringComparison.Ordinal);
        Assert.Contains(
            """
            select
                PRODUCT_ID,
                PRODUCT_NAME,
                PRODUCT_TYPE,
                PRODUCT_PRICE_CENTS,
                PRODUCT_PRICE,
                PRODUCT_DESCRIPTION,
                IS_FOOD_ITEM,
                IS_DRINK_ITEM
            from sq_stg_products
            """,
            model.Sql,
            StringComparison.Ordinal);
    }

    [Fact]
    public void Generate_EnrichedCustomer360_ProducesModel()
    {
        PowerMartDocument document = Sample();
        MappingDef mapping = document.Mappings.Single(candidate => candidate.Name == "m_FL_JS_MARTS_ENRICHED");
        GraphBuildResult built = new DataFlowGraphBuilder().Build(mapping, document);
        Assert.DoesNotContain(built.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);

        SqlModel model = new SqlGenerator(new ExpressionTranslator()).Generate(document, mapping, built, "CUSTOMER_360");
        TargetDef target = document.Targets.Single(candidate => candidate.Name == "CUSTOMER_360");

        Assert.False(model.IsBlocked);
        Assert.StartsWith("with ", model.Sql, StringComparison.Ordinal);
        Assert.Contains("{{ ref('stg_customers') }}", model.Sql, StringComparison.Ordinal);
        foreach (FieldDef field in target.Fields)
        {
            Assert.Contains(field.Name, model.Sql, StringComparison.Ordinal);
        }
    }

    private static int Ancestors(string mappingName, string target)
    {
        PowerMartDocument document = Sample();
        MappingDef mapping = document.Mappings.Single(candidate => candidate.Name == mappingName);
        return new DataFlowGraphBuilder().Build(mapping, document).Graph.UpstreamOf(target).Count;
    }

    private static SqlModel GenerateSample(string mappingName, string target)
    {
        PowerMartDocument document = Sample();
        MappingDef mapping = document.Mappings.Single(candidate => candidate.Name == mappingName);
        GraphBuildResult built = new DataFlowGraphBuilder().Build(mapping, document);
        Assert.DoesNotContain(built.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        return new SqlGenerator(new ExpressionTranslator()).Generate(document, mapping, built, target);
    }

    private static PowerMartDocument Sample()
    {
        return new PowerMartXmlParser().Parse(Parsing.SamplePaths.ExportFile);
    }

    private static PowerMartDocument Customers()
    {
        return Pipeline(
            "RAW_CUSTOMERS",
            ["ID", "NAME"],
            "SQ_RAW_CUSTOMERS",
            TransformationKind.SourceQualifier,
            [Io("ID", 0), Io("NAME", 1)],
            Empty(),
            [("ID", "ID"), ("NAME", "NAME"), ("ID", "CUSTOMER_ID"), ("NAME", "CUSTOMER_NAME")],
            ["CUSTOMER_ID", "CUSTOMER_NAME"]);
    }

    private static PowerMartDocument Joiner(string joinType)
    {
        SourceDef products = Source("RAW_PRODUCTS", "SKU");
        SourceDef supplies = Source("RAW_SUPPLIES", "SKU", "COST");
        TargetDef target = Target("TGT", "LEFT_SKU", "RIGHT_SKU");
        var ports = new PortDef[]
        {
            Io("SKU1", 0, PortType.Master),
            Io("SKU", 1),
            Io("COST", 2),
        };
        TransformationDef joiner = Step("JNR_SUPPLIES", TransformationKind.Joiner, ports, Attrs(
            ("Join Condition", "SKU1 = SKU"),
            ("Join Type", joinType)));
        var instances = new[]
        {
            Instance("RAW_PRODUCTS", InstanceKind.Source),
            Instance("RAW_SUPPLIES", InstanceKind.Source),
            Instance("JNR_SUPPLIES", InstanceKind.Transformation),
            Instance("TGT", InstanceKind.Target),
        };
        var connectors = new[]
        {
            new ConnectorDef("RAW_PRODUCTS", "SKU", "JNR_SUPPLIES", "SKU1"),
            new ConnectorDef("RAW_SUPPLIES", "SKU", "JNR_SUPPLIES", "SKU"),
            new ConnectorDef("RAW_SUPPLIES", "COST", "JNR_SUPPLIES", "COST"),
            new ConnectorDef("JNR_SUPPLIES", "SKU1", "TGT", "LEFT_SKU"),
            new ConnectorDef("JNR_SUPPLIES", "SKU", "TGT", "RIGHT_SKU"),
        };
        var mapping = new MappingDef("m_test", true, instances, [joiner], connectors, []);
        return new PowerMartDocument("TEST", [products, supplies], [target], [mapping], []);
    }

    private static PowerMartDocument Pipeline(
        string sourceName,
        string[] sourceFields,
        string stepName,
        TransformationKind kind,
        PortDef[] ports,
        IReadOnlyDictionary<string, string?> attributes,
        (string From, string To)[] links,
        string[] targetFields)
    {
        var connectors = new List<ConnectorDef>();
        for (int index = 0; index < links.Length; index++)
        {
            bool intoStep = index < ports.Count(port => port.Type.HasFlag(PortType.Input));
            if (intoStep)
            {
                connectors.Add(new ConnectorDef(sourceName, links[index].From, stepName, links[index].To));
            }
            else
            {
                connectors.Add(new ConnectorDef(stepName, links[index].From, "TGT", links[index].To));
            }
        }

        var mapping = new MappingDef(
            "m_test",
            true,
            [
                Instance(sourceName, InstanceKind.Source),
                Instance(stepName, InstanceKind.Transformation),
                Instance("TGT", InstanceKind.Target),
            ],
            [Step(stepName, kind, ports, attributes)],
            connectors,
            []);
        return new PowerMartDocument(
            "TEST",
            [Source(sourceName, sourceFields)],
            [Target("TGT", targetFields)],
            [mapping],
            []);
    }

    private static SqlModel Generate(PowerMartDocument document)
    {
        MappingDef mapping = Assert.Single(document.Mappings);
        GraphBuildResult built = new DataFlowGraphBuilder().Build(mapping, document);
        Assert.DoesNotContain(built.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
        return new SqlGenerator(new ExpressionTranslator()).Generate(document, mapping, built, "TGT");
    }

    private static SourceDef Source(string name, params string[] fields)
    {
        return new SourceDef(name, "RAW", "RAW", "DuckDB", fields.Select(Field).ToArray());
    }

    private static TargetDef Target(string name, params string[] fields)
    {
        return new TargetDef(name, "DuckDB", fields.Select(Field).ToArray());
    }

    private static FieldDef Field(string name)
    {
        return new FieldDef(name, "string", 10, 0, true, KeyType.None, null, null, null);
    }

    private static InstanceDef Instance(string name, InstanceKind kind)
    {
        return new InstanceDef(name, name, kind, [], Empty());
    }

    private static TransformationDef Step(
        string name,
        TransformationKind kind,
        PortDef[] ports,
        IReadOnlyDictionary<string, string?> attributes)
    {
        return new TransformationDef(name, kind, null, ports, [], attributes);
    }

    private static PortDef In(string name, int position)
    {
        return new PortDef(name, PortType.Input, position, "string", 10, 0, null, ExpressionKind.None, null, null, false, null);
    }

    private static PortDef Out(string name, int position, string? expression = null)
    {
        return new PortDef(
            name,
            PortType.Output,
            position,
            "string",
            10,
            0,
            expression,
            expression is null ? ExpressionKind.None : ExpressionKind.General,
            null,
            null,
            false,
            null);
    }

    private static PortDef Io(string name, int position, PortType extra = PortType.None, string? expression = null)
    {
        return new PortDef(
            name,
            PortType.Input | PortType.Output | extra,
            position,
            "string",
            10,
            0,
            expression,
            expression is null ? ExpressionKind.None : ExpressionKind.General,
            null,
            null,
            false,
            null);
    }

    private static PortDef Var(string name, int position, string expression)
    {
        return new PortDef(name, PortType.Variable, position, "string", 10, 0, expression, ExpressionKind.General, null, null, false, null);
    }

    private static IReadOnlyDictionary<string, string?> Empty()
    {
        return new Dictionary<string, string?>(StringComparer.Ordinal);
    }

    private static IReadOnlyDictionary<string, string?> Attrs(params (string Name, string Value)[] pairs)
    {
        return pairs.ToDictionary(pair => pair.Name, pair => (string?)pair.Value, StringComparer.Ordinal);
    }
}
