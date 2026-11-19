namespace KonSolutions.XmlRecibidos;

/// <summary>Lo que el servicio necesita de Microsoft 365 y los límites de lo que baja. Sale de la sección «XmlRecibidos» de appsettings.json; el secreto se puede poner en la variable de entorno
/// KonSolutions_GRAPH_SECRET (tiene prioridad y es lo recomendado: el archivo no debe llevar el secreto real). Las carpetas de Outlook NO van aquí: están en la configuración general.</summary>
public class XmlRecibidosOpciones
{
    public string? TenantId { get; set; }
    public string? ClientId { get; set; }
    public string? ClientSecret { get; set; }
    /// <summary>El buzón del que se leen los correos (su dirección, por ejemplo compras@empresa.com).</summary>
    public string? Buzon { get; set; }
    public string AuthorityHost { get; set; } = "https://login.microsoftonline.com";
    public string GraphUrl { get; set; } = "https://graph.microsoft.com/v1.0";
    /// <summary>Lo más que se acepta de un adjunto (MB). Uno más grande se rechaza y el correo va a la carpeta de errores.</summary>
    public int MaxAdjuntoMB { get; set; } = 25;
    /// <summary>Lo más que puede pesar un ZIP ya desempaquetado (MB), contando todo lo de adentro: protege de los ZIP que se inflan.</summary>
    public int MaxZipDescomprimidoMB { get; set; } = 100;
    public int MaxArchivosPorZip { get; set; } = 200;
    /// <summary>Cuántos correos se leen en cada corrida (los más antiguos primero); los demás quedan para la siguiente.</summary>
    public int MaxCorreosPorCiclo { get; set; } = 50;
    /// <summary>A dónde va un correo sin ningún XML, PDF ni ZIP adjunto: «Errores» (por omisión, deja limpia la recepción) o «Procesados».</summary>
    public string SinAdjuntosVaA { get; set; } = "Errores";

    public string? SecretoEfectivo()
    {
        var env = Environment.GetEnvironmentVariable("KonSolutions_GRAPH_SECRET");
        return string.IsNullOrWhiteSpace(env) ? ClientSecret : env;
    }
    public bool Configurado => !string.IsNullOrWhiteSpace(TenantId) && !string.IsNullOrWhiteSpace(ClientId) && !string.IsNullOrWhiteSpace(SecretoEfectivo()) && !string.IsNullOrWhiteSpace(Buzon);
}

public record CorreoResumen(string Id, string? Asunto, string? De, DateTimeOffset? Recibido, bool TieneAdjuntos);
public record AdjuntoResumen(string Id, string Nombre, long Tamano, string? ContentType, bool EsArchivo, bool EsInline);

/// <summary>Un archivo ya listo para guardar: un adjunto, o lo que había dentro de un ZIP.</summary>
public record ArchivoRecibido(string Nombre, byte[] Contenido, string? Origen);

public class XmlRecibidosCorreoResultado
{
    public string Id { get; set; } = "";
    public string? Asunto { get; set; }
    public string? De { get; set; }
    /// <summary>PROCESADOS, ERRORES o SIN_MOVER (no se pudo mover o hubo una falla pasajera: se reintenta en la siguiente corrida).</summary>
    public string Destino { get; set; } = "";
    public List<string> Archivos { get; set; } = new();
    public List<string> Documentos { get; set; } = new();
    public List<string> Problemas { get; set; } = new();
    /// <summary>Lo que se notó pero no es un problema (por ejemplo una constancia de recepción de SUNAT que venía junto al comprobante).</summary>
    public List<string> Avisos { get; set; } = new();
}

public class XmlRecibidosCiclo
{
    public DateTime Inicio { get; set; } = DateTime.Now;
    public DateTime? Fin { get; set; }
    /// <summary>La corrida no se hizo o se cortó: aquí dice por qué. Vacío cuando todo salió bien.</summary>
    public string? Mensaje { get; set; }
    public bool Omitida { get; set; }
    public int Correos { get; set; }
    public int CorreosProcesados { get; set; }
    public int CorreosConError { get; set; }
    public int CorreosSinMover { get; set; }
    public int XmlCargados { get; set; }
    public int XmlRepetidos { get; set; }
    public int PdfGuardados { get; set; }
    /// <summary>Comprobantes que se validaron con SUNAT en esta corrida (los nuevos y los que quedaban pendientes) y los que no se pudieron consultar.</summary>
    public int SunatValidados { get; set; }
    public int SunatSinConsultar { get; set; }
    /// <summary>Hay cuenta de Microsoft 365: se leyo el correo. Si es false, solo se procesaron los archivos de la carpeta.</summary>
    public bool ConCorreo { get; set; }
    /// <summary>Lotes de archivos sueltos en la carpeta de Compras (cada XML con su PDF, o cada ZIP): los que pasaron a procesados y los que fueron a errores.</summary>
    public int CarpetaLotes { get; set; }
    public int CarpetaProcesados { get; set; }
    public int CarpetaConError { get; set; }
    public List<XmlRecibidosCorreoResultado> Detalle { get; set; } = new();
}

public class XmlRecibidosEstadoDto
{
    public bool CuentaConfigurada { get; set; }
    public string? Buzon { get; set; }
    public bool Activo { get; set; }
    public int Minutos { get; set; }
    public bool EnCurso { get; set; }
    public DateTime? UltimaCorrida { get; set; }
    public XmlRecibidosCiclo? Ultimo { get; set; }
}
