using System.Collections.Frozen;
using System.Globalization;
using System.Text;
using System.Xml;
using System.Xml.Linq;
using PwcToDbtConverter.Model;

namespace PwcToDbtConverter.Parsing;

/// <summary>
/// LINQ to XML reader for a PowerCenter export. It types every element the later stages need
/// and records a diagnostic for anything it does not understand.
/// </summary>
public sealed class PowerMartXmlParser : IPowerMartParser
{
    private const string UnionTemplateName = "Union Transformation";

    public PowerMartDocument Parse(string path)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        if (!File.Exists(path))
        {
            throw new PowerMartParseException(path, "File not found.");
        }

        try
        {
            using XmlReader reader = XmlReader.Create(path, CreateReaderSettings());
            return Load(reader, path);
        }
        catch (PowerMartParseException)
        {
            throw;
        }
        catch (Exception exception) when (exception is XmlException or IOException or NotSupportedException)
        {
            throw new PowerMartParseException(path, "The file is not readable XML.", exception);
        }
    }

    public PowerMartDocument Parse(TextReader reader, string sourceName)
    {
        Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);

        try
        {
            using XmlReader xmlReader = XmlReader.Create(reader, CreateReaderSettings());
            return Load(xmlReader, sourceName);
        }
        catch (PowerMartParseException)
        {
            throw;
        }
        catch (Exception exception) when (exception is XmlException or NotSupportedException)
        {
            throw new PowerMartParseException(sourceName, "The file is not readable XML.", exception);
        }
    }

    private static PowerMartDocument Load(XmlReader reader, string sourceName)
    {
        XDocument document = XDocument.Load(reader);
        if (document.Root is null || !string.Equals(document.Root.Name.LocalName, "POWERMART", StringComparison.Ordinal))
        {
            throw new PowerMartParseException(sourceName, "The document has no POWERMART root.");
        }

        var diagnostics = new List<Diagnostic>();
        List<XElement> folders = document.Root.Elements("REPOSITORY").Elements("FOLDER").ToList();
        if (folders.Count == 0)
        {
            diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, string.Empty, null, "The export has no FOLDER element."));
            return new PowerMartDocument(string.Empty, [], [], [], Freeze(diagnostics));
        }

        if (folders.Count > 1)
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Warning,
                string.Empty,
                null,
                "The export has more than one FOLDER. All of them are read."));
        }

        var sources = new List<SourceDef>();
        var targets = new List<TargetDef>();
        var mappings = new List<MappingDef>();
        foreach (XElement folder in folders)
        {
            if (XmlAttributeReader.Read(folder, "NAME") is null)
            {
                diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, string.Empty, null, "FOLDER is missing NAME."));
            }

            foreach (XElement source in folder.Elements("SOURCE"))
            {
                SourceDef? parsed = ParseSource(source, diagnostics);
                if (parsed is not null)
                {
                    sources.Add(parsed);
                }
            }

            foreach (XElement target in folder.Elements("TARGET"))
            {
                TargetDef? parsed = ParseTarget(target, diagnostics);
                if (parsed is not null)
                {
                    targets.Add(parsed);
                }
            }

            foreach (XElement mapping in folder.Elements("MAPPING"))
            {
                MappingDef? parsed = ParseMapping(mapping, diagnostics);
                if (parsed is not null)
                {
                    mappings.Add(parsed);
                }
            }
        }

        string documentFolder = XmlAttributeReader.Read(folders[0], "NAME") ?? string.Empty;
        return new PowerMartDocument(
            documentFolder,
            Freeze(sources),
            Freeze(targets),
            Freeze(mappings),
            Freeze(diagnostics));
    }

    private static SourceDef? ParseSource(XElement element, List<Diagnostic> diagnostics)
    {
        string? name = XmlAttributeReader.Read(element, "NAME");
        if (name is null)
        {
            diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, string.Empty, null, "SOURCE is missing NAME."));
            return null;
        }

        string? ownerName = XmlAttributeReader.Read(element, "OWNERNAME");
        string? dbdName = XmlAttributeReader.Read(element, "DBDNAME");
        if (ownerName is null)
        {
            diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, string.Empty, name, "SOURCE is missing OWNERNAME."));
        }

        if (dbdName is null)
        {
            diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, string.Empty, name, "SOURCE is missing DBDNAME."));
        }

        return new SourceDef(
            name,
            ownerName,
            dbdName,
            XmlAttributeReader.Read(element, "DATABASETYPE"),
            ParseFields(element.Elements("SOURCEFIELD"), name, diagnostics));
    }

    private static TargetDef? ParseTarget(XElement element, List<Diagnostic> diagnostics)
    {
        string? name = XmlAttributeReader.Read(element, "NAME");
        if (name is null)
        {
            diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, string.Empty, null, "TARGET is missing NAME."));
            return null;
        }

        return new TargetDef(
            name,
            XmlAttributeReader.Read(element, "DATABASETYPE"),
            ParseFields(element.Elements("TARGETFIELD"), name, diagnostics));
    }

    private static IReadOnlyList<FieldDef> ParseFields(
        IEnumerable<XElement> elements,
        string ownerName,
        List<Diagnostic> diagnostics)
    {
        var fields = new List<FieldDef>();
        foreach (XElement element in elements)
        {
            string? name = XmlAttributeReader.Read(element, "NAME");
            if (name is null)
            {
                diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, string.Empty, ownerName, "Field is missing NAME."));
                continue;
            }

            fields.Add(new FieldDef(
                name,
                ReadDataType(element, string.Empty, ownerName, diagnostics),
                ReadInt(element, "PRECISION", string.Empty, ownerName, diagnostics),
                ReadInt(element, "SCALE", string.Empty, ownerName, diagnostics),
                ReadNullable(element, ownerName, diagnostics),
                ReadKeyType(element, ownerName, diagnostics),
                XmlAttributeReader.Read(element, "REFERENCEDDBD"),
                XmlAttributeReader.Read(element, "REFERENCEDTABLE"),
                XmlAttributeReader.Read(element, "REFERENCEDFIELD")));
        }

        return Freeze(fields);
    }

    private static MappingDef? ParseMapping(XElement element, List<Diagnostic> diagnostics)
    {
        string? name = XmlAttributeReader.Read(element, "NAME");
        if (name is null)
        {
            diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, string.Empty, null, "MAPPING is missing NAME."));
            return null;
        }

        string? isValid = XmlAttributeReader.Read(element, "ISVALID");
        bool valid = !string.Equals(isValid, "NO", StringComparison.Ordinal);
        if (string.Equals(isValid, "NO", StringComparison.Ordinal))
        {
            diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, name, null, "Mapping is marked ISVALID=NO."));
        }
        else if (isValid is not null && !string.Equals(isValid, "YES", StringComparison.Ordinal))
        {
            valid = false;
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Warning,
                name,
                null,
                $"Mapping ISVALID value '{isValid}' is not YES or NO."));
        }

        var transformations = new List<TransformationDef>();
        foreach (XElement transformation in element.Elements("TRANSFORMATION"))
        {
            TransformationDef? parsed = ParseTransformation(transformation, name, diagnostics);
            if (parsed is not null)
            {
                transformations.Add(parsed);
            }
        }

        var instances = new List<InstanceDef>();
        foreach (XElement instance in element.Elements("INSTANCE"))
        {
            InstanceDef? parsed = ParseInstance(instance, name, diagnostics);
            if (parsed is not null)
            {
                instances.Add(parsed);
            }
        }

        var connectors = new List<ConnectorDef>();
        foreach (XElement connector in element.Elements("CONNECTOR"))
        {
            ConnectorDef? parsed = ParseConnector(connector, name, diagnostics);
            if (parsed is not null)
            {
                connectors.Add(parsed);
            }
        }

        var variables = new List<MappingVariableDef>();
        foreach (XElement variable in element.Elements("MAPPINGVARIABLE"))
        {
            MappingVariableDef? parsed = ParseVariable(variable, name, diagnostics);
            if (parsed is not null)
            {
                variables.Add(parsed);
            }
        }

        return new MappingDef(name, valid, Freeze(instances), Freeze(transformations), Freeze(connectors), Freeze(variables));
    }

    private static TransformationDef? ParseTransformation(XElement element, string mapping, List<Diagnostic> diagnostics)
    {
        string? name = XmlAttributeReader.Read(element, "NAME");
        if (name is null)
        {
            diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, mapping, null, "TRANSFORMATION is missing NAME."));
            return null;
        }

        string? templateName = XmlAttributeReader.Read(element, "TEMPLATENAME");
        TransformationKind kind = Classify(XmlAttributeReader.Read(element, "TYPE"), templateName, mapping, name, diagnostics);

        var ports = new List<PortDef>();
        int position = 0;
        foreach (XElement field in element.Elements("TRANSFORMFIELD"))
        {
            PortDef? port = ParsePort(field, position, mapping, name, diagnostics);
            if (port is not null)
            {
                ports.Add(port);
                position++;
            }
        }

        var groups = new List<GroupDef>();
        int groupIndex = 0;
        foreach (XElement group in element.Elements("GROUP"))
        {
            GroupDef? parsed = ParseGroup(group, groupIndex, mapping, name, diagnostics);
            if (parsed is not null)
            {
                groups.Add(parsed);
            }

            groupIndex++;
        }

        return new TransformationDef(
            name,
            kind,
            templateName,
            Freeze(ports),
            Freeze(groups),
            ReadAttributes(element, mapping, name, diagnostics));
    }

    private static PortDef? ParsePort(
        XElement element,
        int position,
        string mapping,
        string transformation,
        List<Diagnostic> diagnostics)
    {
        string? name = XmlAttributeReader.Read(element, "NAME");
        if (name is null)
        {
            diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, mapping, transformation, "TRANSFORMFIELD is missing NAME."));
            return null;
        }

        return new PortDef(
            name,
            ReadPortType(element, mapping, transformation, name, diagnostics),
            position,
            ReadDataType(element, mapping, transformation, diagnostics),
            ReadInt(element, "PRECISION", mapping, transformation, diagnostics),
            ReadInt(element, "SCALE", mapping, transformation, diagnostics),
            XmlAttributeReader.Read(element, "EXPRESSION"),
            ReadExpressionKind(element, mapping, transformation, name, diagnostics),
            XmlAttributeReader.Read(element, "GROUP"),
            XmlAttributeReader.Read(element, "REF_FIELD"),
            ReadSortKey(element, mapping, transformation, name, diagnostics),
            XmlAttributeReader.Read(element, "SORTDIRECTION"));
    }

    private static GroupDef? ParseGroup(
        XElement element,
        int index,
        string mapping,
        string transformation,
        List<Diagnostic> diagnostics)
    {
        string? name = XmlAttributeReader.Read(element, "NAME");
        if (name is null)
        {
            diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, mapping, transformation, "GROUP is missing NAME."));
            return null;
        }

        int? order = ReadInt(element, "ORDER", mapping, transformation, diagnostics);
        return new GroupDef(
            name,
            XmlAttributeReader.Read(element, "TYPE") ?? string.Empty,
            order ?? index + 1,
            XmlAttributeReader.Read(element, "EXPRESSION"));
    }

    private static InstanceDef? ParseInstance(XElement element, string mapping, List<Diagnostic> diagnostics)
    {
        string? name = XmlAttributeReader.Read(element, "NAME");
        if (name is null)
        {
            diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, mapping, null, "INSTANCE is missing NAME."));
            return null;
        }

        string? transformationName = XmlAttributeReader.Read(element, "TRANSFORMATION_NAME");
        if (transformationName is null)
        {
            diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, mapping, name, "INSTANCE is missing TRANSFORMATION_NAME."));
            return null;
        }

        string? instanceType = XmlAttributeReader.Read(element, "TYPE");
        InstanceKind? kind = ReadInstanceKind(instanceType);
        if (kind is null)
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Warning,
                mapping,
                name,
                $"Unknown instance type '{instanceType ?? "(none)"}'."));
            return null;
        }

        var associatedSources = new List<string>();
        foreach (XElement associated in element.Elements("ASSOCIATED_SOURCE_INSTANCE"))
        {
            string? associatedName = XmlAttributeReader.Read(associated, "NAME");
            if (associatedName is null)
            {
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Warning,
                    mapping,
                    name,
                    "ASSOCIATED_SOURCE_INSTANCE is missing NAME."));
                continue;
            }

            associatedSources.Add(associatedName);
        }

        return new InstanceDef(
            name,
            transformationName,
            kind.Value,
            Freeze(associatedSources),
            ReadAttributes(element, mapping, name, diagnostics));
    }

    private static ConnectorDef? ParseConnector(XElement element, string mapping, List<Diagnostic> diagnostics)
    {
        string? fromInstance = XmlAttributeReader.Read(element, "FROMINSTANCE");
        string? fromField = XmlAttributeReader.Read(element, "FROMFIELD");
        string? toInstance = XmlAttributeReader.Read(element, "TOINSTANCE");
        string? toField = XmlAttributeReader.Read(element, "TOFIELD");
        if (fromInstance is null || fromField is null || toInstance is null || toField is null)
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Warning,
                mapping,
                null,
                "CONNECTOR is missing FROMINSTANCE, FROMFIELD, TOINSTANCE or TOFIELD."));
            return null;
        }

        return new ConnectorDef(fromInstance, fromField, toInstance, toField);
    }

    private static MappingVariableDef? ParseVariable(XElement element, string mapping, List<Diagnostic> diagnostics)
    {
        string? name = XmlAttributeReader.Read(element, "NAME");
        if (name is null)
        {
            diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, mapping, null, "MAPPINGVARIABLE is missing NAME."));
            return null;
        }

        return new MappingVariableDef(
            name,
            XmlAttributeReader.Read(element, "DATATYPE"),
            XmlAttributeReader.Read(element, "DEFAULTVALUE"),
            string.Equals(XmlAttributeReader.Read(element, "ISPARAM"), "YES", StringComparison.Ordinal));
    }

    private static TransformationKind Classify(
        string? type,
        string? templateName,
        string mapping,
        string name,
        List<Diagnostic> diagnostics)
    {
        // Union is a custom transformation. TYPE alone cannot tell it apart from other templates.
        if (string.Equals(type, "Custom Transformation", StringComparison.Ordinal))
        {
            if (string.Equals(templateName, UnionTemplateName, StringComparison.Ordinal))
            {
                return TransformationKind.Union;
            }

            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Warning,
                mapping,
                name,
                $"Unsupported custom transformation template '{templateName ?? "(none)"}'."));
            return TransformationKind.Unsupported;
        }

        TransformationKind kind = type switch
        {
            "Source Qualifier" => TransformationKind.SourceQualifier,
            "Expression" => TransformationKind.Expression,
            "Filter" => TransformationKind.Filter,
            "Joiner" => TransformationKind.Joiner,
            "Sorter" => TransformationKind.Sorter,
            "Aggregator" => TransformationKind.Aggregator,
            "Router" => TransformationKind.Router,
            _ => TransformationKind.Unsupported,
        };

        if (kind == TransformationKind.Unsupported)
        {
            diagnostics.Add(new Diagnostic(
                DiagnosticSeverity.Warning,
                mapping,
                name,
                $"Unsupported transformation type '{type ?? "(none)"}'."));
        }

        return kind;
    }

    private static PortType ReadPortType(
        XElement element,
        string mapping,
        string transformation,
        string port,
        List<Diagnostic> diagnostics)
    {
        string? text = XmlAttributeReader.Read(element, "PORTTYPE");
        if (text is null)
        {
            diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, mapping, transformation, $"Port '{port}' is missing PORTTYPE."));
            return PortType.None;
        }

        if (string.Equals(text, "LOCAL VARIABLE", StringComparison.Ordinal))
        {
            return PortType.Variable;
        }

        PortType flags = PortType.None;
        foreach (string token in text.Split('/'))
        {
            if (string.Equals(token, "INPUT", StringComparison.Ordinal))
            {
                flags |= PortType.Input;
            }
            else if (string.Equals(token, "OUTPUT", StringComparison.Ordinal))
            {
                flags |= PortType.Output;
            }
            else if (string.Equals(token, "MASTER", StringComparison.Ordinal))
            {
                flags |= PortType.Master;
            }
            else
            {
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Warning,
                    mapping,
                    transformation,
                    $"Port '{port}' has unknown PORTTYPE token '{token}'."));
            }
        }

        return flags;
    }

    private static ExpressionKind ReadExpressionKind(
        XElement element,
        string mapping,
        string transformation,
        string port,
        List<Diagnostic> diagnostics)
    {
        string? text = XmlAttributeReader.Read(element, "EXPRESSIONTYPE");
        if (text is null)
        {
            return ExpressionKind.None;
        }

        if (string.Equals(text, "GENERAL", StringComparison.Ordinal))
        {
            return ExpressionKind.General;
        }

        if (string.Equals(text, "GROUPBY", StringComparison.Ordinal))
        {
            return ExpressionKind.GroupBy;
        }

        diagnostics.Add(new Diagnostic(
            DiagnosticSeverity.Warning,
            mapping,
            transformation,
            $"Port '{port}' has unknown EXPRESSIONTYPE '{text}'."));
        return ExpressionKind.None;
    }

    private static bool ReadSortKey(
        XElement element,
        string mapping,
        string transformation,
        string port,
        List<Diagnostic> diagnostics)
    {
        string? text = XmlAttributeReader.Read(element, "ISSORTKEY");
        if (text is null || string.Equals(text, "NO", StringComparison.Ordinal))
        {
            return false;
        }

        if (string.Equals(text, "YES", StringComparison.Ordinal))
        {
            return true;
        }

        diagnostics.Add(new Diagnostic(
            DiagnosticSeverity.Warning,
            mapping,
            transformation,
            $"Port '{port}' has unknown ISSORTKEY '{text}'."));
        return false;
    }

    private static bool ReadNullable(XElement element, string ownerName, List<Diagnostic> diagnostics)
    {
        string? text = XmlAttributeReader.Read(element, "NULLABLE");
        if (text is null || string.Equals(text, "NULL", StringComparison.Ordinal))
        {
            return true;
        }

        if (string.Equals(text, "NOTNULL", StringComparison.Ordinal))
        {
            return false;
        }

        string? field = XmlAttributeReader.Read(element, "NAME");
        diagnostics.Add(new Diagnostic(
            DiagnosticSeverity.Warning,
            string.Empty,
            ownerName,
            $"Field '{field}' has unknown NULLABLE '{text}'."));
        return true;
    }

    private static KeyType ReadKeyType(XElement element, string ownerName, List<Diagnostic> diagnostics)
    {
        string? text = XmlAttributeReader.Read(element, "KEYTYPE");
        if (text is null || string.Equals(text, "NOT A KEY", StringComparison.Ordinal))
        {
            return KeyType.None;
        }

        if (string.Equals(text, "PRIMARY KEY", StringComparison.Ordinal))
        {
            return KeyType.Primary;
        }

        if (string.Equals(text, "FOREIGN KEY", StringComparison.Ordinal))
        {
            return KeyType.Foreign;
        }

        string? field = XmlAttributeReader.Read(element, "NAME");
        diagnostics.Add(new Diagnostic(
            DiagnosticSeverity.Warning,
            string.Empty,
            ownerName,
            $"Field '{field}' has unknown KEYTYPE '{text}'."));
        return KeyType.None;
    }

    private static InstanceKind? ReadInstanceKind(string? type)
    {
        return type switch
        {
            "SOURCE" => InstanceKind.Source,
            "TARGET" => InstanceKind.Target,
            "TRANSFORMATION" => InstanceKind.Transformation,
            _ => null,
        };
    }

    private static string ReadDataType(XElement element, string mapping, string instance, List<Diagnostic> diagnostics)
    {
        string? dataType = XmlAttributeReader.Read(element, "DATATYPE");
        if (dataType is not null)
        {
            return dataType;
        }

        string? name = XmlAttributeReader.Read(element, "NAME");
        diagnostics.Add(new Diagnostic(
            DiagnosticSeverity.Warning,
            mapping,
            instance,
            $"'{name}' is missing DATATYPE."));
        return string.Empty;
    }

    private static int? ReadInt(
        XElement element,
        string attribute,
        string mapping,
        string? instance,
        List<Diagnostic> diagnostics)
    {
        string? text = XmlAttributeReader.Read(element, attribute);
        if (text is null)
        {
            return null;
        }

        if (int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out int value))
        {
            return value;
        }

        diagnostics.Add(new Diagnostic(
            DiagnosticSeverity.Warning,
            mapping,
            instance,
            $"Attribute {attribute} is not an integer ('{text}')."));
        return null;
    }

    private static IReadOnlyDictionary<string, string?> ReadAttributes(
        XElement element,
        string mapping,
        string? instance,
        List<Diagnostic> diagnostics)
    {
        var attributes = new Dictionary<string, string?>(StringComparer.Ordinal);
        foreach (XElement tableAttribute in element.Elements("TABLEATTRIBUTE"))
        {
            string? name = XmlAttributeReader.Read(tableAttribute, "NAME");
            if (name is null)
            {
                diagnostics.Add(new Diagnostic(DiagnosticSeverity.Warning, mapping, instance, "TABLEATTRIBUTE is missing NAME."));
                continue;
            }

            if (attributes.ContainsKey(name))
            {
                diagnostics.Add(new Diagnostic(
                    DiagnosticSeverity.Warning,
                    mapping,
                    instance,
                    $"Duplicate table attribute '{name}'."));
                continue;
            }

            attributes.Add(name, XmlAttributeReader.Read(tableAttribute, "VALUE"));
        }

        if (attributes.Count == 0)
        {
            return FrozenDictionary<string, string?>.Empty;
        }

        return attributes.ToFrozenDictionary(StringComparer.Ordinal);
    }

    private static IReadOnlyList<T> Freeze<T>(List<T> values)
    {
        return values.Count == 0 ? Array.Empty<T>() : values.ToArray();
    }

    private static XmlReaderSettings CreateReaderSettings()
    {
        // Prohibit throws on the DOCTYPE in these exports, and a resolver would fetch powrmart.dtd.
        return new XmlReaderSettings
        {
            DtdProcessing = DtdProcessing.Ignore,
            XmlResolver = null,
        };
    }
}
