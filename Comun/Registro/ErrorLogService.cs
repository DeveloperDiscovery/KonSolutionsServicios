using System.Text;

namespace KonSolutionsServicios;

/// <summary>Anota los errores del servicio en el Registro de eventos de Windows (via ILogger) y en un archivo por dia: errors_AAAA-MM-DD.log, en la carpeta Servicio:CarpetaLogs de appsettings.json
/// (por omision, la carpeta «logs» al lado del .exe). Nunca lanza: un fallo al escribir el log no debe tumbar una corrida.</summary>
public class ErrorLogService
{
    private static readonly object _l = new();
    private readonly ILogger<ErrorLogService> _logger;
    private readonly string _dir;
    public ErrorLogService(IConfiguration config, ILogger<ErrorLogService> logger)
    {
        _logger = logger;
        var d = config["Servicio:CarpetaLogs"];
        _dir = string.IsNullOrWhiteSpace(d) ? Path.Combine(AppContext.BaseDirectory, "logs") : d.Trim();
    }
    public string Directorio => _dir;

    /// <summary>Informacion y avisos normales (no son errores): van al registro del servicio (logs\servicio_AAAA-MM-DD.log) igual que lo demas.</summary>
    public void LogInformation(string mensaje, params object?[] args) { try { _logger.LogInformation(mensaje, args); } catch { } }
    /// <summary>El paso a paso (solo se ve con el nivel Debug: appsettings.Development.json, que usa el perfil de Visual Studio, o Logging:LogLevel:Default = Debug).</summary>
    public void LogDebug(string mensaje, params object?[] args) { try { _logger.LogDebug(mensaje, args); } catch { } }
    public void LogWarning(string mensaje, params object?[] args) { try { _logger.LogWarning(mensaje, args); } catch { } }

    public void LogError(Exception ex, string programa, string clase, string metodo, string? parametros = null)
    {
        try { _logger.LogError(ex, "{Clase}.{Metodo}", clase, metodo); } catch { }
        try
        {
            Directory.CreateDirectory(_dir);
            var sb = new StringBuilder();
            sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append(" | ").Append(programa).Append(" | ").Append(clase).Append('.').Append(metodo).AppendLine();
            if (!string.IsNullOrEmpty(parametros)) sb.Append("  parametros: ").AppendLine(parametros);
            sb.AppendLine(ex.ToString()).AppendLine(new string('-', 80));
            lock (_l) File.AppendAllText(Path.Combine(_dir, $"errors_{DateTime.Now:yyyy-MM-dd}.log"), sb.ToString(), Encoding.UTF8);
        }
        catch { }
    }
}
