using System.Data;
using Dapper;
using Microsoft.Data.SqlClient;

namespace KonSolutionsServicios;

public interface IDbConnectionFactory { IDbConnection CreateConnection(); }

/// <summary>La conexion a SQL Server. La cadena sale de la variable de entorno de la MAQUINA KonSolutions_DB_CONNECTION (igual que la API); ConnectionStrings:DefaultConnection de appsettings.json solo es un respaldo para pruebas.
/// Igual que en la API, cada conexion abre con SET ARITHABORT ON.</summary>
public class SqlConnectionFactory : IDbConnectionFactory
{
    private readonly string _cs; private readonly ILogger<SqlConnectionFactory>? _log; private bool _anotada;
    public SqlConnectionFactory(IConfiguration config, ILogger<SqlConnectionFactory>? log = null)
    {
        _log = log;
        var cs = Environment.GetEnvironmentVariable("KonSolutions_DB_CONNECTION");
        if (string.IsNullOrWhiteSpace(cs)) cs = config.GetConnectionString("DefaultConnection");
        if (string.IsNullOrWhiteSpace(cs))
            throw new InvalidOperationException("Falta la variable de entorno KonSolutions_DB_CONNECTION (cadena de conexion a SQL Server). Definela a nivel de maquina y reinicia el servicio.");
        _cs = cs;
    }
    public IDbConnection CreateConnection()
    {
        var conn = new SqlConnection(_cs);
        if (!_anotada) { try { var b = new SqlConnectionStringBuilder(_cs); _log?.LogDebug("SQL: conectando a {Servidor} / base {Base} (usuario {Usuario}; la clave no se muestra)", b.DataSource, b.InitialCatalog, string.IsNullOrEmpty(b.UserID) ? "(autenticacion de Windows)" : b.UserID); } catch { } _anotada = true; }
        conn.Open();
        using var cmd = conn.CreateCommand();
        cmd.CommandText = "SET ARITHABORT ON";
        cmd.ExecuteNonQuery();
        return conn;
    }
}

public class PathFiles
{
    public string COD_KEY_PATHFILE { get; set; } = string.Empty;
    public string? DES_PATH_FILE { get; set; }
}

public interface IPathFilesRepository { Task<PathFiles?> ShowbyidAsync(string COD_KEY_PATHFILE); }

public class PathFilesRepository : IPathFilesRepository
{
    private readonly IDbConnectionFactory _db;
    public PathFilesRepository(IDbConnectionFactory db) => _db = db;
    public async Task<PathFiles?> ShowbyidAsync(string COD_KEY_PATHFILE)
    {
        using var conn = _db.CreateConnection();
        return await conn.QueryFirstOrDefaultAsync<PathFiles>("PROC_ENTORNO_PATHFILES_SHOWBYID", new { COD_KEY_PATHFILE }, commandType: CommandType.StoredProcedure);
    }
}
