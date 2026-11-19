using KonSolutionsServicios;
using System.Net;
using System.Security.Cryptography;
using Microsoft.Data.SqlClient;
using Microsoft.Extensions.Options;

namespace KonSolutions.XmlRecibidos;

/// <summary>Lo último que pasó con el servicio, para mostrarlo: se guarda en memoria (una sola copia para todo el servicio).</summary>
public class XmlRecibidosEstado
{
    private readonly object _l = new();
    private bool _enCurso; private DateTime? _ultima; private XmlRecibidosCiclo? _ultimo;
    public void Inicia() { lock (_l) _enCurso = true; }
    public void Termina(XmlRecibidosCiclo c) { lock (_l) { _enCurso = false; _ultima = c.Fin ?? DateTime.Now; if (!c.Omitida) _ultimo = c; } }
    public (bool EnCurso, DateTime? Ultima, XmlRecibidosCiclo? Ultimo) Leer() { lock (_l) return (_enCurso, _ultima, _ultimo); }
}

/// <summary>El servicio de XML recibidos. Cada corrida: lee la carpeta de recepción del buzón de Microsoft 365, baja los XML y PDF de cada correo (desempaquetando los ZIP), carga los XML con PROC_XML_RECIBIDOS_CARGAR,
/// guarda los archivos en la ruta de archivos (subcarpeta de procesados o de errores, con un .txt con el motivo al lado de los que fallaron) y mueve el correo: a la carpeta de procesados si se bajó y cargó todo,
/// a la de errores si algo no se pudo. Una falla pasajera (sin red, Microsoft 365 saturado, la base caída) no mueve nada: el correo se reintenta en la siguiente corrida. Los XML repetidos no son un error.</summary>
public class XmlRecibidosService : IXmlRecibidosService
{
    private const string Programa = "KonSolutionsServicios";
    private const string Clase = nameof(XmlRecibidosService);
    private const string Usuario = "SERVICIO XML";
    // una sola corrida a la vez en todo el servicio (la programada y la manual no se pisan)
    private static readonly SemaphoreSlim _una = new(1, 1);

    private readonly IGraphCorreo _graph;
    private readonly IXmlRecibidosRepository _repo;
    private readonly IPathFilesRepository _paths;
    private readonly ErrorLogService _log;
    private readonly XmlRecibidosOpciones _o;
    private readonly XmlRecibidosEstado _estado;
    private readonly ISunatValidador? _sunat;
    private CredencialSol? _cred; private bool _credLeida;

    public XmlRecibidosService(IGraphCorreo graph, IXmlRecibidosRepository repo, IPathFilesRepository paths, ErrorLogService log, IOptions<XmlRecibidosOpciones> o, XmlRecibidosEstado estado, ISunatValidador? sunat = null)
    { _graph = graph; _repo = repo; _paths = paths; _log = log; _o = o.Value; _estado = estado; _sunat = sunat; }

    public async Task<XmlRecibidosEstadoDto> EstadoAsync()
    {
        var (enCurso, ultima, ultimo) = _estado.Leer();
        ConfiguracionGeneral? cfg = null;
        try { cfg = await _repo.ConfigAsync(); } catch (Exception ex) { _log.LogError(ex, Programa, Clase, nameof(EstadoAsync)); }
        return new XmlRecibidosEstadoDto
        {
            CuentaConfigurada = _graph.Configurado, Buzon = _graph.Buzon, Activo = cfg?.FLG_XML_RECIBIDOS_ACTIVO == true, Minutos = cfg?.CAN_MINUTOS_XML_RECIBIDOS ?? 15,
            EnCurso = enCurso, UltimaCorrida = ultima, Ultimo = ultimo
        };
    }

    private static bool NombreSimple(string n) => n.Length > 0 && !n.Contains("..") && n.IndexOfAny(new[] { '/', '\\', ':' }) < 0;

    public async Task<XmlRecibidosCiclo> EjecutarAsync(bool ignorarActivo, CancellationToken ct)
    {
        var ciclo = new XmlRecibidosCiclo();
        if (!await _una.WaitAsync(0, ct)) { ciclo.Mensaje = "Ya hay una corrida en curso: espera a que termine."; ciclo.Omitida = true; ciclo.Fin = DateTime.Now; return ciclo; }
        _estado.Inicia();
        try
        {
            await CorreAsync(ciclo, ignorarActivo, ct);
            await ValidarPendientesSunatAsync(ciclo, ct);
            await ValidarGuiaAsync(null);
        }
        catch (OperationCanceledException) { ciclo.Mensaje ??= "La corrida se canceló."; }
        catch (GraphException gx) { ciclo.Mensaje = gx.Message; _log.LogError(gx, Programa, Clase, nameof(EjecutarAsync)); }
        catch (Exception ex) { ciclo.Mensaje = "La corrida se cortó: " + ex.Message; _log.LogError(ex, Programa, Clase, nameof(EjecutarAsync)); }
        finally
        {
            ciclo.Fin = DateTime.Now;
            _estado.Termina(ciclo);
            _una.Release();
        }
        return ciclo;
    }

    /// <summary>Valida un comprobante con SUNAT y deja el resultado en el comprobante. Una falla (sin red, SUNAT caído, sin Clave SOL) no impide la carga: queda como ERR y se reintenta en las siguientes corridas.
    /// Solo comprobantes (factura, boleta, notas): las guías de remisión no se validan con este servicio de SUNAT.</summary>
    private async Task ValidarSunatAsync(decimal numCpe, XmlRecibidosCiclo ciclo, CancellationToken ct)
    {
        if (_sunat is null) return;
        try
        {
            if (!_credLeida) { _cred = await _repo.CredencialSolAsync(); _credLeida = true; }
            if (_cred is null)
            {
                await _repo.SunatRegistrarAsync(numCpe, "ERR", "No se pudo validar con SUNAT: falta el usuario o la clave SOL en Configuración general.");
                ciclo.SunatSinConsultar++; return;
            }
            var c = await _repo.SunatConsultaAsync(numCpe);
            if (c is null) return;
            var r = await _sunat.ValidarAsync(c, _cred, ct);
            await _repo.SunatRegistrarAsync(numCpe, r.Codigo, r.Mensaje);
            if (r.Consultado) ciclo.SunatValidados++; else ciclo.SunatSinConsultar++;
            _log.LogDebug("SUNAT: el comprobante {Num} -> {Codigo} {Mensaje}", numCpe, r.Codigo, r.Mensaje);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { ciclo.SunatSinConsultar++; _log.LogError(ex, Programa, Clase, nameof(ValidarSunatAsync)); }
    }

    /// <summary>Las guías no pasan por SUNAT (no hay consulta de guías de terceros): se revisan con el procedimiento propio (RUC, serie, correlativo, fecha, firma y líneas). Una falla no impide la carga.</summary>
    private async Task ValidarGuiaAsync(decimal? numGre)
    {
        try { await _repo.ValidarGuiaAsync(numGre); }
        catch (Exception ex) { _log.LogError(ex, Programa, Clase, nameof(ValidarGuiaAsync)); }
    }

    /// <summary>Al final de cada corrida: los comprobantes que todavía no tienen resultado de SUNAT (o cuya consulta falló), hasta 20 por corrida.</summary>
    private async Task ValidarPendientesSunatAsync(XmlRecibidosCiclo ciclo, CancellationToken ct)
    {
        if (_sunat is null) return;
        try { foreach (var num in await _repo.SunatPendientesAsync(20)) { ct.ThrowIfCancellationRequested(); await ValidarSunatAsync(num, ciclo, ct); } }
        catch (OperationCanceledException) { throw; }
        catch (Exception ex) { _log.LogError(ex, Programa, Clase, nameof(ValidarPendientesSunatAsync)); }
    }

    private async Task CorreAsync(XmlRecibidosCiclo ciclo, bool ignorarActivo, CancellationToken ct)
    {
        _credLeida = false;
        var cfg = await _repo.ConfigAsync();
        if (cfg is null) { ciclo.Mensaje = "No hay configuración general."; return; }
        if (!ignorarActivo && cfg.FLG_XML_RECIBIDOS_ACTIVO != true) { ciclo.Omitida = true; return; }

        // la configuración completa
        string? Falta(string? v, string nombre) => string.IsNullOrWhiteSpace(v) ? nombre : null;
        ciclo.ConCorreo = _graph.Configurado;
        var faltan = new List<string?> { Falta(cfg.COD_KEY_PATHFILE_COMPRAS_XML, "la ruta de archivos de Compras") };
        string recepcionTxt = "", procesadosTxt = "", erroresTxt = "";
        if (_graph.Configurado)
        {
            faltan.AddRange(new[] { Falta(cfg.DES_CARPETA_CORREO_RECEPCION, "la carpeta de Outlook de recepción"), Falta(cfg.DES_CARPETA_CORREO_PROCESADOS, "la carpeta de Outlook de procesados"), Falta(cfg.DES_CARPETA_CORREO_ERRORES, "la carpeta de Outlook de errores") });
        }
        var faltantes = faltan.Where(x => x is not null).ToList();
        if (faltantes.Count > 0) { ciclo.Mensaje = "Falta en la configuración general (pestaña Compras): " + string.Join(", ", faltantes) + "."; return; }
        if (_graph.Configurado)
        {
            recepcionTxt = cfg.DES_CARPETA_CORREO_RECEPCION!.Trim(); procesadosTxt = cfg.DES_CARPETA_CORREO_PROCESADOS!.Trim(); erroresTxt = cfg.DES_CARPETA_CORREO_ERRORES!.Trim();
            if (new[] { recepcionTxt, procesadosTxt, erroresTxt }.Distinct(StringComparer.OrdinalIgnoreCase).Count() != 3) { ciclo.Mensaje = "Las tres carpetas de Outlook tienen que ser distintas."; return; }
        }

        var clave = cfg.COD_KEY_PATHFILE_COMPRAS_XML!.Trim();
        var ruta = await _paths.ShowbyidAsync(clave);
        if (ruta is null || string.IsNullOrWhiteSpace(ruta.DES_PATH_FILE)) { ciclo.Mensaje = $"La ruta «{clave}» no existe en Rutas de archivos o no tiene carpeta."; return; }
        var raiz = ruta.DES_PATH_FILE!.Trim();
        if (!Directory.Exists(raiz)) { ciclo.Mensaje = $"La carpeta «{raiz}» no existe o el servicio no puede verla."; return; }
        var subProc = string.IsNullOrWhiteSpace(cfg.DES_CARPETA_COMPRAS_PROCESADOS) ? "procesados" : cfg.DES_CARPETA_COMPRAS_PROCESADOS!.Trim();
        var subErr = string.IsNullOrWhiteSpace(cfg.DES_CARPETA_COMPRAS_ERRORES) ? "errores" : cfg.DES_CARPETA_COMPRAS_ERRORES!.Trim();
        if (!NombreSimple(subProc) || !NombreSimple(subErr) || string.Equals(subProc, subErr, StringComparison.OrdinalIgnoreCase)) { ciclo.Mensaje = "Las subcarpetas de procesados y de errores tienen que ser nombres de carpeta distintos, sin rutas."; return; }
        var dirProc = Path.Combine(raiz, subProc); var dirErr = Path.Combine(raiz, subErr);
        _log.LogDebug("PASO 0: la carpeta de archivos es {Raiz}; los que entren van a {Proc} y los que no, a {Err}.", raiz, dirProc, dirErr);

        Directory.CreateDirectory(dirProc); Directory.CreateDirectory(dirErr);
        // si el correo no se puede leer (sin cuenta, Microsoft 365 caido, carpeta de Outlook que no existe) el motivo queda en ciclo.Mensaje y se SIGUE con los archivos de la carpeta
        if (!_graph.Configurado) _log.LogDebug("PASO 1 y 2 (correo): se omiten, no hay cuenta de Microsoft 365. Se sigue con la carpeta.");
        if (_graph.Configurado)
        {
            try { await CorreoAsync(ciclo, recepcionTxt, procesadosTxt, erroresTxt, dirProc, dirErr, ct); }
            catch (GraphException gx) { ciclo.Mensaje = "Correo: " + gx.Message; _log.LogError(gx, Programa, Clase, nameof(CorreoAsync)); }
        }
        await CarpetaAsync(ciclo, raiz, dirProc, dirErr, ct);
    }

    /// <summary>Paso 1 y 2: baja del buzón los correos de la carpeta de recepción y procesa sus archivos. Cada correo va a procesados o a errores.</summary>
    private async Task CorreoAsync(XmlRecibidosCiclo ciclo, string recepcionTxt, string procesadosTxt, string erroresTxt, string dirProc, string dirErr, CancellationToken ct)
    {
        // las tres carpetas del buzón: si falta una no se toca nada
        _log.LogDebug("PASO 1: busco en el buzon {Buzon} las carpetas de Outlook: recepcion «{R}», procesados «{P}», errores «{E}».", _graph.Buzon, recepcionTxt, procesadosTxt, erroresTxt);
        var idRecepcion = await _graph.ResolverCarpetaAsync(recepcionTxt, ct);
        var idProcesados = await _graph.ResolverCarpetaAsync(procesadosTxt, ct);
        var idErrores = await _graph.ResolverCarpetaAsync(erroresTxt, ct);
        var noExisten = new[] { (idRecepcion, recepcionTxt), (idProcesados, procesadosTxt), (idErrores, erroresTxt) }.Where(x => x.Item1 is null).Select(x => $"«{x.Item2}»").ToList();
        if (noExisten.Count > 0) { ciclo.Mensaje = $"No existe en el buzón {_graph.Buzon} la carpeta de Outlook " + string.Join(", ", noExisten) + "."; return; }

        var correos = await _graph.ListarCorreosAsync(idRecepcion!, Math.Max(1, _o.MaxCorreosPorCiclo), ct);
        ciclo.Correos = correos.Count;
        _log.LogDebug("PASO 1: {N} correo(s) en la carpeta de recepcion (maximo {Max} por corrida).", correos.Count, Math.Max(1, _o.MaxCorreosPorCiclo));
        var transitoriosSeguidos = 0;
        foreach (var correo in correos)
        {
            ct.ThrowIfCancellationRequested();
            var res = new XmlRecibidosCorreoResultado { Id = correo.Id, Asunto = correo.Asunto, De = correo.De };
            ciclo.Detalle.Add(res);
            _log.LogDebug("--- Correo «{Asunto}» de {De}: descargo sus adjuntos y los proceso.", correo.Asunto, correo.De);
            var cortar = false;
            try
            {
                var ok = await ProcesaCorreoAsync(correo, res, ciclo, dirProc, dirErr, ct);
                transitoriosSeguidos = 0;
                var destino = ok ? idProcesados! : idErrores!;
                res.Destino = ok ? "PROCESADOS" : "ERRORES";
                _log.LogDebug("Correo «{Asunto}»: {Resultado} -> lo muevo a la carpeta de Outlook {Destino}.{Problemas}", correo.Asunto, ok ? "todo bien" : "hubo problemas", ok ? "PROCESADOS" : "ERRORES", res.Problemas.Count > 0 ? " Problemas: " + string.Join(" | ", res.Problemas) : "");
                try { await _graph.MoverAsync(correo.Id, destino, ct); }
                catch (GraphException gx) when (!gx.Fatal)
                {
                    // el correo ya se cargo pero no se pudo mover: queda en recepcion y la siguiente corrida lo reconoce (los XML repetidos no son un error)
                    res.Destino = "SIN_MOVER"; res.Problemas.Add("No se pudo mover el correo a su carpeta (se reintenta en la siguiente corrida): " + gx.Message);
                    _log.LogError(gx, Programa, Clase, "MoverCorreo");
                    if (gx.Transitorio && ++transitoriosSeguidos >= 3) { ciclo.Mensaje = "Microsoft 365 falló tres veces seguidas; se corta la corrida y se reintenta en la siguiente. Último error: " + gx.Message; cortar = true; }
                }
            }
            catch (GraphException gx) when (gx.Transitorio && !gx.Fatal)
            {
                // una falla pasajera: el correo queda donde esta y se vuelve a intentar; tres seguidas = Microsoft 365 no esta bien, se corta la corrida
                res.Destino = "SIN_MOVER"; res.Problemas.Add("Falla pasajera con Microsoft 365 (se reintenta en la siguiente corrida): " + gx.Message);
                if (++transitoriosSeguidos >= 3) { ciclo.Mensaje = "Microsoft 365 falló tres veces seguidas; se corta la corrida y se reintenta en la siguiente. Último error: " + gx.Message; cortar = true; }
            }
            if (res.Destino == "PROCESADOS") ciclo.CorreosProcesados++;
            else if (res.Destino == "ERRORES") ciclo.CorreosConError++;
            else ciclo.CorreosSinMover++;
            if (cortar) break;
        }
    }

    /// <summary>Paso 3: procesa los archivos sueltos de la carpeta de Compras (los que alguien dejó ahí a mano, o que quedaron de antes). Cada XML se carga junto con su PDF; un ZIP se abre y se procesa su contenido.
    /// Lo que entra se mueve a la subcarpeta de procesados y lo que no, a la de errores (con un .txt al lado con el motivo). Un PDF sin su XML se deja donde está (puede ser que el XML todavía no llegue).
    /// No depende de Microsoft 365: sin cuenta, este es el único paso.</summary>
    private async Task CarpetaAsync(XmlRecibidosCiclo ciclo, string raiz, string dirProc, string dirErr, CancellationToken ct)
    {
        var ahora = DateTime.Now;
        // no se tocan los archivos que se estan copiando en este momento (modificados hace menos de 15 segundos)
        var sueltos = Directory.GetFiles(raiz).Where(f => new[] { ".xml", ".pdf", ".zip" }.Contains(Ext(f)) && (ahora - File.GetLastWriteTime(f)).TotalSeconds >= 15).OrderBy(f => File.GetLastWriteTime(f)).ToList();
        _log.LogDebug("PASO 3: reviso la carpeta {Raiz}: {N} archivo(s) suelto(s) listo(s) (xml/pdf/zip, sin contar los que se estan copiando).", raiz, sueltos.Count);
        if (sueltos.Count == 0) { _log.LogDebug("PASO 3: no hay nada que procesar en la carpeta."); return; }
        var xmls = sueltos.Where(f => Ext(f) == ".xml").ToList(); var pdfs = sueltos.Where(f => Ext(f) == ".pdf").ToList(); var zips = sueltos.Where(f => Ext(f) == ".zip").ToList();
        var pdfsUsados = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var lotes = new List<(string Nombre, List<string> Originales)>();
        foreach (var x in xmls)
        {
            var l = new List<string> { x };
            foreach (var p in pdfs.Where(p => BaseN(p) == BaseN(x) && !pdfsUsados.Contains(p))) { l.Add(p); pdfsUsados.Add(p); }
            lotes.Add((Path.GetFileName(x), l));
        }
        foreach (var z in zips) lotes.Add((Path.GetFileName(z), new List<string> { z }));
        foreach (var p in pdfs.Where(p => !pdfsUsados.Contains(p))) _log.LogInformation("XmlRecibidos: el PDF «{Pdf}» no tiene su XML en la carpeta; se deja donde está.", Path.GetFileName(p));

        var max = Math.Max(1, _o.MaxCorreosPorCiclo);
        _log.LogDebug("PASO 3: {L} lote(s) (cada XML con su PDF, o cada ZIP); proceso hasta {Max} en esta corrida.", lotes.Count, max);
        foreach (var (nombre, originales) in lotes.Take(max))
        {
            ct.ThrowIfCancellationRequested();
            var res = new XmlRecibidosCorreoResultado { Id = originales[0], Asunto = nombre, De = "(carpeta)" };
            _log.LogDebug("--- Lote «{Nombre}»: {Archivos}", nombre, string.Join(", ", originales.Select(Path.GetFileName)));
            var maxBytes = (long)_o.MaxAdjuntoMB * 1024 * 1024;
            var archivos = new List<ArchivoRecibido>(); var zipOrigen = default(ArchivoRecibido);
            try
            {
                foreach (var f in originales)
                {
                    var n = XmlTexto.NombreSeguro(Path.GetFileName(f));
                    var len = new FileInfo(f).Length;
                    if (len > maxBytes) { res.Problemas.Add($"«{n}» pesa {Mb(len)} MB y el máximo es {_o.MaxAdjuntoMB} MB."); archivos.Add(new ArchivoRecibido(n, Array.Empty<byte>(), "carpeta")); continue; }
                    var bytes = File.ReadAllBytes(f);   // un archivo bloqueado por otro programa lanza IOException: el lote se salta y se reintenta
                    if (Ext(n) == ".zip")
                    {
                        zipOrigen = new ArchivoRecibido(n, bytes, "carpeta");
                        try { archivos.AddRange(ZipSeguro.Extraer(bytes, n, (long)_o.MaxZipDescomprimidoMB * 1024 * 1024, _o.MaxArchivosPorZip)); }
                        catch (ZipInvalidoException zx) { res.Problemas.Add(zx.Message); }
                    }
                    else archivos.Add(new ArchivoRecibido(n, bytes, "carpeta"));
                }
            }
            catch (IOException) { _log.LogInformation("XmlRecibidos: «{Archivo}» está en uso por otro programa; se reintenta en la siguiente corrida.", nombre); continue; }
            catch (UnauthorizedAccessException ux) { res.Problemas.Add("No se pudo leer: " + ux.Message); }

            ciclo.CarpetaLotes++; ciclo.Detalle.Add(res);
            var ok = res.Problemas.Count == 0 && await ProcesaArchivosAsync(archivos.Where(a => a.Contenido.Length > 0).ToList(), res, ciclo, dirProc, dirErr, "El archivo", ct);
            // los originales: si el lote entro, ya estan en procesados; si fallo, van a errores con su motivo. Se quitan de la carpeta para no reprocesarlos.
            if (zipOrigen is not null) Guarda(ok ? dirProc : dirErr, zipOrigen, ok ? null : string.Join(" | ", res.Problemas));
            foreach (var f in originales)
            {
                try
                {
                    if (!ok && Ext(f) != ".zip" && !File.Exists(Path.Combine(dirErr, Path.GetFileName(f)))) Guarda(dirErr, new ArchivoRecibido(Path.GetFileName(f), File.ReadAllBytes(f), "carpeta"), string.Join(" | ", res.Problemas));
                    File.Delete(f);
                }
                catch (Exception ex) { _log.LogWarning("XmlRecibidos: no se pudo quitar «{Archivo}» de la carpeta: {Motivo}", Path.GetFileName(f), ex.Message); }
            }
            res.Destino = ok ? "PROCESADOS" : "ERRORES";
            _log.LogDebug("Lote «{Nombre}»: {Resultado} -> pasa a {Dir} y sale de la carpeta.{Problemas}", nombre, ok ? "todo bien" : "hubo problemas", ok ? dirProc : dirErr, res.Problemas.Count > 0 ? " Problemas: " + string.Join(" | ", res.Problemas) : "");
            if (ok) ciclo.CarpetaProcesados++; else ciclo.CarpetaConError++;
        }
    }

    private static string Mb(long bytes) => (bytes / 1048576.0).ToString("0.#", System.Globalization.CultureInfo.InvariantCulture);
    private static string Ext(string nombre) => Path.GetExtension(nombre).ToLowerInvariant();
    // el nombre sin extension, en minusculas y sin ceros a la izquierda en cada tramo (20100041953-01-F581-07567512 = 20100041953-1-F581-7567512): el XML y el PDF de un mismo comprobante no siempre los escriben igual
    private static string BaseN(string nombre) => string.Join("-", Path.GetFileNameWithoutExtension(nombre).ToLowerInvariant().Split('-').Select(s => s.TrimStart('0') is { Length: > 0 } x ? x : (s.Length > 0 ? "0" : "")));

    /// <summary>Procesa un correo. Devuelve true si todo se bajó y cargó (va a procesados) y false si algo no se pudo (va a errores). Una falla pasajera sale como GraphException transitoria o como otra excepción: no se mueve nada.</summary>
    private async Task<bool> ProcesaCorreoAsync(CorreoResumen correo, XmlRecibidosCorreoResultado res, XmlRecibidosCiclo ciclo, string dirProc, string dirErr, CancellationToken ct)
    {
        var adjuntos = await _graph.ListarAdjuntosAsync(correo.Id, ct);
        _log.LogDebug("PASO 1: el correo trae {N} adjunto(s): {Lista}", adjuntos.Count, string.Join(", ", adjuntos.Select(a => $"{a.Nombre} ({Mb(a.Tamano)} MB)")));
        var candidatos = adjuntos.Where(a => a.EsArchivo && !a.EsInline && (new[] { ".xml", ".pdf", ".zip" }.Contains(Ext(a.Nombre)))).ToList();
        if (candidatos.Count == 0)
        {
            res.Problemas.Add("El correo no trae ningún XML, PDF ni ZIP adjunto.");
            return string.Equals(_o.SinAdjuntosVaA, "Procesados", StringComparison.OrdinalIgnoreCase);
        }

        var archivos = new List<ArchivoRecibido>();
        var maxBytes = (long)_o.MaxAdjuntoMB * 1024 * 1024;
        foreach (var a in candidatos)
        {
            ct.ThrowIfCancellationRequested();
            var nombre = XmlTexto.NombreSeguro(a.Nombre);
            if (a.Tamano > maxBytes) { res.Problemas.Add($"«{nombre}» pesa {Mb(a.Tamano)} MB y el máximo es {_o.MaxAdjuntoMB} MB."); continue; }
            _log.LogDebug("PASO 1: descargo «{Nombre}»...", nombre);
            byte[] bytes;
            try { bytes = await _graph.DescargarAdjuntoAsync(correo.Id, a.Id, ct); }
            catch (GraphException gx) when (gx.Estado == HttpStatusCode.NotFound) { res.Problemas.Add($"«{nombre}» ya no está en el correo."); continue; }
            if (bytes.Length > maxBytes) { res.Problemas.Add($"«{nombre}» pesa {Mb(bytes.Length)} MB y el máximo es {_o.MaxAdjuntoMB} MB."); continue; }
            if (Ext(nombre) == ".zip")
            {
                try { archivos.AddRange(ZipSeguro.Extraer(bytes, nombre, (long)_o.MaxZipDescomprimidoMB * 1024 * 1024, _o.MaxArchivosPorZip)); }
                catch (ZipInvalidoException zx) { res.Problemas.Add(zx.Message); }
            }
            else archivos.Add(new ArchivoRecibido(nombre, bytes, null));
        }

        return await ProcesaArchivosAsync(archivos, res, ciclo, dirProc, dirErr, "El correo", ct);
    }

    /// <summary>Procesa un lote de archivos (los de un correo, o un XML con su PDF de la carpeta): carga cada XML y guarda todo en procesados o en errores. Devuelve true si todo entró.
    /// Un fallo de la base (no de una regla del XML) sube como excepción: nada se mueve y se reintenta.</summary>
    private async Task<bool> ProcesaArchivosAsync(List<ArchivoRecibido> archivos, XmlRecibidosCorreoResultado res, XmlRecibidosCiclo ciclo, string dirProc, string dirErr, string origen, CancellationToken ct)
    {
        _log.LogDebug("PASO 2: {N} archivo(s) para procesar ({X} XML, {P} PDF).", archivos.Count, archivos.Count(f => Ext(f.Nombre) == ".xml"), archivos.Count(f => Ext(f.Nombre) == ".pdf"));
        // los XML: cada uno se carga; el archivo va a procesados si entró y a errores (con un .txt con el motivo) si no
        var cargados = new List<(string Base, string Clase, decimal Num)>();
        var cdrs = new List<ArchivoRecibido>();
        foreach (var x in archivos.Where(f => Ext(f.Nombre) == ".xml"))
        {
            ct.ThrowIfCancellationRequested();
            res.Archivos.Add(x.Nombre);
            _log.LogDebug("PASO 2: leo «{Nombre}» ({Kb} KB) y lo cargo en SQL con PROC_XML_RECIBIDOS_CARGAR...", x.Nombre, x.Contenido.Length / 1024);
            try
            {
                var texto = XmlTexto.Decodificar(x.Contenido);
                // la constancia de recepción (CDR) de SUNAT que muchos proveedores mandan junto a la factura: no es un comprobante ni un error; se guarda y ya
                if (XmlTexto.Raiz(texto) == "ApplicationResponse") { cdrs.Add(x); res.Avisos.Add($"«{x.Nombre}» es una constancia de recepción de SUNAT (CDR): se guarda pero no se carga."); continue; }
                var (clase, num, nuevo) = await _repo.CargarAsync(texto, x.Nombre, Usuario, Environment.MachineName);
                if (nuevo) ciclo.XmlCargados++; else ciclo.XmlRepetidos++;
                if (nuevo && clase == "CPE") await ValidarSunatAsync(num, ciclo, ct);
                if (nuevo && clase == "GRE") await ValidarGuiaAsync(num);
                res.Documentos.Add($"{clase} {num}{(nuevo ? "" : " (ya estaba)")}");
                _log.LogDebug("PASO 2: «{Nombre}» -> documento {Clase} {Num} ({Estado}). Lo guardo en {Dir}.", x.Nombre, clase, num, nuevo ? "nuevo" : "ya estaba cargado", dirProc);
                cargados.Add((BaseN(x.Nombre), clase, num));
                Guarda(dirProc, x);
            }
            catch (SqlException sx) when (sx.Number is >= 50700 and <= 50734)
            {
                res.Problemas.Add($"«{x.Nombre}»: {sx.Message}");
                _log.LogDebug("PASO 2: «{Nombre}» RECHAZADO por SQL ({Num}): {Motivo}. Lo guardo en {Dir} con un .txt del motivo.", x.Nombre, sx.Number, sx.Message, dirErr);
                Guarda(dirErr, x, sx.Message);
            }
            // cualquier otra falla (la base caída, sin conexión) sube: el correo no se mueve y se reintenta
        }

        // un correo que no dejó ningún comprobante ni guía (sin XML, solo PDF, o solo constancias de recepción) no se puede procesar: va a errores con sus archivos
        if (cargados.Count == 0 && res.Problemas.Count == 0)
            res.Problemas.Add(cdrs.Count > 0 ? $"{origen} solo trae constancias de recepción (CDR): ningún comprobante ni guía." : $"{origen} no trae ningún XML (ni suelto ni dentro de un ZIP).");

        // los PDF: el que acompaña a un XML cargado va junto a los procesados y se anota en el documento; el que no acompaña a nadie sigue la suerte del correo
        var pdfs = archivos.Where(f => Ext(f.Nombre) == ".pdf").ToList();
        var correoOk = res.Problemas.Count == 0;
        var distintos = cargados.Select(c => (c.Clase, c.Num)).Distinct().ToList();
        var usados = new HashSet<ArchivoRecibido>();
        foreach (var doc in distintos)
        {
            var delDoc = cargados.Where(c => c.Clase == doc.Clase && c.Num == doc.Num).Select(c => c.Base).ToList();
            var pdf = pdfs.FirstOrDefault(p => !usados.Contains(p) && delDoc.Contains(BaseN(p.Nombre)));
            if (pdf is null && distintos.Count == 1 && pdfs.Count == 1 && !usados.Contains(pdfs[0])) pdf = pdfs[0];
            if (pdf is null) continue;
            usados.Add(pdf);
            var nombreFinal = Guarda(dirProc, pdf);
            await _repo.AsignarPdfAsync(doc.Clase, doc.Num, Path.GetFileName(dirProc) + "/" + nombreFinal);
            _log.LogDebug("PASO 2: el PDF «{Pdf}» acompana al documento {Clase} {Num}: lo guardo en procesados y lo anoto en el documento.", pdf.Nombre, doc.Clase, doc.Num);
            ciclo.PdfGuardados++;
            res.Archivos.Add(pdf.Nombre);
        }
        foreach (var cdr in cdrs) Guarda(correoOk ? dirProc : dirErr, cdr);
        foreach (var p in pdfs.Where(p => !usados.Contains(p)))
        {
            Guarda(correoOk ? dirProc : dirErr, p);
            ciclo.PdfGuardados++;
            res.Archivos.Add(p.Nombre);
        }
        return correoOk;
    }

    /// <summary>Guarda el archivo en la carpeta. Si ya hay uno con ese nombre y es el mismo contenido, no lo repite; si es otro, le agrega (2), (3)... Devuelve el nombre con el que quedó. Con un motivo, deja al lado un .txt.</summary>
    private static string Guarda(string dir, ArchivoRecibido f, string? motivo = null)
    {
        Directory.CreateDirectory(dir);
        var baseN = Path.GetFileNameWithoutExtension(f.Nombre); var ext = Path.GetExtension(f.Nombre);
        var nombre = f.Nombre; var n = 1;
        while (true)
        {
            var destino = Path.Combine(dir, nombre);
            if (!File.Exists(destino)) { File.WriteAllBytes(destino, f.Contenido); break; }
            if (new FileInfo(destino).Length == f.Contenido.Length && SHA256.HashData(File.ReadAllBytes(destino)).AsSpan().SequenceEqual(SHA256.HashData(f.Contenido))) break;
            nombre = $"{baseN} ({++n}){ext}";
        }
        if (motivo is not null) File.WriteAllText(Path.Combine(dir, nombre + ".txt"), motivo + Environment.NewLine + "Origen: " + (f.Origen ?? "adjunto del correo") + Environment.NewLine + DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss"));
        return nombre;
    }
}
