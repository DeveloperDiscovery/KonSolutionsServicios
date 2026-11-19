using System.Net.Http.Headers;
using System.Text.Json;
using Microsoft.Data.SqlClient;

namespace KonSolutionsInstalador;

/// <summary>Comprobaciones antes de instalar: asi un dato mal escrito se descubre aqui y no dias despues con el servicio "corriendo pero sin hacer nada".</summary>
public static class Pruebas
{
    public static async Task<(bool Ok, string Mensaje)> SqlAsync(string conexion)
    {
        try
        {
            await using var c = new SqlConnection(conexion);
            await c.OpenAsync();
            await using var cmd = c.CreateCommand();
            cmd.CommandText = @"SELECT (SELECT COUNT(*) FROM ESTANDAR.CONFIGURACION_GENERAL), (SELECT COUNT(*) FROM sys.procedures WHERE name = N'PROC_XML_RECIBIDOS_CARGAR'),
                                       (SELECT COUNT(*) FROM sys.procedures WHERE name = N'PROC_ENTORNO_PATHFILES_SHOWBYID')";
            await using var r = await cmd.ExecuteReaderAsync();
            await r.ReadAsync();
            var cfg = r.GetInt32(0); var cargar = r.GetInt32(1); var rutas = r.GetInt32(2);
            if (cargar == 0) return (false, "Conecta, pero falta PROC_XML_RECIBIDOS_CARGAR: corre los scripts SQL 39, 41 y 48.");
            if (rutas == 0) return (false, "Conecta, pero falta PROC_ENTORNO_PATHFILES_SHOWBYID.");
            return (true, $"Conecta a {c.DataSource} / {c.Database}; hay {cfg} configuracion(es) general(es) y los procedimientos del servicio existen.");
        }
        catch (SqlException ex) when (ex.Message.Contains("Invalid object name") || ex.Message.Contains("Nombre de objeto")) { return (false, "Conecta a SQL Server, pero a una base sin los scripts del servicio (falta ESTANDAR.CONFIGURACION_GENERAL u otro objeto): revisa la base de la cadena y corre los scripts SQL 39, 41 y 48."); }
        catch (Exception ex) { return (false, "No conecta a SQL Server: " + ex.Message); }
    }

    /// <summary>Pide un token de Microsoft Graph con las credenciales (client credentials). Solo prueba que el tenant, el cliente y el secreto son validos; el permiso Mail.ReadWrite y el buzon se prueban al leer el correo.</summary>
    public static async Task<(bool Ok, string Mensaje)> GraphAsync(string tenant, string client, string secreto, string authorityHost = "https://login.microsoftonline.com")
    {
        try
        {
            using var http = new HttpClient { Timeout = TimeSpan.FromSeconds(30) };
            var cuerpo = new FormUrlEncodedContent(new Dictionary<string, string> { ["client_id"] = client, ["client_secret"] = secreto, ["scope"] = "https://graph.microsoft.com/.default", ["grant_type"] = "client_credentials" });
            var resp = await http.PostAsync($"{authorityHost.TrimEnd('/')}/{tenant}/oauth2/v2.0/token", cuerpo);
            var json = await resp.Content.ReadAsStringAsync();
            if (resp.IsSuccessStatusCode) return (true, "Microsoft 365 acepto las credenciales (tenant, cliente y secreto).");
            string? desc = null; try { desc = JsonDocument.Parse(json).RootElement.GetProperty("error_description").GetString(); } catch { }
            return (false, $"Microsoft 365 rechazo las credenciales ({(int)resp.StatusCode}): {(desc ?? json).Split('\n')[0].Trim()}");
        }
        catch (Exception ex) { return (false, "No se pudo llegar a Microsoft 365: " + ex.Message); }
    }

    public static (bool Ok, string Mensaje) Carpeta(string ruta)
    {
        try { if (Directory.Exists(ruta)) return (true, $"La carpeta {ruta} se ve desde esta sesion."); return (false, $"La carpeta {ruta} no existe o esta sesion no la ve."); }
        catch (Exception ex) { return (false, ex.Message); }
    }
}
