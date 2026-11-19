using System.Net;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using Microsoft.Extensions.Options;

namespace KonSolutions.XmlRecibidos;

/// <summary>Un error de Microsoft Graph. Fatal = no vale la pena seguir (la cuenta no tiene permiso o las credenciales no sirven); Transitorio = una falla pasajera (límite de uso, servicio caído, sin red) que se reintenta
/// en la siguiente corrida sin tocar el correo.</summary>
public class GraphException : Exception
{
    public HttpStatusCode? Estado { get; }
    public bool Fatal { get; }
    public bool Transitorio { get; }
    public GraphException(string mensaje, HttpStatusCode? estado, bool fatal, bool transitorio, Exception? inner = null) : base(mensaje, inner) { Estado = estado; Fatal = fatal; Transitorio = transitorio; }
}

/// <summary>El token de acceso se guarda en un solo lugar para todo el servicio: el cliente HTTP se crea muchas veces y cada una pediría otro.</summary>
public class GraphTokenCache
{
    public readonly SemaphoreSlim Candado = new(1, 1);
    public string? Token;
    public DateTimeOffset Vence;
}

/// <summary>Microsoft Graph con permisos de aplicación (client credentials): no hay usuario. Hace falta el permiso Mail.ReadWrite de aplicación (para leer y para mover correos); conviene limitarlo al buzón con
/// una política de acceso de aplicación (New-ApplicationAccessPolicy). Reintenta lo pasajero (429, 503, 504) respetando Retry-After.</summary>
public class GraphCorreo : IGraphCorreo
{
    private readonly HttpClient _http;
    private readonly XmlRecibidosOpciones _o;
    private readonly GraphTokenCache _cache;
    private const int Intentos = 4;
    // las pausas entre intentos: en las pruebas se pueden acortar
    public static TimeSpan PausaBase = TimeSpan.FromSeconds(1);

    public GraphCorreo(HttpClient http, IOptions<XmlRecibidosOpciones> o, GraphTokenCache cache)
    { _http = http; _o = o.Value; _cache = cache; _http.Timeout = TimeSpan.FromMinutes(5); }

    public bool Configurado => _o.Configurado;
    public string? Buzon => _o.Buzon;

    private string Usuario => $"{_o.GraphUrl.TrimEnd('/')}/users/{Uri.EscapeDataString(_o.Buzon ?? "")}";

    // ───────── token ─────────
    private async Task<string> TokenAsync(CancellationToken ct, bool renovar = false)
    {
        await _cache.Candado.WaitAsync(ct);
        try
        {
            if (!renovar && _cache.Token is not null && _cache.Vence > DateTimeOffset.UtcNow.AddMinutes(2)) return _cache.Token;
            var url = $"{_o.AuthorityHost.TrimEnd('/')}/{Uri.EscapeDataString(_o.TenantId ?? "")}/oauth2/v2.0/token";
            var cuerpo = new FormUrlEncodedContent(new Dictionary<string, string>
            {
                ["client_id"] = _o.ClientId ?? "", ["client_secret"] = _o.SecretoEfectivo() ?? "", ["scope"] = "https://graph.microsoft.com/.default", ["grant_type"] = "client_credentials"
            });
            HttpResponseMessage r;
            try { r = await _http.PostAsync(url, cuerpo, ct); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            { throw new GraphException("No se pudo pedir el token a Microsoft 365: " + ex.Message, null, false, true, ex); }
            var txt = await r.Content.ReadAsStringAsync(ct);
            if (!r.IsSuccessStatusCode)
            {
                var det = Detalle(txt);
                // credenciales mal puestas (400/401) = fatal; una falla del servicio (5xx) = pasajera
                var pasajero = (int)r.StatusCode >= 500 || r.StatusCode == HttpStatusCode.TooManyRequests;
                throw new GraphException($"Microsoft 365 no dio el token ({(int)r.StatusCode}): {det}", r.StatusCode, !pasajero, pasajero);
            }
            using var doc = JsonDocument.Parse(txt);
            _cache.Token = doc.RootElement.GetProperty("access_token").GetString();
            var seg = doc.RootElement.TryGetProperty("expires_in", out var e) ? e.GetInt32() : 3600;
            _cache.Vence = DateTimeOffset.UtcNow.AddSeconds(seg);
            return _cache.Token!;
        }
        finally { _cache.Candado.Release(); }
    }

    private static string Detalle(string txt)
    {
        try
        {
            using var d = JsonDocument.Parse(txt);
            if (d.RootElement.TryGetProperty("error_description", out var ed)) return ed.GetString() ?? txt;
            if (d.RootElement.TryGetProperty("error", out var e))
            {
                if (e.ValueKind == JsonValueKind.String) return e.GetString() ?? txt;
                if (e.TryGetProperty("message", out var m)) return m.GetString() ?? txt;
            }
        }
        catch (JsonException) { }
        return txt.Length > 300 ? txt[..300] : txt;
    }

    // ───────── pedidos ─────────
    private async Task<HttpResponseMessage> EnviaAsync(Func<HttpRequestMessage> crea, CancellationToken ct)
    {
        var renovado = false;
        for (var intento = 1; ; intento++)
        {
            var token = await TokenAsync(ct);
            using var req = crea();
            req.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            HttpResponseMessage r;
            try { r = await _http.SendAsync(req, HttpCompletionOption.ResponseContentRead, ct); }
            catch (Exception ex) when (ex is HttpRequestException or TaskCanceledException && !ct.IsCancellationRequested)
            {
                if (intento >= Intentos) throw new GraphException("No se pudo hablar con Microsoft 365: " + ex.Message, null, false, true, ex);
                await Task.Delay(Pausa(intento, null), ct); continue;
            }
            if (r.IsSuccessStatusCode) return r;
            var estado = r.StatusCode; var txt = await r.Content.ReadAsStringAsync(ct);
            var retryAfter = r.Headers.RetryAfter?.Delta;
            r.Dispose();
            if (estado == HttpStatusCode.Unauthorized && !renovado) { renovado = true; await TokenAsync(ct, renovar: true); intento--; continue; }
            var pasajero = estado == HttpStatusCode.TooManyRequests || estado == HttpStatusCode.ServiceUnavailable || estado == HttpStatusCode.GatewayTimeout || estado == HttpStatusCode.BadGateway || estado == HttpStatusCode.RequestTimeout;
            if (pasajero && intento < Intentos) { await Task.Delay(Pausa(intento, retryAfter), ct); continue; }
            var det = Detalle(txt);
            if (estado == HttpStatusCode.Unauthorized || estado == HttpStatusCode.Forbidden)
                throw new GraphException($"Microsoft 365 rechazó el acceso al buzón ({(int)estado}): {det}. Revisa que la aplicación tenga el permiso Mail.ReadWrite de aplicación con consentimiento del administrador y que pueda ver el buzón.", estado, true, false);
            throw new GraphException($"Microsoft 365 respondió {(int)estado}: {det}", estado, false, pasajero || (int)estado >= 500);
        }
    }

    private static TimeSpan Pausa(int intento, TimeSpan? retryAfter)
    {
        var p = retryAfter ?? TimeSpan.FromTicks(PausaBase.Ticks * (1L << (intento - 1)));
        return p > TimeSpan.FromSeconds(30) ? TimeSpan.FromSeconds(30) : p;
    }

    private async Task<JsonDocument> GetJsonAsync(string url, CancellationToken ct)
    {
        using var r = await EnviaAsync(() => new HttpRequestMessage(HttpMethod.Get, url), ct);
        return JsonDocument.Parse(await r.Content.ReadAsStringAsync(ct));
    }

    // ───────── carpetas ─────────
    public async Task<string?> ResolverCarpetaAsync(string ruta, CancellationToken ct)
    {
        var partes = ruta.Split(new[] { '/', '\\' }, StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        if (partes.Length == 0) return null;
        string? id = null;
        for (var i = 0; i < partes.Length; i++)
        {
            var url = id is null ? $"{Usuario}/mailFolders?$top=100&$select=id,displayName" : $"{Usuario}/mailFolders/{Uri.EscapeDataString(id)}/childFolders?$top=100&$select=id,displayName";
            string? hallado = null;
            while (url is not null && hallado is null)
            {
                using var doc = await GetJsonAsync(url, ct);
                foreach (var f in doc.RootElement.GetProperty("value").EnumerateArray())
                    if (string.Equals(f.GetProperty("displayName").GetString(), partes[i], StringComparison.OrdinalIgnoreCase)) { hallado = f.GetProperty("id").GetString(); break; }
                url = doc.RootElement.TryGetProperty("@odata.nextLink", out var nx) ? nx.GetString() : null;
            }
            // «Inbox» en inglés aunque el buzón esté en español: es la carpeta conocida
            if (hallado is null && id is null && string.Equals(partes[i], "inbox", StringComparison.OrdinalIgnoreCase))
            {
                try { using var d = await GetJsonAsync($"{Usuario}/mailFolders/inbox?$select=id", ct); hallado = d.RootElement.GetProperty("id").GetString(); }
                catch (GraphException ex) when (ex.Estado == HttpStatusCode.NotFound) { }
            }
            if (hallado is null) return null;
            id = hallado;
        }
        return id;
    }

    // ───────── correos ─────────
    public async Task<IReadOnlyList<CorreoResumen>> ListarCorreosAsync(string carpetaId, int max, CancellationToken ct)
    {
        var res = new List<CorreoResumen>();
        var url = $"{Usuario}/mailFolders/{Uri.EscapeDataString(carpetaId)}/messages?$top={Math.Min(Math.Max(max, 1), 100)}&$select=id,subject,from,receivedDateTime,hasAttachments&$orderby=receivedDateTime asc";
        while (url is not null && res.Count < max)
        {
            using var doc = await GetJsonAsync(url, ct);
            foreach (var m in doc.RootElement.GetProperty("value").EnumerateArray())
            {
                if (res.Count >= max) break;
                string? de = null;
                if (m.TryGetProperty("from", out var f) && f.ValueKind == JsonValueKind.Object && f.TryGetProperty("emailAddress", out var ea) && ea.TryGetProperty("address", out var ad)) de = ad.GetString();
                DateTimeOffset? rec = m.TryGetProperty("receivedDateTime", out var rd) && rd.ValueKind == JsonValueKind.String ? rd.GetDateTimeOffset() : null;
                res.Add(new CorreoResumen(m.GetProperty("id").GetString()!, m.TryGetProperty("subject", out var s) ? s.GetString() : null, de, rec, m.TryGetProperty("hasAttachments", out var h) && h.GetBoolean()));
            }
            url = doc.RootElement.TryGetProperty("@odata.nextLink", out var nx) ? nx.GetString() : null;
        }
        return res;
    }

    public async Task<IReadOnlyList<AdjuntoResumen>> ListarAdjuntosAsync(string correoId, CancellationToken ct)
    {
        var res = new List<AdjuntoResumen>();
        var url = $"{Usuario}/messages/{Uri.EscapeDataString(correoId)}/attachments?$top=100&$select=id,name,contentType,size,isInline";
        while (url is not null)
        {
            using var doc = await GetJsonAsync(url, ct);
            foreach (var a in doc.RootElement.GetProperty("value").EnumerateArray())
            {
                var tipo = a.TryGetProperty("@odata.type", out var t) ? t.GetString() : null;
                res.Add(new AdjuntoResumen(a.GetProperty("id").GetString()!, a.TryGetProperty("name", out var n) ? n.GetString() ?? "" : "",
                    a.TryGetProperty("size", out var sz) && sz.ValueKind == JsonValueKind.Number ? sz.GetInt64() : 0, a.TryGetProperty("contentType", out var ct2) ? ct2.GetString() : null,
                    EsArchivo: string.Equals(tipo, "#microsoft.graph.fileAttachment", StringComparison.OrdinalIgnoreCase), EsInline: a.TryGetProperty("isInline", out var il) && il.ValueKind == JsonValueKind.True));
            }
            url = doc.RootElement.TryGetProperty("@odata.nextLink", out var nx) ? nx.GetString() : null;
        }
        return res;
    }

    public async Task<byte[]> DescargarAdjuntoAsync(string correoId, string adjuntoId, CancellationToken ct)
    {
        var url = $"{Usuario}/messages/{Uri.EscapeDataString(correoId)}/attachments/{Uri.EscapeDataString(adjuntoId)}/$value";
        using var r = await EnviaAsync(() => new HttpRequestMessage(HttpMethod.Get, url), ct);
        return await r.Content.ReadAsByteArrayAsync(ct);
    }

    public async Task MoverAsync(string correoId, string carpetaDestinoId, CancellationToken ct)
    {
        var url = $"{Usuario}/messages/{Uri.EscapeDataString(correoId)}/move";
        var json = JsonSerializer.Serialize(new { destinationId = carpetaDestinoId });
        using var r = await EnviaAsync(() => new HttpRequestMessage(HttpMethod.Post, url) { Content = new StringContent(json, Encoding.UTF8, "application/json") }, ct);
    }
}
