using PwcToDbtConverter.Model;

namespace PwcToDbtConverter.Dbt;

/// <summary>
/// Writes a dbt project for one PowerCenter export. File names and YAML entries are sorted.
/// </summary>
public interface IDbtProjectWriter
{
    IReadOnlyList<string> Write(string outputDirectory, PowerMartDocument document, IReadOnlyList<DbtModel> models);
}
