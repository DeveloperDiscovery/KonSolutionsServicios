namespace KonSolutions.XmlRecibidos;

/// <summary>Solo lo que el servicio lee de ESTANDAR.CONFIGURACION_GENERAL (los demas campos que devuelve el procedimiento se ignoran).</summary>
public class ConfiguracionGeneral
{
    public string? COD_KEY_PATHFILE_COMPRAS_XML { get; set; }
    public string? DES_CARPETA_COMPRAS_PROCESADOS { get; set; }
    public string? DES_CARPETA_COMPRAS_ERRORES { get; set; }
    public bool? FLG_XML_RECIBIDOS_ACTIVO { get; set; }
    public int? CAN_MINUTOS_XML_RECIBIDOS { get; set; }
    public string? DES_CARPETA_CORREO_RECEPCION { get; set; }
    public string? DES_CARPETA_CORREO_PROCESADOS { get; set; }
    public string? DES_CARPETA_CORREO_ERRORES { get; set; }
}
