using System.Text;

namespace KonSolutionsServicios;

/// <summary>Escribe TODO lo que el servicio anota (informacion, avisos y errores) en un archivo por dia: servicio_AAAA-MM-DD.log, en la carpeta Servicio:CarpetaLogs de appsettings.json
/// (por omision, «logs» al lado del .exe). Asi se ve que hace el servicio aunque el Registro de eventos de Windows solo muestre avisos y errores. Nunca lanza.</summary>
public sealed class FileLoggerProvider : ILoggerProvider
{
    private static readonly object _l = new();
    private readonly string _dir;
    private readonly LogLevel _minimo;
    public FileLoggerProvider(string dir, LogLevel minimo = LogLevel.Information) { _dir = dir; _minimo = minimo < LogLevel.Information ? minimo : LogLevel.Information; }
    public ILogger CreateLogger(string categoryName) => new Logger(_dir, categoryName, _minimo);
    public void Dispose() { }

    private sealed class Logger : ILogger
    {
        private readonly string _dir, _cat; private readonly LogLevel _min;
        public Logger(string dir, string cat, LogLevel min) { _dir = dir; _cat = cat; _min = min; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel nivel) => nivel >= _min;
        public void Log<TState>(LogLevel nivel, EventId id, TState state, Exception? ex, Func<TState, Exception?, string> formato)
        {
            if (!IsEnabled(nivel)) return;
            try
            {
                var corto = _cat.StartsWith("Microsoft.") || _cat.StartsWith("System.") ? null : _cat;   // el ruido de Microsoft.Hosting no
                if (corto is null && nivel < LogLevel.Warning) return;
                if (nivel == LogLevel.Debug && _cat.StartsWith("Microsoft")) return;
                var sb = new StringBuilder();
                sb.Append(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss")).Append(" [").Append(nivel.ToString().ToUpperInvariant()[..4]).Append("] ").Append(_cat.Split('.').Last()).Append(": ").Append(formato(state, ex));
                if (ex is not null) sb.AppendLine().Append(ex);
                sb.AppendLine();
                Directory.CreateDirectory(_dir);
                lock (_l) File.AppendAllText(Path.Combine(_dir, $"servicio_{DateTime.Now:yyyy-MM-dd}.log"), sb.ToString(), Encoding.UTF8);
            }
            catch { }
        }
    }
}
