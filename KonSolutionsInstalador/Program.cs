using KonSolutionsInstalador;

// Instalador de KonSolutionsServicios. Sin argumentos: menu interactivo. Con argumentos (instalacion desatendida):
//   Instalador.exe --instalar --destino C:\KonSolutions\Servicios --tenant ... --client ... --buzon ... --secreto ... --conexion "Server=..." [--cuenta DOM\usu --clave-cuenta ...] [--sin-iniciar]
//   Instalador.exe --desinstalar [--borrar-archivos --destino ...]     Instalador.exe --estado
const string VarDb = "KonSolutions_DB_CONNECTION", VarGraph = "KonSolutions_GRAPH_SECRET";
Console.OutputEncoding = System.Text.Encoding.UTF8;

string? Arg(string nombre) { var i = Array.IndexOf(args, nombre); return i >= 0 && i + 1 < args.Length && !args[i + 1].StartsWith("--") ? args[i + 1] : null; }
bool Tiene(string nombre) => args.Contains(nombre);
string? Maquina(string v) => Environment.GetEnvironmentVariable(v, EnvironmentVariableTarget.Machine);

Console.ForegroundColor = ConsoleColor.Cyan; Console.WriteLine("KonSolutions - Instalador del servicio de Windows (KonSolutionsServicios)"); Console.ResetColor();

if (!OperatingSystem.IsWindows()) { Consola.Error("Este instalador es para Windows."); return 1; }
if (!EsAdministrador()) { Consola.Error("Ejecutalo como Administrador (clic derecho > Ejecutar como administrador)."); return 1; }

if (Tiene("--estado")) { Console.WriteLine("Servicio: " + ServicioWindows.Estado()); return 0; }
if (Tiene("--desinstalar")) return Desinstalar(Tiene("--borrar-archivos"), Arg("--destino"), interactivo: false);
if (Tiene("--instalar")) return await Instalar(LeerArgs(), interactivo: false);

while (true)
{
    Console.WriteLine();
    Console.WriteLine($"  Estado del servicio: {ServicioWindows.Estado()}");
    Console.WriteLine("  1) Instalar o actualizar");
    Console.WriteLine("  2) Desinstalar");
    Console.WriteLine("  3) Iniciar / reiniciar");
    Console.WriteLine("  4) Detener");
    Console.WriteLine("  5) Salir");
    switch (Consola.Preguntar("Elige", "1"))
    {
        case "1": await Instalar(Preguntar(), interactivo: true); break;
        case "2": Desinstalar(Consola.Confirmar("Borrar tambien los archivos de la carpeta de instalacion?", false), null, interactivo: true); break;
        case "3": ServicioWindows.Detener(); { var r = ServicioWindows.Iniciar(); (r.Ok ? (Action<string>)Consola.Ok : Consola.Error)(r.Mensaje); } break;
        case "4": ServicioWindows.Detener(); Consola.Ok("Detenido: " + ServicioWindows.Estado()); break;
        case "5": return 0;
    }
}

static bool EsAdministrador() { using var id = System.Security.Principal.WindowsIdentity.GetCurrent(); return new System.Security.Principal.WindowsPrincipal(id).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator); }

Ajustes LeerArgs() => new()
{
    Destino = Arg("--destino") ?? @"C:\KonSolutions\Servicios", TenantId = Arg("--tenant") ?? "", ClientId = Arg("--client") ?? "", Buzon = Arg("--buzon") ?? "", SecretoGraph = Arg("--secreto") ?? "",
    Conexion = Arg("--conexion") ?? "", Cuenta = Arg("--cuenta") ?? "", ClaveCuenta = Arg("--clave-cuenta") ?? "", Iniciar = !Tiene("--sin-iniciar")
};

Ajustes Preguntar()
{
    Consola.Titulo("Datos de la instalacion (Enter acepta lo que sale entre corchetes)");
    Consola.Info("Los datos de Microsoft 365 son OPCIONALES: sin ellos el servicio solo procesa los archivos de la carpeta de Compras.");
    var a = new Ajustes();
    a.Destino = Consola.Preguntar("Carpeta de instalacion", a.Destino);
    var (t, c, b) = Configuracion.Actuales(Path.Combine(a.Destino, "appsettings.json"));
    a.TenantId = Consola.Preguntar("Microsoft 365: TenantId (ID de directorio)", t == "" ? null : t);
    a.ClientId = Consola.Preguntar("Microsoft 365: ClientId (ID de aplicacion)", c == "" ? null : c);
    a.Buzon = Consola.Preguntar("Buzon del que se leen los correos (por ejemplo compras@empresa.com)", b == "" ? null : b);
    a.SecretoGraph = Consola.PreguntarOculto(Maquina(VarGraph) is null ? "Secreto de la aplicacion de Azure" : "Secreto de la aplicacion de Azure (ya hay uno guardado)");
    a.Conexion = Consola.PreguntarOculto(Maquina(VarDb) is null ? "Cadena de conexion a SQL Server" : "Cadena de conexion a SQL Server (ya hay una guardada)");
    Console.WriteLine("  Cuenta con que corre el servicio. Si la ruta de archivos es una carpeta de RED (\\\\servidor\\...) usa una cuenta con acceso: LocalSystem no entra a carpetas de red.");
    a.Cuenta = Consola.Preguntar("Cuenta (DOMINIO\\usuario; vacio = LocalSystem)", "");
    if (a.Cuenta != "") a.ClaveCuenta = Consola.PreguntarOculto("Clave de esa cuenta");
    return a;
}

async Task<int> Instalar(Ajustes a, bool interactivo)
{
    Consola.Titulo("1. Archivos del servicio");
    var origen = Path.Combine(AppContext.BaseDirectory, "Servicio");
    if (!File.Exists(Path.Combine(origen, ServicioWindows.ExeServicio))) { Consola.Error($"No encuentro {ServicioWindows.ExeServicio} en {origen}. Usa la carpeta que genera publicar.ps1 (Instalador.exe y la subcarpeta Servicio juntos)."); return 1; }
    var cfgDestino = Path.Combine(a.Destino, "appsettings.json");
    var primeraVez = !File.Exists(cfgDestino);

    Consola.Titulo("2. Comprobaciones");
    var conexion = a.Conexion != "" ? a.Conexion : Maquina(VarDb) ?? "";
    var secreto = a.SecretoGraph != "" ? a.SecretoGraph : Maquina(VarGraph) ?? "";
    var tenant = a.TenantId != "" ? a.TenantId : Configuracion.Actuales(cfgDestino).Tenant;
    var client = a.ClientId != "" ? a.ClientId : Configuracion.Actuales(cfgDestino).Client;
    var buzon = a.Buzon != "" ? a.Buzon : Configuracion.Actuales(cfgDestino).Buzon;
    var fallas = 0;
    if (conexion == "") { Consola.Error("Falta la cadena de conexion a SQL Server."); fallas++; }
    else { var r = await Pruebas.SqlAsync(conexion); if (r.Ok) Consola.Ok(r.Mensaje); else { Consola.Error(r.Mensaje); fallas++; } }
    if (tenant == "" || client == "" || secreto == "" || buzon == "") Consola.Aviso("Sin cuenta de Microsoft 365 (TenantId, ClientId, secreto y buzon): el servicio NO leera el correo, pero SI procesara los XML y PDF que dejes en la carpeta de archivos de Compras (cargarlos y pasarlos a procesados o errores). Puedes agregar la cuenta despues volviendo a correr el instalador.");
    else { var r = await Pruebas.GraphAsync(tenant, client, secreto); if (r.Ok) Consola.Ok(r.Mensaje); else { Consola.Aviso(r.Mensaje + " El correo no se leera hasta corregirlo; la carpeta si se procesa."); } }
    if (conexion != "") await AvisoRutaAsync(conexion, a.Cuenta);
    if (fallas > 0 && interactivo && !Consola.Confirmar($"Hay {fallas} comprobacion(es) que fallaron. Instalar de todos modos?", false)) return 1;
    if (fallas > 0 && !interactivo) { Consola.Error("Instalacion cancelada por las comprobaciones."); return 1; }

    Consola.Titulo("3. Instalando");
    try
    {
        ServicioWindows.Detener();
        Directory.CreateDirectory(a.Destino);
        var respaldo = primeraVez ? null : File.ReadAllText(cfgDestino);
        CopiarCarpeta(origen, a.Destino);
        if (respaldo is not null) File.WriteAllText(cfgDestino, respaldo);          // la configuracion que ya tenias no se pisa
        Configuracion.Aplicar(cfgDestino, a);
        Consola.Ok($"Archivos copiados a {a.Destino}");
        if (a.Conexion != "") { Environment.SetEnvironmentVariable(VarDb, a.Conexion, EnvironmentVariableTarget.Machine); Consola.Ok($"Variable de maquina {VarDb} guardada."); }
        if (a.SecretoGraph != "") { Environment.SetEnvironmentVariable(VarGraph, a.SecretoGraph, EnvironmentVariableTarget.Machine); Consola.Ok($"Variable de maquina {VarGraph} guardada."); }
        var crear = ServicioWindows.Crear(Path.Combine(a.Destino, ServicioWindows.ExeServicio), a.Cuenta, a.ClaveCuenta);
        if (!crear.Ok) { Consola.Error(crear.Mensaje); return 1; }
        Consola.Ok(crear.Mensaje);
    }
    catch (Exception ex) { Consola.Error("No se pudo instalar: " + ex.Message); return 1; }

    if (a.Iniciar)
    {
        Consola.Titulo("4. Iniciando");
        var r = ServicioWindows.Iniciar();
        if (r.Ok) Consola.Ok(r.Mensaje); else { Consola.Error(r.Mensaje); return 1; }
        Consola.Info($"Primera corrida en ~30 segundos. Mira {Path.Combine(a.Destino, "logs")}\\servicio_{DateTime.Now:yyyy-MM-dd}.log");
    }
    Console.WriteLine(); Consola.Ok("Listo.");
    return 0;
}

int Desinstalar(bool borrar, string? destino, bool interactivo)
{
    Consola.Titulo("Desinstalando");
    if (!ServicioWindows.Existe()) Consola.Aviso("El servicio no estaba instalado.");
    else { var r = ServicioWindows.Eliminar(); if (r.Ok) Consola.Ok(r.Mensaje); else { Consola.Error(r.Mensaje); return 1; } }
    var dir = destino ?? @"C:\KonSolutions\Servicios";
    if (interactivo && borrar) dir = Consola.Preguntar("Carpeta a borrar", dir);
    if (borrar && Directory.Exists(dir)) { try { Directory.Delete(dir, true); Consola.Ok("Archivos borrados: " + dir); } catch (Exception ex) { Consola.Aviso("No se pudo borrar la carpeta: " + ex.Message); } }
    Consola.Info("Las variables de entorno de maquina no se tocan (por si reinstalas).");
    return 0;
}

static void CopiarCarpeta(string origen, string destino)
{
    foreach (var d in Directory.GetDirectories(origen, "*", SearchOption.AllDirectories)) Directory.CreateDirectory(Path.Combine(destino, Path.GetRelativePath(origen, d)));
    foreach (var f in Directory.GetFiles(origen, "*", SearchOption.AllDirectories)) File.Copy(f, Path.Combine(destino, Path.GetRelativePath(origen, f)), true);
}

// avisa si la ruta de archivos de Compras es de red y el servicio va a correr como LocalSystem (no entra a carpetas de red)
static async Task AvisoRutaAsync(string conexion, string cuenta)
{
    try
    {
        await using var c = new Microsoft.Data.SqlClient.SqlConnection(conexion); await c.OpenAsync();
        await using var cmd = c.CreateCommand();
        cmd.CommandText = "SELECT TOP 1 COD_KEY_PATHFILE_COMPRAS_XML FROM ESTANDAR.CONFIGURACION_GENERAL";
        var clave = (await cmd.ExecuteScalarAsync()) as string;
        if (string.IsNullOrWhiteSpace(clave)) { Consola.Aviso("Configuracion general > Compras todavia no tiene la ruta de archivos de los XML: el servicio no podra guardar hasta que la elijas."); return; }
        await using var sp = c.CreateCommand(); sp.CommandType = System.Data.CommandType.StoredProcedure; sp.CommandText = "PROC_ENTORNO_PATHFILES_SHOWBYID"; sp.Parameters.AddWithValue("@COD_KEY_PATHFILE", clave.Trim());
        await using var r = await sp.ExecuteReaderAsync();
        string? ruta = null; if (await r.ReadAsync()) { var i = r.GetOrdinal("DES_PATH_FILE"); ruta = r.IsDBNull(i) ? null : r.GetString(i).Trim(); }
        if (string.IsNullOrEmpty(ruta)) { Consola.Aviso($"La ruta '{clave}' no existe en Entorno > Rutas de archivos o no tiene carpeta."); return; }
        var ve = Pruebas.Carpeta(ruta);
        if (ve.Ok) Consola.Ok(ve.Mensaje); else Consola.Aviso(ve.Mensaje);
        if (ruta.StartsWith(@"\\") && string.IsNullOrWhiteSpace(cuenta)) Consola.Aviso($"La ruta {ruta} es de RED y el servicio correria como LocalSystem, que NO entra a carpetas de red. Instalalo con una cuenta (DOMINIO\\usuario) que tenga acceso.");
    }
    catch (Exception ex) { Consola.Aviso("No pude revisar la ruta de archivos: " + ex.Message); }
}
