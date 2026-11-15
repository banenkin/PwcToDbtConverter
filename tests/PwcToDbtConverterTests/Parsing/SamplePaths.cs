namespace PwcToDbtConverterTests.Parsing;

internal static class SamplePaths
{
    public static string ExportFile { get; } = Resolve();

    private static string Resolve()
    {
        DirectoryInfo? directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null)
        {
            string candidate = Path.Combine(directory.FullName, "sample-data", "FLOWLINE_DEMO_JAFFLESHOP.xml");
            if (File.Exists(candidate))
            {
                return candidate;
            }

            directory = directory.Parent;
        }

        throw new InvalidOperationException(
            "Could not find sample-data/FLOWLINE_DEMO_JAFFLESHOP.xml by walking up from the test output directory.");
    }
}
