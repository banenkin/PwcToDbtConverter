using System.Xml.Linq;

namespace PwcToDbtConverter.Parsing;

/// <summary>
/// Reads PowerCenter attributes. A missing attribute and an empty one both come back as null,
/// because exports write VALUE="" for unset table attributes.
/// </summary>
public static class XmlAttributeReader
{
    public static string? Read(XElement element, string name)
    {
        string? value = (string?)element.Attribute(name);
        if (string.IsNullOrWhiteSpace(value))
        {
            return null;
        }

        return value;
    }
}
