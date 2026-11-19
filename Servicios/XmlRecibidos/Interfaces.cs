using KonSolutionsServicios;

namespace KonSolutions.XmlRecibidos;

/// <summary>Lo que el servicio de XML recibidos necesita de Microsoft 365 (Microsoft Graph). Separado en una interfaz para poder probar el servicio con un buzón de mentira.</summary>
public interface IGraphCorreo
{
    bool Configurado { get; }
    string? Buzon { get; }
    /// <summary>El id de la carpeta del buzón a partir de su ruta («Bandeja de entrada/Comprobantes»), o null si no existe.</summary>
    Task<string?> ResolverCarpetaAsync(string ruta, CancellationToken ct);
    Task<IReadOnlyList<CorreoResumen>> ListarCorreosAsync(string carpetaId, int max, CancellationToken ct);
    Task<IReadOnlyList<AdjuntoResumen>> ListarAdjuntosAsync(string correoId, CancellationToken ct);
    Task<byte[]> DescargarAdjuntoAsync(string correoId, string adjuntoId, CancellationToken ct);
    Task MoverAsync(string correoId, string carpetaDestinoId, CancellationToken ct);
}

public interface IXmlRecibidosRepository
{
    Task<ConfiguracionGeneral?> ConfigAsync();
    /// <summary>Carga el XML (PROC_XML_RECIBIDOS_CARGAR). Si el XML se rechaza (vacío, mal formado, de otro RUC, emitido por la empresa...) lanza SqlException con un número 50700 a 50734.</summary>
    Task<(string Clase, decimal Numero, bool Nuevo)> CargarAsync(string xml, string nombreArchivo, string codUsuario, string codEstacion);
    Task AsignarPdfAsync(string clase, decimal numero, string nombrePdf);
    /// <summary>La Clave SOL de Configuración general (la clave sale descifrada por PROC_ESTANDAR_CONFIGURACION_GENERAL_PASSWORD_VCDP_LEER); null si no hay usuario o clave.</summary>
    Task<CredencialSol?> CredencialSolAsync();
    Task<SunatConsulta?> SunatConsultaAsync(decimal numCpe);
    Task SunatRegistrarAsync(decimal numCpe, string codigo, string mensaje);
    Task<IReadOnlyList<decimal>> SunatPendientesAsync(int max);
    /// <summary>Revisa la guía (o todas las que no tienen resultado, sin número) con PROC_LOGISTICA_GRE_RECIBIDAS_VALIDAR (script 53).</summary>
    Task ValidarGuiaAsync(decimal? numGre);
}

public interface IXmlRecibidosService
{
    /// <summary>Una corrida: lee la carpeta de recepción del buzón, baja los XML y PDF (desempaquetando los ZIP), carga los XML y mueve cada correo a procesados o a errores.
    /// Programada: si la carga automática está apagada no hace nada. Manual (ignorarActivo): corre aunque esté apagada.</summary>
    Task<XmlRecibidosCiclo> EjecutarAsync(bool ignorarActivo, CancellationToken ct);
    Task<XmlRecibidosEstadoDto> EstadoAsync();
}
