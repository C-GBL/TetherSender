using System.Globalization;
using System.Text;
using System.Xml.Linq;

namespace TetherSender.Usbmux;

/// <summary>Minimal XML plist reader/writer, enough for the usbmuxd protocol (§11.3).</summary>
public static class Plist
{
    private static readonly XDocumentType DocType = new("plist", "-//Apple//DTD PLIST 1.0//EN", "http://www.apple.com/DTDs/PropertyList-1.0.dtd", null);

    public static byte[] Serialize(IReadOnlyDictionary<string, object> dict)
    {
        var doc = new XDocument(new XDeclaration("1.0", "UTF-8", null), DocType,
            new XElement("plist", new XAttribute("version", "1.0"), ToElement(dict)));
        using var ms = new MemoryStream();
        using (var writer = new StreamWriter(ms, new UTF8Encoding(false)))
        {
            doc.Save(writer);
        }
        return ms.ToArray();
    }

    public static Dictionary<string, object> Parse(byte[] xml)
    {
        var doc = XDocument.Parse(Encoding.UTF8.GetString(xml));
        var root = doc.Root?.Elements().FirstOrDefault() ?? throw new InvalidDataException("empty plist");
        return FromElement(root) as Dictionary<string, object> ?? throw new InvalidDataException("plist root is not a dict");
    }

    private static XElement ToElement(object value) => value switch
    {
        string s => new XElement("string", s),
        bool b => new XElement(b ? "true" : "false"),
        int or long or ushort or uint => new XElement("integer", Convert.ToString(value, CultureInfo.InvariantCulture)),
        IReadOnlyDictionary<string, object> d => new XElement("dict", d.SelectMany(kv => new[] { new XElement("key", kv.Key), ToElement(kv.Value) })),
        IEnumerable<object> list => new XElement("array", list.Select(ToElement)),
        _ => throw new NotSupportedException($"plist type {value.GetType()}"),
    };

    private static object FromElement(XElement e)
    {
        switch (e.Name.LocalName)
        {
            case "dict":
                var dict = new Dictionary<string, object>();
                string? key = null;
                foreach (var child in e.Elements())
                {
                    if (child.Name.LocalName == "key") key = child.Value;
                    else if (key != null) { dict[key] = FromElement(child); key = null; }
                }
                return dict;
            case "array":
                return e.Elements().Select(FromElement).ToList();
            case "string":
                return e.Value;
            case "integer":
                return long.Parse(e.Value, CultureInfo.InvariantCulture);
            case "real":
                return double.Parse(e.Value, CultureInfo.InvariantCulture);
            case "true":
                return true;
            case "false":
                return false;
            case "data":
                return Convert.FromBase64String(e.Value.Trim());
            case "date":
                return e.Value;
            default:
                return e.Value;
        }
    }
}
