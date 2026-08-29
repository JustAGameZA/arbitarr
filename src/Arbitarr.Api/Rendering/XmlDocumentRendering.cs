using System.Text;
using System.Xml.Linq;

namespace Arbitarr.Api.Rendering;

/// <summary>
/// Renders an <see cref="XDocument"/> to a string including its XML declaration.
/// <see cref="XDocument.ToString()"/> deliberately omits the declaration by default, but
/// Torznab/Newznab clients (and the golden-XML fixtures) expect the standard
/// <c>&lt;?xml version="1.0" encoding="UTF-8"?&gt;</c> prolog on every response.
/// </summary>
internal static class XmlDocumentRendering
{
    public static string ToXmlString(XDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        using var stringWriter = new Utf8StringWriter();
        document.Save(stringWriter);
        return stringWriter.ToString();
    }

    private sealed class Utf8StringWriter : StringWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
    }
}
