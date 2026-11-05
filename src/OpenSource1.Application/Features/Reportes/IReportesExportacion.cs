using OpenSource1.Application.Features.MovimientosCliente.Dtos;
using OpenSource1.Core.Common;

namespace OpenSource1.Application.Features.Reportes;

/// <summary>Complete report rows and totals from a single server-side database snapshot.</summary>
public interface IReportesExportacion
{
    Task<Result<ReporteResponse<VentaReporteFila>>> VentasAsync(ReporteCriterios criterios, CancellationToken ct = default);
    Task<Result<ReporteResponse<InventarioReporteFila>>> InventarioAsync(ReporteCriterios criterios, CancellationToken ct = default);
    Task<Result<ReporteResponse<TopProductoReporteFila>>> TopProductosAsync(ReporteCriterios criterios, CancellationToken ct = default);
    Task<Result<ReporteResponse<EstadoCuentaClienteResponse>>> CuentasPorCobrarAsync(ReporteCriterios criterios, CancellationToken ct = default);
}
