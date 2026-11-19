using System.Diagnostics;

namespace KonSolutionsInstalador;

/// <summary>Maneja el servicio de Windows con sc.exe (viene con Windows: no hace falta instalar nada).</summary>
public static class ServicioWindows
{
    public const string Nombre = "KonSolutionsServicios";
    public const string ExeServicio = "KonSolutionsServicios.exe";

    public static (int Codigo, string Salida) Sc(params string[] args)
    {
        var psi = new ProcessStartInfo("sc.exe") { RedirectStandardOutput = true, RedirectStandardError = true, UseShellExecute = false, CreateNoWindow = true };
        foreach (var a in args) psi.ArgumentList.Add(a);
        using var p = Process.Start(psi)!;
        var s = p.StandardOutput.ReadToEnd() + p.StandardError.ReadToEnd();
        p.WaitForExit();
        return (p.ExitCode, s.Trim());
    }

    public static bool Existe() => Sc("query", Nombre).Codigo == 0;

    public static string Estado()
    {
        var (c, s) = Sc("query", Nombre);
        if (c != 0) return "NO INSTALADO";
        foreach (var l in s.Split('\n')) if (l.Contains("STATE")) return l.Split(':')[1].Trim().Split(' ').Last();
        return "DESCONOCIDO";
    }

    public static bool Esperar(string estado, int segundos)
    {
        var fin = DateTime.UtcNow.AddSeconds(segundos);
        while (DateTime.UtcNow < fin) { if (Estado() == estado) return true; Thread.Sleep(500); }
        return Estado() == estado;
    }

    public static void Detener() { if (Existe() && Estado() != "STOPPED") { Sc("stop", Nombre); Esperar("STOPPED", 30); } }

    /// <summary>Crea el servicio (o lo actualiza si ya existe): inicio automatico retrasado, reinicio ante fallas y descripcion.</summary>
    public static (bool Ok, string Mensaje) Crear(string exe, string cuenta, string clave)
    {
        var existe = Existe();
        var args = new List<string> { existe ? "config" : "create", Nombre, "binPath=", exe, "start=", "delayed-auto", "DisplayName=", "KonSolutions Servicios" };
        if (!string.IsNullOrWhiteSpace(cuenta)) { args.Add("obj="); args.Add(cuenta); args.Add("password="); args.Add(clave); }
        else if (existe) { args.Add("obj="); args.Add("LocalSystem"); }
        var (c, s) = Sc(args.ToArray());
        if (c != 0) return (false, s);
        Sc("description", Nombre, "Servicio generico de KonSolutions: corre tareas programadas (hoy: carga de XML recibidos desde Microsoft 365).");
        Sc("failure", Nombre, "reset=", "86400", "actions=", "restart/60000/restart/60000/restart/60000");
        return (true, existe ? "Servicio actualizado." : "Servicio creado.");
    }

    public static (bool Ok, string Mensaje) Iniciar()
    {
        var (c, s) = Sc("start", Nombre);
        if (c != 0 && !s.Contains("1056")) return (false, s);   // 1056 = ya estaba corriendo
        return Esperar("RUNNING", 30) ? (true, "El servicio esta corriendo.") : (false, "El servicio no llego a RUNNING: mira el Registro de eventos de Windows y la carpeta logs.");
    }

    public static (bool Ok, string Mensaje) Eliminar() { Detener(); var (c, s) = Sc("delete", Nombre); return (c == 0, c == 0 ? "Servicio eliminado." : s); }
}
