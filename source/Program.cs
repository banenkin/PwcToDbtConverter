using PwcToDbtConverter.Dbt;
using PwcToDbtConverter.Graph;
using PwcToDbtConverter.Model;
using PwcToDbtConverter.Parsing;
using PwcToDbtConverter.Sql;
using PwcToDbtConverter.Translation;

if (args.Length != 2)
{
    Console.Error.WriteLine("Usage: PwcToDbtConverter <input.xml> <output-dir>");
    return 1;
}

try
{
    var converter = new DbtProjectConverter(
        new PowerMartXmlParser(),
        new DataFlowGraphBuilder(),
        new SqlGenerator(new ExpressionTranslator()),
        new DbtProjectWriter());
    ConversionReport report = converter.Convert(args[0], args[1]);
    Console.Write(report.Format());
    return report.Diagnostics.Any(diagnostic => diagnostic.Severity == DiagnosticSeverity.Error) ? 1 : 0;
}
catch (PowerMartParseException exception)
{
    Console.Error.WriteLine(exception.Message);
    return 1;
}
