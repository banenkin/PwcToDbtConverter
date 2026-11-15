using PwcToDbtConverter.Model;
using PwcToDbtConverter.Parsing;

namespace PwcToDbtConverterTests.Parsing;

public class PowerMartXmlParserTests
{
    [Fact]
    public void Parse_SampleExport_MatchesInventoryCounts()
    {
        PowerMartDocument document = new PowerMartXmlParser().Parse(SamplePaths.ExportFile);

        Assert.Equal("FLOWLINE_DEMO_JAFFLESHOP", document.FolderName);
        Assert.Equal(3, document.Mappings.Count);
        Assert.Equal(16, document.Targets.Count);
        Assert.Equal(12, document.Sources.Count);
        Assert.Equal(98, document.Mappings.Sum(mapping => mapping.Transformations.Count));
        Assert.Equal(142, document.Mappings.Sum(mapping => mapping.Instances.Count));
        Assert.Equal(1066, document.Mappings.Sum(mapping => mapping.Connectors.Count));
        Assert.Equal(1183, document.Mappings.Sum(mapping => mapping.Transformations.Sum(transformation => transformation.Ports.Count)));
        Assert.Equal(16, document.Mappings.Sum(mapping => mapping.Transformations.Sum(transformation => transformation.Groups.Count)));

        Assert.Equal(
            new[] { "m_FL_JS_STAGING_FULL", "m_FL_JS_MARTS_ENRICHED", "m_FL_JS_MARTS_CORE" },
            document.Mappings.Select(mapping => mapping.Name).ToArray());
        Assert.Equal(165, ConnectorCount(document, "m_FL_JS_STAGING_FULL"));
        Assert.Equal(624, ConnectorCount(document, "m_FL_JS_MARTS_ENRICHED"));
        Assert.Equal(277, ConnectorCount(document, "m_FL_JS_MARTS_CORE"));

        Assert.Equal(28, KindCount(document, TransformationKind.SourceQualifier));
        Assert.Equal(25, KindCount(document, TransformationKind.Expression));
        Assert.Equal(16, KindCount(document, TransformationKind.Joiner));
        Assert.Equal(15, KindCount(document, TransformationKind.Sorter));
        Assert.Equal(9, KindCount(document, TransformationKind.Aggregator));
        Assert.Equal(2, KindCount(document, TransformationKind.Router));
        Assert.Equal(1, KindCount(document, TransformationKind.Filter));
        Assert.Equal(2, KindCount(document, TransformationKind.Union));
        Assert.Equal(0, KindCount(document, TransformationKind.Unsupported));

        Assert.Equal(6, document.Sources.Count(source => string.Equals(source.OwnerName, "RAW", StringComparison.Ordinal)));
        Assert.Equal(6, document.Sources.Count(source => string.Equals(source.OwnerName, "STG", StringComparison.Ordinal)));
        AssertNoDiagnostics(document);
    }

    [Fact]
    public void Parse_SampleExport_MatchesPortTypeCounts()
    {
        PowerMartDocument document = new PowerMartXmlParser().Parse(SamplePaths.ExportFile);

        Assert.Equal(212, PortCount(document, PortType.Input));
        Assert.Equal(582, PortCount(document, PortType.Input | PortType.Output));
        Assert.Equal(102, PortCount(document, PortType.Input | PortType.Output | PortType.Master));
        Assert.Equal(250, PortCount(document, PortType.Output));
        Assert.Equal(31, PortCount(document, PortType.Variable));
        Assert.Equal(6, PortCount(document, PortType.Input | PortType.Master));
    }

    [Fact]
    public void Parse_SampleExport_KeepsAliasesAndRunDate()
    {
        PowerMartDocument document = new PowerMartXmlParser().Parse(SamplePaths.ExportFile);
        MappingDef staging = document.Mappings.Single(mapping => mapping.Name == "m_FL_JS_STAGING_FULL");

        InstanceDef aliasedProducts = staging.Instances.Single(instance => instance.Name == "RAW_PRODUCTS1");
        Assert.Equal("RAW_PRODUCTS", aliasedProducts.TransformationName);
        Assert.Equal(InstanceKind.Source, aliasedProducts.Kind);

        InstanceDef qualifier = staging.Instances.Single(instance => instance.Name == "SQ_RAW_PRODUCTS1");
        Assert.Equal("RAW_PRODUCTS1", Assert.Single(qualifier.AssociatedSourceInstances));

        InstanceDef customers = staging.Instances.Single(instance => instance.Name == "STG_CUSTOMERS");
        Assert.Equal("TRUNCATE TABLE STG.STG_CUSTOMERS", customers.Attributes["Pre SQL"]);

        MappingDef enriched = document.Mappings.Single(mapping => mapping.Name == "m_FL_JS_MARTS_ENRICHED");
        MappingVariableDef variable = Assert.Single(enriched.Variables);
        Assert.Equal("$$RUN_DATE", variable.Name);
        Assert.Equal("12/31/2025 00:00:00", variable.DefaultValue);
        Assert.True(variable.IsParameter);
        Assert.Empty(document.Mappings.Single(mapping => mapping.Name == "m_FL_JS_MARTS_CORE").Variables);
        Assert.Empty(staging.Variables);
    }

    [Fact]
    public void Parse_EmptyAttributeValue_BecomesNull()
    {
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <POWERMART>
              <REPOSITORY NAME="REPO">
                <FOLDER NAME="FOLDER">
                  <MAPPING NAME="m_test" ISVALID="YES">
                    <TRANSFORMATION NAME="EXP_TEST" TYPE="Expression">
                      <TABLEATTRIBUTE NAME="Tracing Level" VALUE=""/>
                    </TRANSFORMATION>
                  </MAPPING>
                </FOLDER>
              </REPOSITORY>
            </POWERMART>
            """;

        PowerMartDocument document = ParseInline(xml);
        TransformationDef transformation = Assert.Single(Assert.Single(document.Mappings).Transformations);

        Assert.True(transformation.Attributes.ContainsKey("Tracing Level"));
        Assert.Null(transformation.Attributes["Tracing Level"]);
        AssertNoDiagnostics(document);
    }

    [Fact]
    public void Parse_UnknownTransformationType_RecordsDiagnosticAndContinues()
    {
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <POWERMART>
              <REPOSITORY NAME="REPO">
                <FOLDER NAME="FOLDER">
                  <MAPPING NAME="m_test" ISVALID="YES">
                    <TRANSFORMATION NAME="LKP_TEST" TYPE="Lookup Procedure"/>
                  </MAPPING>
                </FOLDER>
              </REPOSITORY>
            </POWERMART>
            """;

        PowerMartDocument document = ParseInline(xml);
        TransformationDef transformation = Assert.Single(Assert.Single(document.Mappings).Transformations);

        Assert.Equal(TransformationKind.Unsupported, transformation.Kind);
        Diagnostic diagnostic = Assert.Single(document.Diagnostics);
        Assert.Equal(DiagnosticSeverity.Warning, diagnostic.Severity);
        Assert.Equal("m_test", diagnostic.Mapping);
        Assert.Equal("LKP_TEST", diagnostic.Instance);
        Assert.Contains("Lookup Procedure", diagnostic.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_UnionTemplate_IsClassifiedAsUnion()
    {
        const string xml = """
            <?xml version="1.0" encoding="utf-8"?>
            <POWERMART>
              <REPOSITORY NAME="REPO">
                <FOLDER NAME="FOLDER">
                  <MAPPING NAME="m_test" ISVALID="YES">
                    <TRANSFORMATION NAME="UN_REGIONS" TYPE="Custom Transformation" TEMPLATENAME="Union Transformation"/>
                  </MAPPING>
                </FOLDER>
              </REPOSITORY>
            </POWERMART>
            """;

        PowerMartDocument document = ParseInline(xml);
        TransformationDef transformation = Assert.Single(Assert.Single(document.Mappings).Transformations);

        Assert.Equal(TransformationKind.Union, transformation.Kind);
        Assert.Equal("Union Transformation", transformation.TemplateName);
        AssertNoDiagnostics(document);
    }

    [Fact]
    public void Parse_MissingFile_ThrowsWithPath()
    {
        PowerMartParseException exception = Assert.Throws<PowerMartParseException>(
            () => new PowerMartXmlParser().Parse("missing-powermart.xml"));

        Assert.Equal("missing-powermart.xml", exception.Path);
        Assert.Contains("missing-powermart.xml", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_NotXml_ThrowsWithSourceName()
    {
        PowerMartParseException exception = Assert.Throws<PowerMartParseException>(
            () => ParseInline("this is not xml"));

        Assert.Equal("inline.xml", exception.Path);
        Assert.Contains("inline.xml", exception.Message, StringComparison.Ordinal);
    }

    [Fact]
    public void Parse_NoPowerMartRoot_ThrowsWithSourceName()
    {
        PowerMartParseException exception = Assert.Throws<PowerMartParseException>(
            () => ParseInline("<ROOT />"));

        Assert.Equal("inline.xml", exception.Path);
        Assert.Contains("POWERMART", exception.Message, StringComparison.Ordinal);
    }

    private static PowerMartDocument ParseInline(string xml)
    {
        using var reader = new StringReader(xml);
        return new PowerMartXmlParser().Parse(reader, "inline.xml");
    }

    private static int ConnectorCount(PowerMartDocument document, string mappingName)
    {
        return document.Mappings.Single(mapping => mapping.Name == mappingName).Connectors.Count;
    }

    private static int KindCount(PowerMartDocument document, TransformationKind kind)
    {
        return document.Mappings.Sum(mapping => mapping.Transformations.Count(transformation => transformation.Kind == kind));
    }

    private static int PortCount(PowerMartDocument document, PortType type)
    {
        return document.Mappings.Sum(mapping => mapping.Transformations.Sum(
            transformation => transformation.Ports.Count(port => port.Type == type)));
    }

    private static void AssertNoDiagnostics(PowerMartDocument document)
    {
        string text = string.Join(
            "\n",
            document.Diagnostics.Select(diagnostic =>
                $"{diagnostic.Severity} [{diagnostic.Mapping}] {diagnostic.Instance}: {diagnostic.Message}"));
        Assert.True(document.Diagnostics.Count == 0, text);
    }
}
