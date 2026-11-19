using System.Text;
using System.Text.RegularExpressions;
using System.Xml;

namespace KonSolutions.XmlRecibidos;

/// <summary>Pasa los bytes de un archivo XML a texto respetando su codificación: la marca BOM (UTF-8 o UTF-16) o, si no la trae, la que dice la declaración &lt;?xml encoding="..."?&gt; (UTF-8 por omisión).
/// Los de SUNAT casi siempre son UTF-8, pero hay proveedores que mandan ISO-8859-1.</summary>
public static class XmlTexto
{
    static XmlTexto() => Encoding.RegisterProvider(CodePagesEncodingProvider.Instance);
    private static readonly Regex Decl = new(@"^\s*<\?xml[^>]*encoding\s*=\s*[""']([^""']+)[""']", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    public static string Decodificar(byte[] bytes)
    {
        if (bytes.Length >= 3 && bytes[0] == 0xEF && bytes[1] == 0xBB && bytes[2] == 0xBF) return Encoding.UTF8.GetString(bytes, 3, bytes.Length - 3);
        if (bytes.Length >= 2 && bytes[0] == 0xFF && bytes[1] == 0xFE) return Encoding.Unicode.GetString(bytes, 2, bytes.Length - 2);
        if (bytes.Length >= 2 && bytes[0] == 0xFE && bytes[1] == 0xFF) return Encoding.BigEndianUnicode.GetString(bytes, 2, bytes.Length - 2);
        // la declaración está en ASCII: se lee la cabecera como Latin1 para buscar el encoding
        var cabecera = Encoding.Latin1.GetString(bytes, 0, Math.Min(bytes.Length, 200));
        var m = Decl.Match(cabecera);
        if (m.Success)
        {
            try { return Encoding.GetEncoding(m.Groups[1].Value).GetString(bytes); } catch (ArgumentException) { /* codificación que no conozco: UTF-8 */ }
        }
        return Encoding.UTF8.GetString(bytes);
    }

    /// <summary>El nombre local del elemento raíz del XML («Invoice», «ApplicationResponse»...), o null si el texto no es un XML bien formado. No resuelve DTD ni entidades externas.</summary>
    public static string? Raiz(string texto)
    {
        try
        {
            using var r = XmlReader.Create(new StringReader(texto), new XmlReaderSettings { DtdProcessing = DtdProcessing.Prohibit, XmlResolver = null, IgnoreComments = true, IgnoreWhitespace = true });
            while (r.Read()) if (r.NodeType == XmlNodeType.Element) return r.LocalName;
            return null;
        }
        catch (XmlException) { return null; }
    }

    /// <summary>Un nombre de archivo seguro para guardar en disco: sin carpetas, sin caracteres raros, sin nombres reservados y de largo razonable.</summary>
    public static string NombreSeguro(string? nombre, string porOmision = "archivo")
    {
        var n = (nombre ?? "").Replace('\\', '/');
        n = n[(n.LastIndexOf('/') + 1)..];
        // los de Windows siempre (el archivo puede terminar en un recurso compartido de Windows aunque la API corra en otro sistema) mas los del sistema donde corre
        var invalidos = "<>:\"/\\|?*".ToCharArray().Concat(Path.GetInvalidFileNameChars()).ToArray();
        var sb = new StringBuilder();
        foreach (var c in n) sb.Append(Array.IndexOf(invalidos, c) >= 0 || char.IsControl(c) ? '_' : c);
        n = sb.ToString().Trim().TrimEnd('.', ' ');
        if (n.Length == 0 || n.All(c => c == '.' || c == '_')) n = porOmision;
        var ext = Path.GetExtension(n);
        var baseN = Path.GetFileNameWithoutExtension(n);
        var reservados = new[] { "CON", "PRN", "AUX", "NUL", "COM1", "COM2", "COM3", "COM4", "LPT1", "LPT2", "LPT3" };
        if (reservados.Contains(baseN.ToUpperInvariant())) baseN = "_" + baseN;
        if (baseN.Length > 120) baseN = baseN[..120];
        return baseN + (ext.Length > 20 ? ext[..20] : ext);
    }
}
