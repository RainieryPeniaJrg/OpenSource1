using System.Net.Http.Json;
using OpenSource1.Application.Features.FacturasVenta.Calculo;
using OpenSource1.Application.Features.NotasCreditoVenta.Borradores.Dtos;
using OpenSource1.Application.Features.NotasCreditoVenta.Posteadas;
using OpenSource1.Application.Features.NotasCreditoVenta.Posteadas.Dtos;
using OpenSource1.Application.Features.NotasCreditoVenta.Posteo;
using OpenSource1.Core.Common;

namespace OpenSource1.Blazor.Services;

public sealed class NotaCreditoVentaApiClient(HttpClient httpClient, ILogger<NotaCreditoVentaApiClient> logger) : INotaCreditoVentaApiClient
{
    private const string BaseRoute = "api/notas-credito-venta";
    private const string BorradorNoEncontrado = "No se encontró el borrador de nota de crédito indicado (puede haber sido posteado o eliminado).";
    private const string LineaNoEncontrada = "No se encontró la línea de nota de crédito indicada (puede haber sido eliminada).";
    private const string Entidad = "notas de crédito";

    public async Task<PagedResult<NotaCreditoVentaBorradorResponse>> ListBorradoresAsync(
        NotaCreditoVentaBorradorFiltro? filtro = null, PageRequest? paginacion = null, CancellationToken cancellationToken = default)
    {
        var parameters = new List<string>();
        AgregarTexto(parameters, "numero", filtro?.Numero);
        AgregarTexto(parameters, "facturaVentaNumero", filtro?.FacturaVentaNumero);
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
        await VentaApiRespuestas.AsegurarExitoAsync(response, "los borradores de nota de crédito", logger, cancellationToken);
        return await response.Content.ReadFromJsonAsync<PagedResult<NotaCreditoVentaBorradorResponse>>(cancellationToken)
            ?? PagedResult<NotaCreditoVentaBorradorResponse>.Vacio(paginacion ?? new PageRequest());
    }

    public async Task<long> ContarEnPreparacionAsync(CancellationToken cancellationToken = default) =>
        (await ListBorradoresAsync(null, new PageRequest(1, 1, "CreatedAtUtc", Descendente: true), cancellationToken)).Total;

    public Task<NotaCreditoVentaBorradorResponse?> GetBorradorAsync(Guid id, CancellationToken cancellationToken = default) =>
        VentaApiRespuestas.GetOrNullAsync<NotaCreditoVentaBorradorResponse>(
            httpClient, $"{BaseRoute}/borradores/{id}", "el borrador de nota de crédito", logger, cancellationToken);

    public async Task<VentaOperationResult<NotaCreditoVentaBorradorResponse>> CreateBorradorAsync(
        NotaCreditoBorradorInput input, CancellationToken cancellationToken = default)
    {
        var body = new CreateBorradorBody(
            input.FacturaVentaNumero, input.FechaRegistro, input.FechaDocumento, input.Descripcion, input.CopiarLineas, input.DevolverInventario,
            input.SerieBorradorId, input.SerieRegistroId);
        using var response = await httpClient.PostAsJsonAsync($"{BaseRoute}/borradores", body, cancellationToken);
        return await VentaApiRespuestas.ToResultAsync<NotaCreditoVentaBorradorResponse>(
            response, "Borrador de nota de crédito creado.", BorradorNoEncontrado, Entidad, logger, cancellationToken);
    }

    public async Task<VentaOperationResult<NotaCreditoVentaBorradorResponse>> UpdateBorradorAsync(
        Guid id, NotaCreditoCabeceraInput input, long xmin, CancellationToken cancellationToken = default)
    {
        var body = new UpdateBorradorBody(xmin, input.FechaRegistro, input.FechaDocumento, input.Descripcion, input.SerieRegistroId);
        using var response = await httpClient.PutAsJsonAsync($"{BaseRoute}/borradores/{id}", body, cancellationToken);
        return await VentaApiRespuestas.ToResultAsync<NotaCreditoVentaBorradorResponse>(
            response, "Borrador de nota de crédito modificado.", BorradorNoEncontrado, Entidad, logger, cancellationToken);
    }

    public async Task<VentaOperationResult<bool>> DeleteBorradorAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.DeleteAsync($"{BaseRoute}/borradores/{id}", cancellationToken);
        return await VentaApiRespuestas.ToPlainResultAsync(
            response, "Borrador de nota de crédito eliminado.", BorradorNoEncontrado, Entidad, logger, cancellationToken);
    }

    public async Task<VentaOperationResult<ResultadoPosteoNotaCredito>> PostearAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsync($"{BaseRoute}/borradores/{id}/postear", content: null, cancellationToken);
        return await VentaApiRespuestas.ToResultAsync<ResultadoPosteoNotaCredito>(
            response, "Nota de crédito posteada.", BorradorNoEncontrado, "posteo de notas de crédito", logger, cancellationToken);
    }

    public Task<TotalesFactura?> GetTotalesAsync(Guid id, CancellationToken cancellationToken = default) =>
        VentaApiRespuestas.GetOrNullAsync<TotalesFactura>(
            httpClient, $"{BaseRoute}/borradores/{id}/totales", "los totales del borrador de nota", logger, cancellationToken);

    public async Task<IReadOnlyList<LineaFacturaAcreditableResponse>?> ListLineasAcreditablesAsync(Guid id, CancellationToken cancellationToken = default) =>
        await VentaApiRespuestas.GetOrNullAsync<List<LineaFacturaAcreditableResponse>>(
            httpClient, $"{BaseRoute}/borradores/{id}/lineas-acreditables", "las líneas acreditables de la factura", logger, cancellationToken);

    public async Task<IReadOnlyList<LineaNotaCreditoVentaBorradorResponse>?> ListLineasAsync(Guid borradorId, CancellationToken cancellationToken = default) =>
        await VentaApiRespuestas.GetOrNullAsync<List<LineaNotaCreditoVentaBorradorResponse>>(
            httpClient, $"{BaseRoute}/borradores/{borradorId}/lineas", "las líneas del borrador de nota", logger, cancellationToken);

    public Task<LineaNotaCreditoVentaBorradorResponse?> GetLineaAsync(Guid id, CancellationToken cancellationToken = default) =>
        VentaApiRespuestas.GetOrNullAsync<LineaNotaCreditoVentaBorradorResponse>(
            httpClient, $"{BaseRoute}/lineas-borrador/{id}", "la línea de nota de crédito", logger, cancellationToken);

    public async Task<VentaOperationResult<LineaNotaCreditoVentaBorradorResponse>> CreateLineaAsync(
        Guid borradorId, long lineaFacturaVentaId, decimal? cantidad, bool devolverInventario, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PostAsJsonAsync(
            $"{BaseRoute}/borradores/{borradorId}/lineas", new CreateLineaBody(lineaFacturaVentaId, cantidad, devolverInventario), cancellationToken);
        return await VentaApiRespuestas.ToResultAsync<LineaNotaCreditoVentaBorradorResponse>(
            response, "Línea agregada.", BorradorNoEncontrado, Entidad, logger, cancellationToken);
    }

    public async Task<VentaOperationResult<LineaNotaCreditoVentaBorradorResponse>> UpdateLineaAsync(
        Guid id, decimal? cantidad, bool devolverInventario, long xmin, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.PutAsJsonAsync(
            $"{BaseRoute}/lineas-borrador/{id}", new UpdateLineaBody(xmin, cantidad, devolverInventario), cancellationToken);
        return await VentaApiRespuestas.ToResultAsync<LineaNotaCreditoVentaBorradorResponse>(
            response, "Línea modificada.", LineaNoEncontrada, Entidad, logger, cancellationToken);
    }

    public async Task<VentaOperationResult<bool>> DeleteLineaAsync(Guid id, CancellationToken cancellationToken = default)
    {
        using var response = await httpClient.DeleteAsync($"{BaseRoute}/lineas-borrador/{id}", cancellationToken);
        return await VentaApiRespuestas.ToPlainResultAsync(response, "Línea eliminada.", LineaNoEncontrada, Entidad, logger, cancellationToken);
    }

    public async Task<PagedResult<NotaCreditoVentaResponse>> ListNotasAsync(
        NotaCreditoVentaSearchCriteria? filtro = null, PageRequest? paginacion = null, CancellationToken cancellationToken = default)
    {
        var parameters = new List<string>();
        AgregarTexto(parameters, "numero", filtro?.Numero);
        AgregarTexto(parameters, "facturaVentaNumero", filtro?.FacturaVentaNumero);
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
        await VentaApiRespuestas.AsegurarExitoAsync(response, "las notas de crédito", logger, cancellationToken);
        return await response.Content.ReadFromJsonAsync<PagedResult<NotaCreditoVentaResponse>>(cancellationToken)
            ?? PagedResult<NotaCreditoVentaResponse>.Vacio(paginacion ?? new PageRequest());
    }

    public Task<NotaCreditoVentaDetalleResponse?> GetNotaAsync(string numero, CancellationToken cancellationToken = default) =>
        VentaApiRespuestas.GetOrNullAsync<NotaCreditoVentaDetalleResponse>(
            httpClient, $"{BaseRoute}/{Uri.EscapeDataString(numero)}", "la nota de crédito", logger, cancellationToken);

    private static void AgregarTexto(List<string> parameters, string nombre, string? valor)
    {
        if (!string.IsNullOrWhiteSpace(valor))
        {
            parameters.Add($"{nombre}={Uri.EscapeDataString(valor.Trim())}");
        }
    }

    // Cuerpos de red exactos de NotasCreditoVentaController (Blazor no referencia el proyecto Api).
    private sealed record CreateBorradorBody(
        string FacturaVentaNumero, DateOnly? FechaRegistro, DateOnly? FechaDocumento, string? Descripcion, bool CopiarLineas, bool DevolverInventario,
        Guid? SerieBorradorId, Guid? SerieRegistroId);

    private sealed record UpdateBorradorBody(
        long Xmin, DateOnly? FechaRegistro, DateOnly? FechaDocumento, string? Descripcion, Guid? SerieRegistroId);

    private sealed record CreateLineaBody(long LineaFacturaVentaId, decimal? Cantidad, bool DevolverInventario);

    private sealed record UpdateLineaBody(long Xmin, decimal? Cantidad, bool DevolverInventario);
}
