using System.Globalization;
using System.Net;
using System.Security;
using System.Text;
using System.Xml.Linq;
using Microsoft.Extensions.Options;

namespace KonSolutions.XmlRecibidos;

/// <summary>Lo que se necesita de un comprobante para preguntarle a SUNAT si existe y es válido.</summary>
public class SunatConsulta
{
    public string RucEmisor { get; set; } = "";
    public string Tipo { get; set; } = "";            // 01 factura, 03 boleta, 07 nota de crédito, 08 nota de débito
    public string Serie { get; set; } = "";
    public string Numero { get; set; } = "";          // el correlativo, sin ceros a la izquierda
    public DateTime Fecha { get; set; }
    public decimal Importe { get; set; }
    public string? TipoDocReceptor { get; set; }
    public string? NumDocReceptor { get; set; }
}

/// <summary>La Clave SOL de la empresa (Configuración general): RUC + usuario + clave.</summary>
public record CredencialSol(string Ruc, string Usuario, string Clave);

/// <summary>El resultado de la consulta. Codigo: el que devolvió SUNAT (por ejemplo 0001); ERR cuando no se pudo consultar (sin red, SUNAT caído, credenciales rechazadas).</summary>
public record SunatResultado(string Codigo, string Mensaje, bool Consultado)
{
    public static SunatResultado Error(string mensaje) => new("ERR", mensaje, false);
}

public interface ISunatValidador
{
    Task<SunatResultado> ValidarAsync(SunatConsulta c, CredencialSol cred, CancellationToken ct);
}

public class SunatOpciones
{
    /// <summary>false: el servicio no consulta a SUNAT.</summary>
    public bool Habilitado { get; set; } = true;
    /// <summary>El servicio SOAP de consulta de validez de comprobantes (billValidService).</summary>
    public string Url { get; set; } = "https://e-factura.sunat.gob.pe/ol-it-wsconsvalidcpe/billValidService";
    public int TimeoutSegundos { get; set; } = 30;
}

/// <summary>Consulta a SUNAT, con la Clave SOL de la empresa, si un comprobante existe y está aceptado (servicio SOAP billValidService, operación validaCDPcriterios, con autenticación WS-Security UsernameToken: «RUC+usuario» y clave).
/// No usa el XML: usa los datos del comprobante (RUC del emisor, tipo, serie, número, fecha e importe). El código y el mensaje que devuelve SUNAT se guardan tal cual en el comprobante.</summary>
public class SunatValidador : ISunatValidador
{
    private static readonly XNamespace Soap = "http://schemas.xmlsoap.org/soap/envelope/";
    private readonly HttpClient _http;
    private readonly SunatOpciones _o;

    public SunatValidador(HttpClient http, IOptions<SunatOpciones> o)
    {
        _http = http; _o = o.Value;
        _http.Timeout = TimeSpan.FromSeconds(Math.Clamp(_o.TimeoutSegundos, 5, 120));
    }

    /// <summary>El usuario del WS-Security: RUC + usuario SOL. Si el usuario guardado ya empieza con 11 dígitos (ya trae el RUC), se envía tal cual.</summary>
    public static string UsuarioEnvio(CredencialSol cred)
    {
        var u = (cred.Usuario ?? "").Trim();
        return u.Length > 11 && u.Take(11).All(char.IsDigit) ? u : (cred.Ruc ?? "").Trim() + u;
    }

    /// <summary>El sobre SOAP de la consulta.</summary>
    public static string Sobre(SunatConsulta c, CredencialSol cred)
    {
        static string X(string? s) => SecurityElement.Escape(s ?? "") ?? "";
        var numero = c.Numero.TrimStart('0'); if (numero.Length == 0) numero = "0";
        var sb = new StringBuilder();
        sb.Append("<soapenv:Envelope xmlns:soapenv=\"http://schemas.xmlsoap.org/soap/envelope/\" xmlns:ser=\"http://service.sunat.gob.pe\" xmlns:wsse=\"http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd\">");
        sb.Append("<soapenv:Header><wsse:Security><wsse:UsernameToken><wsse:Username>").Append(X(UsuarioEnvio(cred))).Append("</wsse:Username><wsse:Password>").Append(X(cred.Clave)).Append("</wsse:Password></wsse:UsernameToken></wsse:Security></soapenv:Header>");
        sb.Append("<soapenv:Body><ser:validaCDPcriterios>");
        sb.Append("<rucEmisor>").Append(X(c.RucEmisor)).Append("</rucEmisor>");
        sb.Append("<tipoCDP>").Append(X(c.Tipo)).Append("</tipoCDP>");
        sb.Append("<serieCDP>").Append(X(c.Serie)).Append("</serieCDP>");
        sb.Append("<numeroCDP>").Append(X(numero)).Append("</numeroCDP>");
        sb.Append("<tipoDocIdReceptor>").Append(X(c.TipoDocReceptor)).Append("</tipoDocIdReceptor>");
        sb.Append("<numeroDocIdReceptor>").Append(X(c.NumDocReceptor)).Append("</numeroDocIdReceptor>");
        sb.Append("<fechaEmision>").Append(c.Fecha.ToString("dd/MM/yyyy", CultureInfo.InvariantCulture)).Append("</fechaEmision>");
        sb.Append("<importeTotal>").Append(c.Importe.ToString("0.00", CultureInfo.InvariantCulture)).Append("</importeTotal>");
        sb.Append("<nroAutorizacion></nroAutorizacion>");
        sb.Append("</ser:validaCDPcriterios></soapenv:Body></soapenv:Envelope>");
        return sb.ToString();
    }

    /// <summary>Lee la respuesta: el código (statusCode) y el mensaje (statusMessage o message) de SUNAT, o el error del SOAP Fault.</summary>
    public static SunatResultado Lee(string cuerpo)
    {
        XDocument doc;
        try { doc = XDocument.Parse(cuerpo); }
        catch { return SunatResultado.Error("SUNAT respondió algo que no es XML."); }
        string? Primero(string nombre) => doc.Descendants().FirstOrDefault(e => e.Name.LocalName == nombre)?.Value?.Trim();
        var falla = Primero("faultstring");
        if (!string.IsNullOrWhiteSpace(falla)) return SunatResultado.Error("SUNAT rechazó la consulta: " + falla);
        var codigo = Primero("statusCode");
        if (string.IsNullOrWhiteSpace(codigo)) return SunatResultado.Error("SUNAT no devolvió un código de estado.");
        var mensaje = Primero("statusMessage") ?? Primero("message") ?? "";
        return new SunatResultado(codigo.Length > 6 ? codigo[..6] : codigo, string.IsNullOrWhiteSpace(mensaje) ? "Sin mensaje de SUNAT." : mensaje, true);
    }

    public async Task<SunatResultado> ValidarAsync(SunatConsulta c, CredencialSol cred, CancellationToken ct)
    {
        if (!_o.Habilitado) return SunatResultado.Error("La consulta a SUNAT está apagada (Sunat:Habilitado).");
        try
        {
            using var req = new HttpRequestMessage(HttpMethod.Post, _o.Url) { Content = new StringContent(Sobre(c, cred), Encoding.UTF8, "text/xml") };
            req.Headers.Add("SOAPAction", "");
            using var resp = await _http.SendAsync(req, ct);
            var cuerpo = await resp.Content.ReadAsStringAsync(ct);
            if (resp.StatusCode is HttpStatusCode.Unauthorized or HttpStatusCode.Forbidden)
            {
                // el detalle que dio SUNAT (si lo dio) y qué usuario se envió, para distinguir una clave mala de un problema de formato
                var falla = cuerpo.Contains("faultstring") ? Lee(cuerpo).Mensaje : "";
                return SunatResultado.Error($"SUNAT no aceptó la Clave SOL (HTTP {(int)resp.StatusCode}). Usuario enviado: «{UsuarioEnvio(cred)}», clave de {cred.Clave.Length} caracteres. Revisa que sean los de Operaciones en Línea.{(string.IsNullOrWhiteSpace(falla) ? "" : " Detalle: " + falla)}");
            }
            if (string.IsNullOrWhiteSpace(cuerpo)) return SunatResultado.Error($"SUNAT no respondió (HTTP {(int)resp.StatusCode}).");
            return Lee(cuerpo);
        }
        catch (OperationCanceledException) when (!ct.IsCancellationRequested) { return SunatResultado.Error("SUNAT no respondió a tiempo."); }
        catch (HttpRequestException ex) { return SunatResultado.Error("No se pudo comunicar con SUNAT: " + ex.Message); }
    }
}
