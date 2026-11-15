namespace PwcToDbtConverter.Translation;

/// <summary>
/// Tells the translator where an expression sits. Aggregates are valid only when <see cref="AllowAggregates"/> is set.
/// </summary>
public sealed record ExpressionContext(
    bool AllowAggregates,
    IReadOnlyDictionary<string, string?> MappingVariables,
    string Mapping = "",
    string? Instance = null);
