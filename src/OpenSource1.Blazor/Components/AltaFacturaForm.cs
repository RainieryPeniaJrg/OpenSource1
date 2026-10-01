using System.ComponentModel.DataAnnotations;
using OpenSource1.Blazor.Services;
using OpenSource1.Core.Enums;

namespace OpenSource1.Blazor.Components;

/// <summary>
/// Alta rápida de una factura (spec alta rápida, Parte 1): cliente, fechas, vencimiento y descripción del documento y la PRIMERA línea en
/// un único formulario Static SSR. Hereda los campos de <see cref="LineaFacturaForm"/> (mismos <c>name</c> que la fila de alta del
/// documento, para usar <see cref="LineaFacturaCampos"/>); la descripción de la cabecera es <see cref="DescripcionDocumento"/> porque
/// <c>Descripcion</c> es la de la línea. El almacén no se envía (lo fija el servidor) y la serie de registro solo la del Administrador.
/// </summary>
public sealed class AltaFacturaForm : LineaFacturaForm
{
    [Required(ErrorMessage = "Elija el cliente.")]
    public Guid? SocioNegocioId { get; set; }

    public string? FechaRegistroTexto { get; set; }

    public string? FechaDocumentoTexto { get; set; }

    public string? FechaVencimientoTexto { get; set; }

    [MaxLength(200, ErrorMessage = "La descripción admite como máximo 200 caracteres.")]
    public string? DescripcionDocumento { get; set; }

    /// <summary>Solo el Administrador ("Más opciones"); null = la configurada.</summary>
    public Guid? SerieRegistroId { get; set; }

    public DateOnly? FechaRegistro => Fecha(FechaRegistroTexto);

    public DateOnly? FechaDocumento => Fecha(FechaDocumentoTexto);

    public DateOnly? FechaVencimiento => Fecha(FechaVencimientoTexto);

    public override IEnumerable<ValidationResult> Validate(ValidationContext validationContext)
    {
        foreach (var resultado in base.Validate(validationContext))
        {
            yield return resultado;
        }

        foreach (var (texto, campo, etiqueta) in new[]
                 {
                     (FechaRegistroTexto, nameof(FechaRegistroTexto), "La fecha de registro"),
                     (FechaDocumentoTexto, nameof(FechaDocumentoTexto), "La fecha de documento"),
                     (FechaVencimientoTexto, nameof(FechaVencimientoTexto), "La fecha de vencimiento"),
                 })
        {
            if (!string.IsNullOrWhiteSpace(texto) && !EntradaFecha.TryParse(texto, out _, out var error, etiqueta))
            {
                yield return new ValidationResult(error ?? $"{etiqueta} no es válida.", [campo]);
            }
        }
    }

    /// <summary>Cabecera + primera línea hacia la API (spec alta rápida): sin almacén; serie de registro solo si <paramref name="esAdministrador"/>.</summary>
    public BorradorCabeceraInput ToAlta(TipoLineaFactura tipo, Guid? productoId, Guid? unidadMedidaId, bool esAdministrador) =>
        new(
            SocioNegocioId ?? Guid.Empty, null, FechaRegistro, FechaDocumento, FechaVencimiento, null,
            string.IsNullOrWhiteSpace(DescripcionDocumento) ? null : DescripcionDocumento.Trim(),
            SerieBorradorId: null,
            SerieRegistroId: esAdministrador ? SerieRegistroId : null,
            PrimeraLinea: ToInput(tipo, productoId, unidadMedidaId));

    private static DateOnly? Fecha(string? texto) =>
        !string.IsNullOrWhiteSpace(texto) && EntradaFecha.TryParse(texto, out var valor, out _) ? valor : null;
}
