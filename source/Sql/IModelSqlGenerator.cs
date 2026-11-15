using PwcToDbtConverter.Graph;
using PwcToDbtConverter.Model;

namespace PwcToDbtConverter.Sql;

/// <summary>
/// Builds one DuckDB dbt model for a target instance.
/// </summary>
public interface IModelSqlGenerator
{
    SqlModel Generate(PowerMartDocument document, MappingDef mapping, GraphBuildResult built, string targetInstance);
}
