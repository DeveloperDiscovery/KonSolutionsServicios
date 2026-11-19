namespace KonSolutionsServicios;

/// <summary>Una tarea que el servicio de Windows corre una y otra vez. Cada tarea vive en su propio ciclo: si una falla o tarda, las demas siguen. Para agregar una nueva:
/// 1) implementa esta interfaz, 2) registrala en Program.cs con builder.Services.AddSingleton&lt;ITareaProgramada, MiTarea&gt;(), 3) (opcional) apagala con Tareas:MiTarea:Habilitada=false en appsettings.json.</summary>
public interface ITareaProgramada
{
    /// <summary>Nombre unico; es la clave en appsettings.json (seccion Tareas) y el que sale en los logs.</summary>
    string Nombre { get; }
    /// <summary>Cuanto espera tras arrancar el servicio antes de la primera corrida.</summary>
    TimeSpan EsperaInicial { get; }
    /// <summary>Una vuelta de la tarea. Recibe un scope nuevo en cada vuelta. Devuelve cuanto esperar hasta la siguiente. Si lanza una excepcion se anota y se reintenta tras ReposoTrasFalla.</summary>
    Task<TimeSpan> EjecutarAsync(IServiceProvider sp, ILogger log, CancellationToken ct);
    TimeSpan ReposoTrasFalla { get; }
}

/// <summary>Aloja todas las ITareaProgramada registradas, cada una en su propio ciclo. Una falla nunca detiene el servicio ni a las demas tareas.</summary>
public class TareasWorker : BackgroundService
{
    private readonly IServiceProvider _sp;
    private readonly IEnumerable<ITareaProgramada> _tareas;
    private readonly IConfiguration _cfg;
    private readonly ILogger<TareasWorker> _log;
    public TareasWorker(IServiceProvider sp, IEnumerable<ITareaProgramada> tareas, IConfiguration cfg, ILogger<TareasWorker> log) { _sp = sp; _tareas = tareas; _cfg = cfg; _log = log; }

    protected override async Task ExecuteAsync(CancellationToken ct)
    {
        var activas = _tareas.Where(t => _cfg.GetValue($"Tareas:{t.Nombre}:Habilitada", true)).ToList();
        foreach (var t in _tareas.Except(activas)) _log.LogInformation("Tarea {Tarea} deshabilitada en appsettings.json (Tareas:{Tarea}:Habilitada).", t.Nombre, t.Nombre);
        _log.LogInformation("KonSolutionsServicios iniciado con {N} tarea(s): {Tareas}", activas.Count, string.Join(", ", activas.Select(t => t.Nombre)));
        await Task.WhenAll(activas.Select(t => Ciclo(t, ct)));
    }

    private async Task Ciclo(ITareaProgramada t, CancellationToken ct)
    {
        var log = (ILogger)_sp.GetRequiredService<ILoggerFactory>().CreateLogger(t.Nombre);
        try { await Task.Delay(t.EsperaInicial, ct); } catch (OperationCanceledException) { return; }
        while (!ct.IsCancellationRequested)
        {
            TimeSpan espera;
            try
            {
                using var scope = _sp.CreateScope();
                espera = await t.EjecutarAsync(scope.ServiceProvider, log, ct);
            }
            catch (OperationCanceledException) { break; }
            catch (Exception ex) { log.LogWarning(ex, "{Tarea}: la corrida programada fallo", t.Nombre); espera = t.ReposoTrasFalla; }
            try { await Task.Delay(espera, ct); } catch (OperationCanceledException) { break; }
        }
    }
}
