namespace PwcToDbtConverter.Model;

[Flags]
public enum PortType
{
    None = 0,
    Input = 1,
    Output = 2,
    Master = 4,
    Variable = 8,
}
