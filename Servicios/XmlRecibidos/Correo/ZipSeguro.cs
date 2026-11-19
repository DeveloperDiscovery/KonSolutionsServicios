using System.IO.Compression;

namespace KonSolutions.XmlRecibidos;

public class ZipInvalidoException : Exception { public ZipInvalidoException(string mensaje, Exception? inner = null) : base(mensaje, inner) { } }

/// <summary>Desempaqueta un ZIP en memoria sin confiar en él: se queda solo con los .xml y .pdf, usa únicamente el nombre del archivo (nada de «..\..\» ni carpetas, así no puede escribir fuera de donde se guarda),
/// cuenta lo que realmente se descomprime (no lo que el ZIP dice que pesa) y corta si pasa de los límites. Un ZIP dentro de otro ZIP se abre una vez más.</summary>
public static class ZipSeguro
{
    private static readonly string[] Util = { ".xml", ".pdf" };

    public static IReadOnlyList<ArchivoRecibido> Extraer(byte[] zip, string nombreZip, long maxDescomprimidoBytes, int maxArchivos, int profundidad = 0)
    {
        var res = new List<ArchivoRecibido>();
        long total = 0; int entradas = 0;
        Recorre(zip, nombreZip, maxDescomprimidoBytes, maxArchivos, profundidad, res, ref total, ref entradas);
        return res;
    }

    private static void Recorre(byte[] zip, string nombreZip, long max, int maxArchivos, int profundidad, List<ArchivoRecibido> res, ref long total, ref int entradas)
    {
        try
        {
            using var ms = new MemoryStream(zip, false);
            using var za = new ZipArchive(ms, ZipArchiveMode.Read);
            foreach (var e in za.Entries)
            {
                if (e.FullName.EndsWith('/') || e.FullName.EndsWith('\\') || string.IsNullOrEmpty(e.Name)) continue;       // carpeta
                var nombre = XmlTexto.NombreSeguro(e.Name);
                var ext = Path.GetExtension(nombre).ToLowerInvariant();
                var esZip = ext == ".zip";
                if (!Util.Contains(ext) && !(esZip && profundidad < 1)) continue;                                            // lo que no sirve se ignora
                if (++entradas > maxArchivos) throw new ZipInvalidoException($"«{nombreZip}» tiene demasiados archivos (el máximo es {maxArchivos}).");
                using var s = e.Open();
                using var salida = new MemoryStream();
                var buf = new byte[81920]; int n;
                while ((n = s.Read(buf, 0, buf.Length)) > 0)
                {
                    total += n;
                    if (total > max) throw new ZipInvalidoException($"«{nombreZip}» pesa más de {max / 1024 / 1024} MB ya desempaquetado.");
                    salida.Write(buf, 0, n);
                }
                if (esZip) Recorre(salida.ToArray(), nombre, max, maxArchivos, profundidad + 1, res, ref total, ref entradas);
                else res.Add(new ArchivoRecibido(nombre, salida.ToArray(), nombreZip));
            }
        }
        catch (ZipInvalidoException) { throw; }
        catch (Exception ex) when (ex is InvalidDataException or NotSupportedException or IOException)
        {
            throw new ZipInvalidoException($"«{nombreZip}» no se pudo abrir: {(ex is InvalidDataException ? "no es un ZIP válido, está dañado o tiene clave" : ex.Message)}.", ex);
        }
    }
}
