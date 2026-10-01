using OpenSource1.Application.Features.FacturasVenta.Borradores.Dtos;
using OpenSource1.Application.Features.FacturasVenta.Calculo;
using OpenSource1.Application.Features.FacturasVenta.Copia;
using OpenSource1.Application.Features.FacturasVenta.Posteadas;
using OpenSource1.Application.Features.FacturasVenta.Posteadas.Dtos;
using OpenSource1.Application.Features.FacturasVenta.Posteo;
using OpenSource1.Core.Common;
using OpenSource1.Core.Enums;

namespace OpenSource1.Blazor.Services;

/// <summary>
/// Cliente tipado de <c>api/facturas-venta</c> (Task 6.6): borradores (cabecera, líneas, totales, liberar/reabrir y posteo) y
/// facturas posteadas. Mismo patrón que <see cref="IDiarioInventarioApiClient"/>: los 400/409/422 traen mensajes reales (texto
/// seguro, en español, con el número de línea ya incluido en los errores de posteo) que se devuelven tal cual.
/// </summary>
public interface IFacturaVentaApiClient
{
    Task<PagedResult<FacturaVentaBorradorResponse>> ListBorradoresAsync(
        FacturaVentaBorradorFiltro? filtro = null, PageRequest? paginacion = null, CancellationToken cancellationToken = default);

    Task<FacturaVentaBorradorResponse?> GetBorradorAsync(Guid id, CancellationToken cancellationToken = default);

    Task<VentaOperationResult<FacturaVentaBorradorResponse>> CreateBorradorAsync(BorradorCabeceraInput input, CancellationToken cancellationToken = default);

    /// <summary>
    /// Envía SIEMPRE ambos socios: la API usa "null = conservar" y cambiar solo el vender-a mantendría el facturar-a anterior.
    /// <c>FechaVencimiento</c> null = conservar (o recalcular si cambia la fecha de documento); <c>Descripcion</c> "" = limpiar.
    /// </summary>
    Task<VentaOperationResult<FacturaVentaBorradorResponse>> UpdateBorradorAsync(
        Guid id, BorradorCabeceraInput input, long xmin, CancellationToken cancellationToken = default);

    Task<VentaOperationResult<bool>> DeleteBorradorAsync(Guid id, CancellationToken cancellationToken = default);

    Task<VentaOperationResult<FacturaVentaBorradorResponse>> LiberarAsync(Guid id, CancellationToken cancellationToken = default);

    Task<VentaOperationResult<FacturaVentaBorradorResponse>> ReabrirAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Posteo (CanModify en la API; un Ejecutor recibe 403). Éxito: número FV, total y registro contable.</summary>
    Task<VentaOperationResult<ResultadoPosteoFactura>> PostearAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary><see langword="null"/> = el borrador no existe (404).</summary>
    Task<TotalesFactura?> GetTotalesAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary><see langword="null"/> = el borrador no existe (404).</summary>
    Task<IReadOnlyList<LineaFacturaVentaBorradorResponse>?> ListLineasAsync(Guid borradorId, CancellationToken cancellationToken = default);

    Task<LineaFacturaVentaBorradorResponse?> GetLineaAsync(Guid id, CancellationToken cancellationToken = default);

    Task<VentaOperationResult<LineaFacturaVentaBorradorResponse>> CreateLineaAsync(
        Guid borradorId, LineaFacturaInput input, CancellationToken cancellationToken = default);

    Task<VentaOperationResult<LineaFacturaVentaBorradorResponse>> UpdateLineaAsync(
        Guid id, LineaFacturaInput input, long xmin, CancellationToken cancellationToken = default);

    Task<VentaOperationResult<bool>> DeleteLineaAsync(Guid id, CancellationToken cancellationToken = default);

    Task<PagedResult<FacturaVentaResponse>> ListFacturasAsync(
        FacturaVentaSearchCriteria? filtro = null, PageRequest? paginacion = null, CancellationToken cancellationToken = default);

    /// <summary><see langword="null"/> = no existe una factura posteada con ese número (404).</summary>
    Task<FacturaVentaDetalleResponse?> GetFacturaAsync(string numero, CancellationToken cancellationToken = default);

    /// <summary>
    /// Copia una factura posteada a un borrador nuevo (CanAdd). Éxito: id y número del borrador y avisos (líneas omitidas o
    /// almacén sustituido). 404 = la factura no existe; 400 = cliente bloqueado/borrado o numeración.
    /// </summary>
    Task<VentaOperationResult<CopiaFacturaResponse>> CopiarABorradorAsync(string numero, CancellationToken cancellationToken = default);
}

/// <summary>Filtros del listado de borradores (<c>Estado</c> como entero: 1 = Abierta, 2 = Liberada, 3 = Posteada; null = Abierta y Liberada).</summary>
public sealed record FacturaVentaBorradorFiltro(string? Numero, string? NombreFacturacion, Guid? SocioNegocioId, int? Estado);

/// <summary>
/// Cabecera de un borrador. En el alta, <c>SocioNegocioFacturarAId</c> null = el mismo que el vender-a y las fechas/almacén null =
/// valores por defecto de la API. En la modificación la página envía siempre ambos socios.
/// Series (spec no-series): en el alta, null = la serie configurada del tipo (<c>SerieBorradorId</c> numera el borrador,
/// <c>SerieRegistroId</c> numerará la factura al postear); en la modificación solo viaja <c>SerieRegistroId</c> (null = conservar).
/// <c>PrimeraLinea</c> (solo en el alta) — alta rápida: primera línea guardada con la cabecera en una transacción; Blazor siempre la envía.
/// </summary>
public sealed record BorradorCabeceraInput(
    Guid SocioNegocioId,
    Guid? SocioNegocioFacturarAId,
    DateOnly? FechaRegistro,
    DateOnly? FechaDocumento,
    DateOnly? FechaVencimiento,
    Guid? AlmacenId,
    string? Descripcion,
    Guid? SerieBorradorId = null,
    Guid? SerieRegistroId = null,
    LineaFacturaInput? PrimeraLinea = null);

/// <summary>
/// Cuerpo de alta/modificación de una línea (reemplazo completo). Los campos que no aplican al <see cref="Tipo"/> deben ir nulos:
/// la API los rechaza (<c>factura.campo_no_aplica</c>). Lo garantiza <c>LineaFacturaForm.ToInput</c>.
/// </summary>
public sealed record LineaFacturaInput(
    TipoLineaFactura Tipo,
    Guid? ProductoId,
    Guid? CuentaContableId,
    string? Descripcion,
    Guid? AlmacenId,
    Guid? UnidadMedidaId,
    decimal? Cantidad,
    decimal? PrecioUnitario,
    decimal? PorcentajeDescuentoLinea,
    Guid? GrupoIvaProductoId);

/// <summary>Resultado de una operación de ventas. <c>Errors</c> son los mensajes reales de un 400 (todos, con su número de línea).</summary>
public sealed record VentaOperationResult<T>(bool Succeeded, string Message, T? Valor = default, IReadOnlyList<string>? Errors = null);
