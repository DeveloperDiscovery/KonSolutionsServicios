using KonSolutionsServicios;
using System.Data;
using Dapper;

namespace KonSolutions.XmlRecibidos;

/// <summary>Lo que el servicio de XML recibidos usa de la base: la configuración general y los procedimientos del script 41 (PROC_XML_RECIBIDOS_CARGAR y PROC_XML_RECIBIDOS_PDF_ASIGNAR).
/// Un XML que se rechaza llega como SqlException con un número propio del script (50700 a 50734) y un mensaje claro.</summary>
public class XmlRecibidosRepository : IXmlRecibidosRepository
{
    private readonly IDbConnectionFactory _db;
    public XmlRecibidosRepository(IDbConnectionFactory db) => _db = db;

    public async Task<ConfiguracionGeneral?> ConfigAsync()
    {
        using var conn = _db.CreateConnection();
        var filas = await conn.QueryAsync<ConfiguracionGeneral>("PROC_ESTANDAR_CONFIGURACION_GENERAL_SHOWALL", commandType: CommandType.StoredProcedure);
        return filas.FirstOrDefault();
    }

    private sealed class FilaCarga { public string COD_CLASE { get; set; } = ""; public decimal NUM_DOCUMENTO { get; set; } public bool FLG_NUEVO { get; set; } }

    public async Task<(string Clase, decimal Numero, bool Nuevo)> CargarAsync(string xml, string nombreArchivo, string codUsuario, string codEstacion)
    {
        using var conn = _db.CreateConnection();
        var p = new DynamicParameters();
        p.Add("DES_XML", xml, DbType.String, size: -1);
        p.Add("DES_NOMBRE_ARCHIVO", nombreArchivo.Length > 260 ? nombreArchivo[^260..] : nombreArchivo);
        p.Add("COD_USUARIO_REGISTRO", codUsuario);
        p.Add("COD_ESTACION_REGISTRO", codEstacion);
        var f = await conn.QueryFirstAsync<FilaCarga>("PROC_XML_RECIBIDOS_CARGAR", p, commandType: CommandType.StoredProcedure);
        return (f.COD_CLASE.Trim(), f.NUM_DOCUMENTO, f.FLG_NUEVO);
    }

    public async Task AsignarPdfAsync(string clase, decimal numero, string nombrePdf)
    {
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync("PROC_XML_RECIBIDOS_PDF_ASIGNAR", new { COD_CLASE = clase, NUM_DOCUMENTO = numero, DES_NOMBRE_ARCHIVO_PDF = nombrePdf, FLG_REEMPLAZAR = false }, commandType: CommandType.StoredProcedure);
    }

    private sealed class FilaSol { public string? NUM_DOCUMENTO_IDENTIDAD { get; set; } public string? COD_USUARIO_VCDP { get; set; } public string? DES_PASSWORD_VCDP { get; set; } }

    public async Task<CredencialSol?> CredencialSolAsync()
    {
        using var conn = _db.CreateConnection();
        var f = await conn.QueryFirstOrDefaultAsync<FilaSol>("PROC_ESTANDAR_CONFIGURACION_GENERAL_PASSWORD_VCDP_LEER", new { DES_NOMBRE_EMPRESA = (string?)null }, commandType: CommandType.StoredProcedure);
        if (f is null || string.IsNullOrWhiteSpace(f.NUM_DOCUMENTO_IDENTIDAD) || string.IsNullOrWhiteSpace(f.COD_USUARIO_VCDP) || string.IsNullOrEmpty(f.DES_PASSWORD_VCDP)) return null;
        return new CredencialSol(f.NUM_DOCUMENTO_IDENTIDAD.Trim(), f.COD_USUARIO_VCDP.Trim(), f.DES_PASSWORD_VCDP);
    }

    private sealed class FilaCpe
    {
        public string? NUM_RUC_EMISOR { get; set; } public string? COD_TIPO_COMPROBANTE_SUNAT { get; set; } public string? NUM_SERIE { get; set; } public string? NUM_CORRELATIVO { get; set; }
        public DateTime FEC_EMISION { get; set; } public decimal VAL_TOTAL { get; set; } public string? COD_TIPO_DOC_RECEPTOR { get; set; } public string? NUM_DOC_RECEPTOR { get; set; }
    }

    private static SunatConsulta? Consulta(FilaCpe? f) => f is null ? null : new SunatConsulta
    {
        RucEmisor = f.NUM_RUC_EMISOR?.Trim() ?? "", Tipo = f.COD_TIPO_COMPROBANTE_SUNAT?.Trim() ?? "", Serie = f.NUM_SERIE?.Trim() ?? "", Numero = f.NUM_CORRELATIVO?.Trim() ?? "",
        Fecha = f.FEC_EMISION, Importe = f.VAL_TOTAL, TipoDocReceptor = f.COD_TIPO_DOC_RECEPTOR?.Trim(), NumDocReceptor = f.NUM_DOC_RECEPTOR?.Trim()
    };

    public async Task<SunatConsulta?> SunatConsultaAsync(decimal numCpe)
    {
        using var conn = _db.CreateConnection();
        return Consulta(await conn.QueryFirstOrDefaultAsync<FilaCpe>("PROC_FINANZAS_CPE_RECIBIDOS_SHOWBYID", new { NUM_CPE = numCpe }, commandType: CommandType.StoredProcedure));
    }

    public async Task SunatRegistrarAsync(decimal numCpe, string codigo, string mensaje)
    {
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync("PROC_FINANZAS_CPE_RECIBIDOS_SUNAT_REGISTRAR", new { NUM_CPE = numCpe, COD_ESTADO_SUNAT = codigo, DES_ESTADO_SUNAT = mensaje }, commandType: CommandType.StoredProcedure);
    }

    public async Task ValidarGuiaAsync(decimal? numGre)
    {
        using var conn = _db.CreateConnection();
        await conn.ExecuteAsync("PROC_LOGISTICA_GRE_RECIBIDAS_VALIDAR", new { NUM_GRE = numGre }, commandType: CommandType.StoredProcedure);
    }

    public async Task<IReadOnlyList<decimal>> SunatPendientesAsync(int max)
    {
        using var conn = _db.CreateConnection();
        return (await conn.QueryAsync<decimal>("SELECT NUM_CPE FROM FINANZAS.CPE_RECIBIDOS WHERE COD_ESTADO_SUNAT IS NULL OR COD_ESTADO_SUNAT = N'ERR' ORDER BY NUM_CPE OFFSET 0 ROWS FETCH NEXT @max ROWS ONLY", new { max })).ToList();
    }
}
