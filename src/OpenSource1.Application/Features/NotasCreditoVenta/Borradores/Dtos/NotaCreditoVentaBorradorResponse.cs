using OpenSource1.Core.Enums;

namespace OpenSource1.Application.Features.NotasCreditoVenta.Borradores.Dtos;

/// <summary>
/// Cabecera de un borrador de nota de crédito, con los códigos/nombres resueltos por JOIN, el total de la factura y el conteo de
/// líneas vivas. <see cref="Xmin"/> es el token de concurrencia optimista que el cliente reenvía en el PUT.
/// </summary>
public sealed class NotaCreditoVentaBorradorResponse
{
    public Guid Id { get; init; }
    public string Numero { get; init; } = string.Empty;
    public string FacturaVentaNumero { get; init; } = string.Empty;
    public decimal FacturaImporteTotal { get; init; }
    public DateOnly FacturaFechaRegistro { get; init; }
    public Guid SocioNegocioId { get; init; }
    public string? SocioNegocioCodigo { get; init; }
    public string? SocioNegocioNombre { get; init; }
    public Guid SocioNegocioFacturarAId { get; init; }
    public string? SocioNegocioFacturarACodigo { get; init; }
    public string NombreFacturacion { get; init; } = string.Empty;
    public string? RazonSocialFacturacion { get; init; }
    public TipoDocumentoFiscal TipoDocumentoFiscal { get; init; }
    public string? NumeroDocumentoFiscal { get; init; }
    public string? DireccionFacturacionLinea1 { get; init; }
    public string? DireccionFacturacionLinea2 { get; init; }
    public string? CiudadFacturacion { get; init; }
    public string? PaisCodigoFacturacion { get; init; }
    public DateOnly FechaRegistro { get; init; }
    public DateOnly FechaDocumento { get; init; }
    public Guid GrupoNegocioId { get; init; }
    public Guid GrupoIvaNegocioId { get; init; }
    public Guid GrupoClienteContableId { get; init; }
    public Guid? CuentaCxCId { get; init; }
    public string? CuentaCxCNumero { get; init; }
    public string Moneda { get; init; } = string.Empty;
    public string? Descripcion { get; init; }
    public int NumeroLineas { get; init; }

    /// <summary>Total con ITBIS de las líneas guardadas (vista previa de las listas, sin los topes de lo ya acreditado).</summary>
    public decimal ImporteTotal { get; init; }

    /// <summary>Abierta (editable) o Posteada (solo lectura, enlazada a <see cref="NotaCreditoVentaNumero"/>).</summary>
    public EstadoNotaCreditoBorrador Estado { get; init; }

    /// <summary>Serie que dio <see cref="Numero"/> (tipo BorradorNotaCreditoVenta).</summary>
    public Guid SerieBorradorId { get; init; }
    public string? SerieBorradorCodigo { get; init; }

    /// <summary>Serie con la que se numera la nota al postear (tipo NotaCreditoVenta).</summary>
    public Guid SerieRegistroId { get; init; }
    public string? SerieRegistroCodigo { get; init; }

    /// <summary>Nota posteada desde este borrador (solo en Posteada).</summary>
    public string? NotaCreditoVentaNumero { get; init; }

    public long Xmin { get; init; }
    public DateTime CreatedAtUtc { get; init; }
    public DateTime? UpdatedAtUtc { get; init; }
    public string CreatedBy { get; init; } = string.Empty;
    public string? UpdatedBy { get; init; }
}
