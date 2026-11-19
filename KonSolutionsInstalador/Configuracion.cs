using System.Text.Json;
using System.Text.Json.Nodes;

namespace KonSolutionsInstalador;

/// <summary>Lo que se pregunta al instalar. El secreto de Graph y la conexion a SQL NO van en appsettings.json: se guardan como variables de entorno de MAQUINA.</summary>
public class Ajustes
{
    public string Destino { get; set; } = @"C:\KonSolutions\Servicios";
    public string TenantId { get; set; } = "";
    public string ClientId { get; set; } = "";
    public string Buzon { get; set; } = "";
    public string SecretoGraph { get; set; } = "";     // vacio = dejar la variable actual
    public string Conexion { get; set; } = "";         // vacio = dejar la variable actual
    public string Cuenta { get; set; } = "";           // vacio = LocalSystem
    public string ClaveCuenta { get; set; } = "";
    public bool Iniciar { get; set; } = true;
}

/// <summary>Edita el appsettings.json del servicio sin tocar el resto de sus secciones.</summary>
public static class Configuracion
{
    static readonly JsonSerializerOptions Opc = new() { WriteIndented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping };

    public static JsonObject Leer(string archivo) => File.Exists(archivo) ? (JsonNode.Parse(File.ReadAllText(archivo), documentOptions: new JsonDocumentOptions { CommentHandling = JsonCommentHandling.Skip, AllowTrailingCommas = true }) as JsonObject ?? new JsonObject()) : new JsonObject();

    /// <summary>Pone TenantId, ClientId y Buzon en la seccion XmlRecibidos. El ClientSecret del archivo se deja SIEMPRE vacio (el secreto va en la variable de entorno).</summary>
    public static void Aplicar(string archivo, Ajustes a)
    {
        var raiz = Leer(archivo);
        var x = raiz["XmlRecibidos"] as JsonObject ?? new JsonObject();
        if (a.TenantId != "") x["TenantId"] = a.TenantId;
        if (a.ClientId != "") x["ClientId"] = a.ClientId;
        if (a.Buzon != "") x["Buzon"] = a.Buzon;
        x["ClientSecret"] = "";
        raiz["XmlRecibidos"] = x;
        File.WriteAllText(archivo, raiz.ToJsonString(Opc));
    }

    public static (string Tenant, string Client, string Buzon) Actuales(string archivo)
    {
        var x = Leer(archivo)["XmlRecibidos"] as JsonObject;
        string S(string k) => x?[k]?.GetValue<string>() ?? "";
        return (S("TenantId"), S("ClientId"), S("Buzon"));
    }
}
