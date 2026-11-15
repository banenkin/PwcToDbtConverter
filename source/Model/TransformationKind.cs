namespace PwcToDbtConverter.Model;

public enum TransformationKind
{
    SourceQualifier,
    Expression,
    Filter,
    Joiner,
    Sorter,
    Aggregator,
    Lookup,
    Router,
    Union,
    Unsupported,
}
