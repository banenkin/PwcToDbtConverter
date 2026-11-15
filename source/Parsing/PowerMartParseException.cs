namespace PwcToDbtConverter.Parsing;

/// <summary>
/// Thrown when a PowerCenter export cannot be read at all.
/// The file path is included in the message.
/// </summary>
public sealed class PowerMartParseException : Exception
{
    public PowerMartParseException(string path, string message)
        : base($"{message} File: {path}.")
    {
        Path = path;
    }

    public PowerMartParseException(string path, string message, Exception inner)
        : base($"{message} File: {path}.", inner)
    {
        Path = path;
    }

    public string Path { get; }
}
