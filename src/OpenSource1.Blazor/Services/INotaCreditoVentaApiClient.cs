using OpenSource1.Application.Features.FacturasVenta.Calculo;
using OpenSource1.Application.Features.NotasCreditoVenta.Borradores.Dtos;
using OpenSource1.Application.Features.NotasCreditoVenta.Posteadas;
using OpenSource1.Application.Features.NotasCreditoVenta.Posteadas.Dtos;
using OpenSource1.Application.Features.NotasCreditoVenta.Posteo;
using OpenSource1.Core.Common;

namespace OpenSource1.Blazor.Services;

/// <summary>
/// Cliente tipado de <c>api/notas-credito-venta</c> (Task 8.7): borradores (cabecera, líneas, líneas acreditables, vista previa
/// de totales y posteo) y notas posteadas. Mismo patrón que <see cref="IFacturaVentaApiClient"/>: los 400/409/422 traen los
/// mensajes reales de la API (con "Línea n:" en los errores de línea) que se devuelven tal cual.
/// </summary>
public interface INotaCreditoVentaApiClient
{
    Task<PagedResult<NotaCreditoVentaBorradorResponse>> ListBorradoresAsync(
        NotaCreditoVentaBorradorFiltro? filtro = null, PageRequest? paginacion = null, CancellationToken cancellationToken = default);

    /// <summary>
    /// Notas en preparación (borradores Abierta) para la pestaña "En preparación (n)": el <c>Total</c> de una página de tamaño 1 del
    /// listado existente. Un error de la API lanza <see cref="HttpRequestException"/> (la página muestra la pestaña sin número).
    /// </summary>
    Task<long> ContarEnPreparacionAsync(CancellationToken cancellationToken = default);

    /// <summary><see langword="null"/> = el borrador no existe (404).</summary>
    Task<NotaCreditoVentaBorradorResponse?> GetBorradorAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Crea un borrador desde una factura posteada (CanAdd). Con <c>CopiarLineas</c>, con todas sus líneas pendientes.</summary>
    Task<VentaOperationResult<NotaCreditoVentaBorradorResponse>> CreateBorradorAsync(
        NotaCreditoBorradorInput input, CancellationToken cancellationToken = default);

    /// <summary>Fechas, descripción y serie de registro ("null = conservar"; descripción "" = limpiar). CanModify; solo Abierta (Posteada = 409).</summary>
    Task<VentaOperationResult<NotaCreditoVentaBorradorResponse>> UpdateBorradorAsync(
        Guid id, NotaCreditoCabeceraInput input, long xmin, CancellationToken cancellationToken = default);

    Task<VentaOperationResult<bool>> DeleteBorradorAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Posteo (CanModify en la API; un Ejecutor recibe 403). Éxito: número NC, total, lo aplicado y el registro contable.</summary>
    Task<VentaOperationResult<ResultadoPosteoNotaCredito>> PostearAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary>Vista previa con los topes de lo ya acreditado. <see langword="null"/> = el borrador no existe (404).</summary>
    Task<TotalesFactura?> GetTotalesAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary><see langword="null"/> = el borrador no existe (404).</summary>
    Task<IReadOnlyList<LineaFacturaAcreditableResponse>?> ListLineasAcreditablesAsync(Guid id, CancellationToken cancellationToken = default);

    /// <summary><see langword="null"/> = el borrador no existe (404).</summary>
    Task<IReadOnlyList<LineaNotaCreditoVentaBorradorResponse>?> ListLineasAsync(Guid borradorId, CancellationToken cancellationToken = default);

    Task<LineaNotaCreditoVentaBorradorResponse?> GetLineaAsync(Guid id, CancellationToken cancellationToken = default);

    Task<VentaOperationResult<LineaNotaCreditoVentaBorradorResponse>> CreateLineaAsync(
        Guid borradorId, long lineaFacturaVentaId, decimal? cantidad, bool devolverInventario, CancellationToken cancellationToken = default);

    Task<VentaOperationResult<LineaNotaCreditoVentaBorradorResponse>> UpdateLineaAsync(
        Guid id, decimal? cantidad, bool devolverInventario, long xmin, CancellationToken cancellationToken = default);

    Task<VentaOperationResult<bool>> DeleteLineaAsync(Guid id, CancellationToken cancellationToken = default);

    Task<PagedResult<NotaCreditoVentaResponse>> ListNotasAsync(
        NotaCreditoVentaSearchCriteria? filtro = null, PageRequest? paginacion = null, CancellationToken cancellationToken = default);

    /// <summary><see langword="null"/> = no existe una nota posteada con ese número (404).</summary>
    Task<NotaCreditoVentaDetalleResponse?> GetNotaAsync(string numero, CancellationToken cancellationToken = default);
}

/// <summary>Filtros del listado de borradores de nota de crédito (<c>Estado</c> como entero: 1 = Abierta, 3 = Posteada; null = Abierta).</summary>
public sealed record NotaCreditoVentaBorradorFiltro(
    string? Numero, string? FacturaVentaNumero, string? NombreFacturacion, Guid? SocioNegocioId, int? Estado = null);

/// <summary>
/// Alta de un borrador de nota: la factura posteada es obligatoria; fechas null = valores por defecto de la API; series null = las
/// configuradas (<c>SerieBorradorId</c> numera el borrador, <c>SerieRegistroId</c> numerará la nota al postear).
/// </summary>
public sealed record NotaCreditoBorradorInput(
    string FacturaVentaNumero,
    DateOnly? FechaRegistro,
    DateOnly? FechaDocumento,
    string? Descripcion,
    bool CopiarLineas,
    bool DevolverInventario,
    Guid? SerieBorradorId = null,
    Guid? SerieRegistroId = null);

/// <summary>Modificación de la cabecera de un borrador de nota: todo "null = conservar" (<c>Descripcion</c> "" = limpiar).</summary>
public sealed record NotaCreditoCabeceraInput(DateOnly? FechaRegistro, DateOnly? FechaDocumento, string? Descripcion, Guid? SerieRegistroId = null);
