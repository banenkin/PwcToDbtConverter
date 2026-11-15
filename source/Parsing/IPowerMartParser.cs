using PwcToDbtConverter.Model;

namespace PwcToDbtConverter.Parsing;

/// <summary>
/// Reads a PowerCenter XML export into a <see cref="PowerMartDocument"/>.
/// Unknown or incomplete content becomes a <see cref="Diagnostic"/>.
/// Throws <see cref="PowerMartParseException"/> only when the input cannot be read as a POWERMART document.
/// </summary>
public interface IPowerMartParser
{
    PowerMartDocument Parse(string path);

    PowerMartDocument Parse(TextReader reader, string sourceName);
}
