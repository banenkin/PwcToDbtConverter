namespace PwcToDbtConverter.Dbt;

/// <summary>
/// One generated model, ready to be written. <see cref="TargetName"/> is the target definition, which can differ from the instance name.
/// </summary>
public sealed record DbtModel(
    string MappingName,
    string InstanceName,
    string TargetName,
    string Sql,
    bool IsBlocked);
