using PwcToDbtConverter.Model;

namespace PwcToDbtConverter.Sql;

/// <summary>
/// One dbt model. <see cref="IsBlocked"/> is set when a graph error makes the SQL unsafe to run.
/// </summary>
public sealed record SqlModel(string Sql, IReadOnlyList<Diagnostic> Diagnostics, bool IsBlocked);
