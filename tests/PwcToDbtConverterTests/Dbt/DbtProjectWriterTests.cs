using PwcToDbtConverter.Dbt;
using PwcToDbtConverter.Graph;
using PwcToDbtConverter.Model;
using PwcToDbtConverter.Parsing;
using PwcToDbtConverter.Sql;
using PwcToDbtConverter.Translation;

namespace PwcToDbtConverterTests.Dbt;

public class DbtProjectWriterTests
{
    [Fact]
    public void Write_PrimaryKey_EmitsUniqueAndNotNull()
    {
        string directory = NewDirectory();
        try
        {
            var document = new PowerMartDocument(
                "Demo Folder",
                [
                    new SourceDef(
                        "RAW_CUSTOMERS",
                        "RAW",
                        "XEPDB1",
                        "Oracle",
                        [Field("ID", KeyType.Primary)]),
                ],
                [
                    new TargetDef(
                        "STG_CUSTOMERS",
                        "Oracle",
                        [Field("CUSTOMER_ID", KeyType.Primary), Field("CUSTOMER_NAME", KeyType.None)]),
                ],
                [],
                []);
            var model = new DbtModel(
                "m_FL_JS_STAGING_FULL",
                "STG_CUSTOMERS",
                "STG_CUSTOMERS",
                "select\n    ID as CUSTOMER_ID\n",
                false);

            IReadOnlyList<string> written = new DbtProjectWriter().Write(directory, document, [model]);

            Assert.Contains(Path.Combine("models", "staging", "stg_customers.sql"), written);
            string project = Read(directory, "dbt_project.yml");
            string sources = Read(directory, Path.Combine("models", "sources.yml"));
            string schema = Read(directory, Path.Combine("models", "schema.yml"));
            string sql = Read(directory, Path.Combine("models", "staging", "stg_customers.sql"));

            AssertYaml(project);
            AssertYaml(sources);
            AssertYaml(schema);
            Assert.Contains("+materialized: table", project, StringComparison.Ordinal);
            Assert.Contains("name: demo_folder", project, StringComparison.Ordinal);
            Assert.Contains("name: \"RAW\"", sources, StringComparison.Ordinal);
            Assert.Contains("name: \"RAW_CUSTOMERS\"", sources, StringComparison.Ordinal);
            Assert.Contains("name: \"stg_customers\"", schema, StringComparison.Ordinal);
            Assert.Contains("name: \"CUSTOMER_ID\"", schema, StringComparison.Ordinal);
            Assert.Contains("- not_null", schema, StringComparison.Ordinal);
            Assert.Contains("- unique", schema, StringComparison.Ordinal);
            Assert.Contains("ID as CUSTOMER_ID", sql, StringComparison.Ordinal);
            Assert.DoesNotContain("CUSTOMER_NAME\"\n        tests:", schema, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    [Fact]
    public void Convert_SampleExport_WritesOneModelPerTarget()
    {
        string directory = NewDirectory();
        try
        {
            ConversionReport report = Converter().Convert(Parsing.SamplePaths.ExportFile, directory);

            Assert.Equal(3, report.MappingCount);
            Assert.Equal(16, report.TargetCount);
            Assert.Equal(16, report.ModelCount);
            Assert.DoesNotContain(report.Diagnostics, diagnostic => diagnostic.Severity == DiagnosticSeverity.Error);
            Assert.Contains("Mappings: 3\nTargets: 16\nModels: 16\n", report.Format(), StringComparison.Ordinal);

            Assert.True(File.Exists(Path.Combine(directory, "dbt_project.yml")));
            string sources = Read(directory, Path.Combine("models", "sources.yml"));
            string schema = Read(directory, Path.Combine("models", "schema.yml"));
            AssertYaml(Read(directory, "dbt_project.yml"));
            AssertYaml(sources);
            AssertYaml(schema);
            Assert.Contains("name: \"RAW\"", sources, StringComparison.Ordinal);
            Assert.Contains("name: \"RAW_CUSTOMERS\"", sources, StringComparison.Ordinal);
            Assert.DoesNotContain("name: \"STG\"", sources, StringComparison.Ordinal);
            Assert.DoesNotContain("name: \"STG_PRODUCTS\"", sources, StringComparison.Ordinal);

            Assert.Equal(16, Directory.GetFiles(directory, "*.sql", SearchOption.AllDirectories).Length);
            AssertFile(directory, Path.Combine("models", "staging", "stg_customers.sql"), "source('RAW', 'RAW_CUSTOMERS')");
            AssertFile(directory, Path.Combine("models", "marts", "core", "products.sql"), "ref('stg_products')");
            AssertFile(directory, Path.Combine("models", "marts", "core", "customers.sql"), "ref('stg_customers')");
            Assert.True(File.Exists(Path.Combine(directory, "models", "marts", "customer_360.sql")));
            Assert.True(File.Exists(Path.Combine(directory, "models", "marts", "order_financials1.sql")));
            Assert.Contains("name: \"stg_customers\"", schema, StringComparison.Ordinal);
            Assert.Contains("name: \"order_financials1\"", schema, StringComparison.Ordinal);
            Assert.Contains("name: \"customers\"", schema, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(directory, recursive: true);
        }
    }

    private static DbtProjectConverter Converter()
    {
        return new DbtProjectConverter(
            new PowerMartXmlParser(),
            new DataFlowGraphBuilder(),
            new SqlGenerator(new ExpressionTranslator()),
            new DbtProjectWriter());
    }

    private static void AssertFile(string directory, string relative, string fragment)
    {
        string text = Read(directory, relative);
        Assert.EndsWith("\n", text);
        Assert.Contains(fragment, text, StringComparison.Ordinal);
    }

    private static string Read(string directory, string relative)
    {
        return File.ReadAllText(Path.Combine(directory, relative));
    }

    private static string NewDirectory()
    {
        string directory = Path.Combine(Path.GetTempPath(), "pwc-dbt-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        return directory;
    }

    private static FieldDef Field(string name, KeyType key)
    {
        return new FieldDef(name, "varchar2", 100, 0, key != KeyType.Primary, key, null, null, null);
    }

    private static void AssertYaml(string text)
    {
        Assert.EndsWith("\n", text);
        Assert.DoesNotContain("\t", text);
        Assert.DoesNotContain("\r", text);
        foreach (string line in text.Split('\n'))
        {
            if (line.Length == 0)
            {
                continue;
            }

            Assert.False(char.IsWhiteSpace(line[^1]));
            int indent = 0;
            while (indent < line.Length && line[indent] == ' ')
            {
                indent++;
            }

            Assert.Equal(0, indent % 2);
            Assert.Equal(0, CountQuotes(line) % 2);
        }
    }

    private static int CountQuotes(string line)
    {
        int count = 0;
        bool escaped = false;
        foreach (char value in line)
        {
            if (escaped)
            {
                escaped = false;
                continue;
            }

            if (value == '\\')
            {
                escaped = true;
                continue;
            }

            if (value == '"')
            {
                count++;
            }
        }

        return count;
    }
}
