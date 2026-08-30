using System.Text;
using System.Xml;
using System.Xml.Linq;

namespace Arbitarr.Api.Rendering;

/// <summary>
/// Renders an <see cref="XDocument"/> to a string including its XML declaration.
/// <see cref="XDocument.ToString()"/> deliberately omits the declaration by default, but
/// Torznab/Newznab clients (and the golden-XML fixtures) expect the standard
/// <c>&lt;?xml version="1.0" encoding="UTF-8"?&gt;</c> prolog on every response.
///
/// The newline used to separate lines is pinned to <c>"\n"</c> explicitly (not
/// <see cref="Environment.NewLine"/>, which is <c>"\r\n"</c> on Windows and <c>"\n"</c> on
/// Linux) so the wire format is byte-identical regardless of the host the process runs on —
/// local development on Windows must produce the same bytes as the Linux CI/deployment target.
/// </summary>
internal static class XmlDocumentRendering
{
    private static readonly XmlWriterSettings Settings = new()
    {
        Indent = true,
        IndentChars = "  ",
        NewLineChars = "\n",
        NewLineHandling = NewLineHandling.Replace,
        OmitXmlDeclaration = false,
    };

    public static string ToXmlString(XDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        using var stringWriter = new Utf8StringWriter();
        using (var xmlWriter = XmlWriter.Create(stringWriter, Settings))
        {
            document.Save(xmlWriter);
            xmlWriter.Flush();
        }

        return stringWriter.ToString();
    }

    private sealed class Utf8StringWriter : StringWriter
    {
        public override Encoding Encoding => Encoding.UTF8;
    }
}
