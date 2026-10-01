using System.Net.Http.Json;
using OpenSource1.Application.Features.FacturasVenta.Borradores.Dtos;
using OpenSource1.Application.Features.FacturasVenta.Calculo;
using OpenSource1.Application.Features.FacturasVenta.Copia;
using OpenSource1.Application.Features.FacturasVenta.Posteadas;
using OpenSource1.Application.Features.FacturasVenta.Posteadas.Dtos;
using OpenSource1.Application.Features.FacturasVenta.Posteo;
using OpenSource1.Core.Common;
using OpenSource1.Core.Enums;

namespace OpenSource1.Blazor.Services;

public sealed class FacturaVentaApiClient(HttpClient httpClient, ILogger<FacturaVentaApiClient> logger) : IFacturaVentaApiClient
{
    private const string BaseRoute = "api/facturas-venta";
    private const string BorradorNoEncontrado = "No se encontró el borrador de factura indicado (puede haber sido posteado o eliminado).";
    private const string LineaNoEncontrada = "No se encontró la línea de factura indicada.";

    public async Task<PagedResult<FacturaVentaBorradorResponse>> ListBorradoresAsync(
        FacturaVentaBorradorFiltro? filtro = null, PageRequest? paginacion = null, CancellationToken cancellationToken = default)
    {
        var parameters = new List<string>();
        AgregarTexto(parameters, "numero", filtro?.Numero);
        AgregarTexto(parameters, "nombreFacturacion", filtro?.NombreFacturacion);
        if (filtro?.SocioNegocioId is { } socioId)
        {
            parameters.Add($"socioId={socioId}");
        }

        if (filtro?.Estado is { } estado)
        {
            parameters.Add($"estado={estado}");
        }

        using var response = await httpClient.GetAsync(ApiRespuestas.ConPaginacion($"{BaseRoute}/borradores", parameters, paginacion), cancellationToken);
        await VentaApiRespuestas.AsegurarExitoAsync(response, "los borradores de factura", logger, cancellationToken);
        return await response.Content.ReadFromJsonAsync<PagedResult<FacturaVentaBorradorResponse>>(cancellationToken)
            ?? PagedResult<FacturaVentaBorradorResponse>.Vacio(paginacion ?? new PageRequest());
    }

    public async Task<long> ContarEnPreparacionAsync(CancellationToken cancellationToken = default) =>
        (await ListBorradoresAsync(null, new PageRequest(1, 1, "CreatedAtUtc", Descendente: true), cancellationToken)).Total;

    public Task<FacturaVentaBorradorResponse?> GetBorradorAsync(Guid id, CancellationToken cancellationToken = default) =>
        VentaApiRespuestas.GetOrNullAsync<FacturaVentaBorradorResponse>(
            httpClient, $"{BaseRoute}/borradores/{id}", "el borrador de factura", logger, cancellationToken);

    public async Task<VentaOperationResult<FacturaVentaBorradorResponse>> CreateBorradorAsync(
        BorradorCabeceraInput input, CancellationToken cancellationToken = default)
    {
        var body = new CreateBorradorBody(
            input.SocioNegocioId, input.SocioNegocioFacturarAId, input.FechaRegistro, input.FechaDocumento, input.FechaVencimiento,
            input.AlmacenId, input.Descripcion, input.SerieBorradorId, input.SerieRegistroId,
            input.PrimeraLinea is { } linea ? Linea(linea) : null);
        using var response = await httpClient.PostAsJsonAsync($"{BaseRoute}/borradores", body, cancellationToken);
        return await VentaApiRespuestas.ToResultAsync<FacturaVentaBorradorResponse>(
            response, "Factura en preparación creada.", BorradorNoEncontrado, "borradores de factura", logger, cancellationToken);
    }

    public async Task<VentaOperationResult<FacturaVentaBorradorResponse>> UpdateBorradorAsync(
        Guid id, BorradorCabeceraInput input, long xmin, CancellationToken cancellationToken = default)
    {
        // Ambos socios SIEMPRE: si el facturar-a no se indica explícitamente, es el mismo que el vender-a (como en el alta).
        var body = new UpdateBorradorBody(
            xmin, input.SocioNegocioId, input.SocioNegocioFacturarAId ?? input.SocioNegocioId, input.FechaRegistro,
            input.FechaDocumento, input.FechaVencimiento, input.AlmacenId, input.Descripcion, input.SerieRegistroId);
        using var response = await httpClient.PutAsJsonAsync($"{BaseRoute}/borradores/{id}", body, cancellationToken);
        return await VentaApiRespuestas.ToResultAsync<FacturaVentaBorradorResponse>(
            response, "Borrador de factura modificado.", BorradorNoEncontrado, "borradores de factura", logger, cancellationToken);
    }

    public async Task<VentaOperationResult<bool>> DeleteBorradorAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.DeleteAsync($"{BaseRoute}/borradores/{id}", cancellationToken);
        return await VentaApiRespuestas.ToPlainResultAsync(
            response, "Borrador de factura eliminado.", BorradorNoEncontrado, "borradores de factura", logger, cancellationToken);
    }

    public Task<VentaOperationResult<FacturaVentaBorradorResponse>> LiberarAsync(Guid id, CancellationToken cancellationToken = default) =>
        CambiarEstadoAsync(id, "liberar", "Borrador liberado.", cancellationToken);

    public Task<VentaOperationResult<FacturaVentaBorradorResponse>> ReabrirAsync(Guid id, CancellationToken cancellationToken = default) =>
        CambiarEstadoAsync(id, "reabrir", "Borrador reabierto.", cancellationToken);

    public async Task<VentaOperationResult<ResultadoPosteoFactura>> PostearAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsync($"{BaseRoute}/borradores/{id}/postear", content: null, cancellationToken);
        return await VentaApiRespuestas.ToResultAsync<ResultadoPosteoFactura>(
            response, "Factura posteada.", BorradorNoEncontrado, "posteo de facturas", logger, cancellationToken);
    }

    public Task<TotalesFactura?> GetTotalesAsync(Guid id, CancellationToken cancellationToken = default) =>
        VentaApiRespuestas.GetOrNullAsync<TotalesFactura>(
            httpClient, $"{BaseRoute}/borradores/{id}/totales", "los totales del borrador", logger, cancellationToken);

    public async Task<IReadOnlyList<LineaFacturaVentaBorradorResponse>?> ListLineasAsync(Guid borradorId, CancellationToken cancellationToken = default) =>
        await VentaApiRespuestas.GetOrNullAsync<List<LineaFacturaVentaBorradorResponse>>(
            httpClient, $"{BaseRoute}/borradores/{borradorId}/lineas", "las líneas del borrador", logger, cancellationToken);

    public Task<LineaFacturaVentaBorradorResponse?> GetLineaAsync(Guid id, CancellationToken cancellationToken = default) =>
        VentaApiRespuestas.GetOrNullAsync<LineaFacturaVentaBorradorResponse>(
            httpClient, $"{BaseRoute}/lineas-borrador/{id}", "la línea de factura", logger, cancellationToken);

    public async Task<VentaOperationResult<LineaFacturaVentaBorradorResponse>> CreateLineaAsync(
        Guid borradorId, LineaFacturaInput input, CancellationToken cancellationToken = default)
    {
        var body = Linea(input);
        using var response = await httpClient.PostAsJsonAsync($"{BaseRoute}/borradores/{borradorId}/lineas", body, cancellationToken);
        return await VentaApiRespuestas.ToResultAsync<LineaFacturaVentaBorradorResponse>(
            response, "Línea agregada.", BorradorNoEncontrado, "líneas de factura", logger, cancellationToken);
    }

    public async Task<VentaOperationResult<LineaFacturaVentaBorradorResponse>> UpdateLineaAsync(
        Guid id, LineaFacturaInput input, long xmin, CancellationToken cancellationToken = default)
    {
        var body = new LineaUpdateBody(
            input.Tipo, xmin, input.ProductoId, input.CuentaContableId, input.Descripcion, input.AlmacenId, input.UnidadMedidaId,
            input.Cantidad, input.PrecioUnitario, input.PorcentajeDescuentoLinea, input.GrupoIvaProductoId);
        using var response = await httpClient.PutAsJsonAsync($"{BaseRoute}/lineas-borrador/{id}", body, cancellationToken);
        return await VentaApiRespuestas.ToResultAsync<LineaFacturaVentaBorradorResponse>(
            response, "Línea modificada.", LineaNoEncontrada, "líneas de factura", logger, cancellationToken);
    }

    public async Task<VentaOperationResult<bool>> DeleteLineaAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.DeleteAsync($"{BaseRoute}/lineas-borrador/{id}", cancellationToken);
        return await VentaApiRespuestas.ToPlainResultAsync(
            response, "Línea eliminada.", LineaNoEncontrada, "líneas de factura", logger, cancellationToken);
    }

    public async Task<PagedResult<FacturaVentaResponse>> ListFacturasAsync(
        FacturaVentaSearchCriteria? filtro = null, PageRequest? paginacion = null, CancellationToken cancellationToken = default)
    {
        var parameters = new List<string>();
        AgregarTexto(parameters, "numero", filtro?.Numero);
        AgregarTexto(parameters, "nombreFacturacion", filtro?.NombreFacturacion);
        if (filtro?.SocioNegocioId is { } socioId)
        {
            parameters.Add($"socioId={socioId}");
        }

        if (filtro?.Desde is { } desde)
        {
            parameters.Add($"desde={desde:yyyy-MM-dd}");
        }

        if (filtro?.Hasta is { } hasta)
        {
            parameters.Add($"hasta={hasta:yyyy-MM-dd}");
        }

        using var response = await httpClient.GetAsync(ApiRespuestas.ConPaginacion(BaseRoute, parameters, paginacion), cancellationToken);
        await VentaApiRespuestas.AsegurarExitoAsync(response, "las facturas de venta", logger, cancellationToken);
        return await response.Content.ReadFromJsonAsync<PagedResult<FacturaVentaResponse>>(cancellationToken)
            ?? PagedResult<FacturaVentaResponse>.Vacio(paginacion ?? new PageRequest());
    }

    public Task<FacturaVentaDetalleResponse?> GetFacturaAsync(string numero, CancellationToken cancellationToken = default) =>
        VentaApiRespuestas.GetOrNullAsync<FacturaVentaDetalleResponse>(
            httpClient, $"{BaseRoute}/{Uri.EscapeDataString(numero)}", "la factura de venta", logger, cancellationToken);

    public async Task<VentaOperationResult<CopiaFacturaResponse>> CopiarABorradorAsync(string numero, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsync($"{BaseRoute}/{Uri.EscapeDataString(numero)}/copiar-a-borrador", content: null, cancellationToken);
        return await VentaApiRespuestas.ToResultAsync<CopiaFacturaResponse>(
            response, "Borrador creado desde la factura.", "No se encontró la factura indicada.", "facturas de venta", logger, cancellationToken);
    }

    private async Task<VentaOperationResult<FacturaVentaBorradorResponse>> CambiarEstadoAsync(
        Guid id, string accion, string successMessage, CancellationToken cancellationToken)
    {
        using var response = await httpClient.PostAsync($"{BaseRoute}/borradores/{id}/{accion}", content: null, cancellationToken);
        return await VentaApiRespuestas.ToResultAsync<FacturaVentaBorradorResponse>(
            response, successMessage, BorradorNoEncontrado, "borradores de factura", logger, cancellationToken);
    }

    private static void AgregarTexto(List<string> parameters, string nombre, string? valor)
    {
        if (!string.IsNullOrWhiteSpace(valor))
        {
            parameters.Add($"{nombre}={Uri.EscapeDataString(valor.Trim())}");
        }
    }

    // Cuerpos de red exactos de FacturasVentaController (Blazor no referencia el proyecto Api).
    private static LineaBody Linea(LineaFacturaInput input) => new(
        input.Tipo, input.ProductoId, input.CuentaContableId, input.Descripcion, input.AlmacenId, input.UnidadMedidaId,
        input.Cantidad, input.PrecioUnitario, input.PorcentajeDescuentoLinea, input.GrupoIvaProductoId);

    private sealed record CreateBorradorBody(
        Guid SocioNegocioId, Guid? SocioNegocioFacturarAId, DateOnly? FechaRegistro, DateOnly? FechaDocumento,
        DateOnly? FechaVencimiento, Guid? AlmacenId, string? Descripcion, Guid? SerieBorradorId, Guid? SerieRegistroId, LineaBody? PrimeraLinea);

    private sealed record UpdateBorradorBody(
        long Xmin, Guid SocioNegocioId, Guid SocioNegocioFacturarAId, DateOnly? FechaRegistro, DateOnly? FechaDocumento,
        DateOnly? FechaVencimiento, Guid? AlmacenId, string? Descripcion, Guid? SerieRegistroId);

    private sealed record LineaBody(
        TipoLineaFactura Tipo, Guid? ProductoId, Guid? CuentaContableId, string? Descripcion, Guid? AlmacenId, Guid? UnidadMedidaId,
        decimal? Cantidad, decimal? PrecioUnitario, decimal? PorcentajeDescuentoLinea, Guid? GrupoIvaProductoId);

    private sealed record LineaUpdateBody(
        TipoLineaFactura Tipo, long Xmin, Guid? ProductoId, Guid? CuentaContableId, string? Descripcion, Guid? AlmacenId,
        Guid? UnidadMedidaId, decimal? Cantidad, decimal? PrecioUnitario, decimal? PorcentajeDescuentoLinea, Guid? GrupoIvaProductoId);
}
