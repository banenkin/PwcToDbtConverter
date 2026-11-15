namespace PwcToDbtConverter.Model;

public sealed record ConnectorDef(string FromInstance, string FromField, string ToInstance, string ToField);
