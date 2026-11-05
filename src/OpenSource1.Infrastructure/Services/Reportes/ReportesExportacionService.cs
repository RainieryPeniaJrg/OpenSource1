using Dapper;
using OpenSource1.Application.Data;
using OpenSource1.Application.Features.MovimientosCliente.Dtos;
using OpenSource1.Application.Features.Reportes;
using OpenSource1.Core.Common;

namespace OpenSource1.Infrastructure.Services.Reportes;

/// <summary>
/// Pages through a report within one repeatable-read, read-only PostgreSQL transaction.
/// Concurrent postings cannot shift rows or replace the totals halfway through an export.
/// </summary>
public sealed class ReportesExportacionService(IDbSession session, IReportesReadRepository repository) : IReportesExportacion
{
    public Task<Result<ReporteResponse<VentaReporteFila>>> VentasAsync(ReporteCriterios criterios, CancellationToken ct = default) =>
        ExportarAsync(criterios, "ventas", repository.VentasAsync, ct);

    public Task<Result<ReporteResponse<InventarioReporteFila>>> InventarioAsync(ReporteCriterios criterios, CancellationToken ct = default) =>
        ExportarAsync(criterios, "inventario", repository.InventarioAsync, ct);

    public Task<Result<ReporteResponse<TopProductoReporteFila>>> TopProductosAsync(ReporteCriterios criterios, CancellationToken ct = default) =>
        ExportarAsync(criterios, "top-productos", repository.TopProductosAsync, ct);

    public Task<Result<ReporteResponse<EstadoCuentaClienteResponse>>> CuentasPorCobrarAsync(ReporteCriterios criterios, CancellationToken ct = default) =>
        ExportarAsync(criterios, "cuentas-por-cobrar", repository.CuentasPorCobrarAsync, ct);

    private async Task<Result<ReporteResponse<T>>> ExportarAsync<T>(ReporteCriterios criterios, string tipo,
        Func<ReporteCriterios, CancellationToken, Task<ReporteResponse<T>>> consulta, CancellationToken ct)
    {
        if (session.HayTransaccionActiva)
            throw new InvalidOperationException("La exportación requiere una transacción propia para garantizar su instantánea.");

        // Resolve the cutoff once before validation and before the first report query.
        criterios = criterios with { Hasta = criterios.Hasta ?? DateOnly.FromDateTime(DateTime.UtcNow) };
        var errores = ReportesValidacion.Validar(criterios, tipo);
        if (errores.Count != 0) return Result<ReporteResponse<T>>.Fallo([.. errores]);

        await using var transaccion = await session.BeginTransactionAsync(ct);
        await session.Connection.ExecuteAsync(new CommandDefinition(
            "SET TRANSACTION ISOLATION LEVEL REPEATABLE READ READ ONLY", transaction: session.CurrentTransaction, cancellationToken: ct));

        criterios = criterios with { Pagina = 1, TamanoPagina = PageRequest.TamanoMaximo };
        var primera = await consulta(criterios, ct);
        var filas = new List<T>(primera.Pagina.Items);
        for (var pagina = 2; pagina <= primera.Pagina.TotalPaginas; pagina++)
        {
            var siguiente = await consulta(criterios with { Pagina = pagina }, ct);
            filas.AddRange(siguiente.Pagina.Items);
        }

        if (filas.Count != primera.Pagina.Total)
            return Result<ReporteResponse<T>>.Fallo(new Error("reporte.exportacion_incompleta.conflicto",
                "No fue posible obtener todos los registros del reporte. Intente nuevamente."));

        await session.CommitAsync(ct);
        return Result<ReporteResponse<T>>.Exito(new(new(filas, 1, filas.Count, primera.Pagina.Total), primera.Totales, primera.FechaCorte));
    }
}
