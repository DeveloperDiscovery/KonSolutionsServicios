using KonSolutionsServicios;

namespace KonSolutions.XmlRecibidos;

/// <summary>Tarea «XmlRecibidos»: corre el servicio de XML recibidos cada tantos minutos (los de la configuracion general; 15 si estan vacios) mientras la carga automatica este activa. Si esta apagada, o falta la
/// cuenta de Microsoft 365, solo se asoma a la configuracion una vez por minuto.</summary>
public class XmlRecibidosTarea : ITareaProgramada
{
    public string Nombre => "XmlRecibidos";
    public TimeSpan EsperaInicial { get; set; } = TimeSpan.FromSeconds(30);
    public TimeSpan UnMinuto { get; set; } = TimeSpan.FromMinutes(1);
    public TimeSpan ReposoTrasFalla => UnMinuto;

    private string? _ultimoEstado;
    // anota solo cuando cambia el estado (para no llenar el log cada minuto mientras espera)
    private void Estado(ILogger log, string estado) { if (estado != _ultimoEstado) { log.LogInformation("XmlRecibidos: {Estado}", estado); _ultimoEstado = estado; } }

    public async Task<TimeSpan> EjecutarAsync(IServiceProvider sp, ILogger log, CancellationToken ct)
    {
        log.LogDebug("==== Nueva revision de XML recibidos ====");
        log.LogDebug("Leyendo la configuracion general de la base de datos...");
        var graph = sp.GetRequiredService<IGraphCorreo>();
        var cfg = await sp.GetRequiredService<IXmlRecibidosRepository>().ConfigAsync();
        log.LogDebug("Configuracion: activa={Activa}, cada {Min} minuto(s), ruta de archivos={Ruta}, cuenta de Microsoft 365 {Cuenta}", cfg?.FLG_XML_RECIBIDOS_ACTIVO, cfg?.CAN_MINUTOS_XML_RECIBIDOS ?? 15, cfg?.COD_KEY_PATHFILE_COMPRAS_XML, graph.Configurado ? "SI (" + graph.Buzon + ")" : "NO");
        if (cfg is null) { Estado(log, "NO CORRE: no hay configuracion general en la base."); return UnMinuto; }
        if (cfg.FLG_XML_RECIBIDOS_ACTIVO != true) { Estado(log, "NO CORRE: la carga automatica esta apagada (Configuracion general > Compras > XML recibidos, casilla de activar)."); return UnMinuto; }
        Estado(log, graph.Configurado
            ? $"ACTIVO: buzon {graph.Buzon}, revisa cada {Math.Clamp(cfg.CAN_MINUTOS_XML_RECIBIDOS ?? 15, 1, 1440)} minuto(s), recepcion «{cfg.DES_CARPETA_CORREO_RECEPCION}», ruta «{cfg.COD_KEY_PATHFILE_COMPRAS_XML}»."
            : $"ACTIVO SIN CORREO: no hay cuenta de Microsoft 365 (revisa TenantId, ClientId y Buzon en appsettings.json y la variable de MAQUINA KonSolutions_GRAPH_SECRET; despues de definirla hay que reiniciar el servicio). Solo se procesan los XML y PDF sueltos en la ruta «{cfg.COD_KEY_PATHFILE_COMPRAS_XML}»; revisa cada {Math.Clamp(cfg.CAN_MINUTOS_XML_RECIBIDOS ?? 15, 1, 1440)} minuto(s).");
        var c = await sp.GetRequiredService<IXmlRecibidosService>().EjecutarAsync(false, ct);
        if (c.Mensaje is not null) log.LogWarning("XmlRecibidos: no se pudo correr: {Mensaje}", c.Mensaje);
        if (c.Correos > 0 || c.CarpetaLotes > 0 || c.XmlCargados > 0)
            log.LogInformation("XmlRecibidos: corrida terminada: correo {Correos} (a procesados {Procesados}, a errores {Errores}, sin mover {SinMover}); carpeta {Lotes} lote(s) (a procesados {CarpetaOk}, a errores {CarpetaMal}); {Xml} XML cargados, {Repetidos} repetidos, {Pdf} PDF guardados.", c.Correos, c.CorreosProcesados, c.CorreosConError, c.CorreosSinMover, c.CarpetaLotes, c.CarpetaProcesados, c.CarpetaConError, c.XmlCargados, c.XmlRepetidos, c.PdfGuardados);
        foreach (var d in c.Detalle.Where(d => d.Problemas.Count > 0)) log.LogWarning("XmlRecibidos: «{Asunto}» ({De}) -> {Destino}: {Problemas}", d.Asunto, d.De, d.Destino, string.Join(" | ", d.Problemas));
        var espera = TimeSpan.FromMinutes(Math.Clamp(cfg.CAN_MINUTOS_XML_RECIBIDOS ?? 15, 1, 1440));
        log.LogDebug("==== Revision terminada. La proxima es en {Min} minuto(s) ====", espera.TotalMinutes);
        return espera;
    }
}
