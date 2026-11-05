using MediatR;
using OpenSource1.Application.Features.MovimientosCliente.Dtos;
using OpenSource1.Core.Common;

namespace OpenSource1.Application.Features.Reportes;

public sealed record ReporteCriterios(DateOnly? Desde = null, DateOnly? Hasta = null, Guid? ClienteId = null,
    Guid? ProductoId = null, string Estado = "todos", Guid? AlmacenId = null, int Pagina = 1, int TamanoPagina = 200,
    string Moneda = "DOP");
public sealed record ReporteTotales(decimal Ventas = 0, decimal Devoluciones = 0, decimal Neto = 0,
    decimal Existencia = 0, decimal Valor = 0, decimal CuentasPorCobrar = 0);
public sealed record ReporteResponse<T>(PagedResult<T> Pagina, ReporteTotales Totales, DateOnly FechaCorte);
public sealed class VentaReporteFila
{
    public string Numero { get; init; } = "";
    public string TipoDocumento { get; init; } = "";
    public DateOnly Fecha { get; init; }
    public Guid ClienteId { get; init; }
    public string ClienteNombre { get; init; } = "";
    public string Moneda { get; init; } = "";
    public decimal Venta { get; init; }
    public decimal Devolucion { get; init; }
    public decimal Neto { get; init; }
}
public sealed class InventarioReporteFila
{
    public Guid ProductoId { get; init; }
    public string ProductoCodigo { get; init; } = "";
    public string ProductoNombre { get; init; } = "";
    public Guid AlmacenId { get; init; }
    public string AlmacenCodigo { get; init; } = "";
    public string UnidadBaseCodigo { get; init; } = "";
    public decimal Existencia { get; init; }
    public decimal Valor { get; init; }
}
public sealed class TopProductoReporteFila
{
    public Guid ProductoId { get; init; }
    public string ProductoCodigo { get; init; } = "";
    public string ProductoNombre { get; init; } = "";
    public string UnidadBaseCodigo { get; init; } = "";
    public decimal CantidadVendida { get; init; }
    public decimal CantidadDevuelta { get; init; }
    public decimal CantidadNeta { get; init; }
    public decimal Ventas { get; init; }
    public decimal Devoluciones { get; init; }
    public decimal Neto { get; init; }
}
public interface IReportesReadRepository
{
    Task<ReporteResponse<VentaReporteFila>> VentasAsync(ReporteCriterios criterios, CancellationToken cancellationToken);
    Task<ReporteResponse<InventarioReporteFila>> InventarioAsync(ReporteCriterios criterios, CancellationToken cancellationToken);
    Task<ReporteResponse<TopProductoReporteFila>> TopProductosAsync(ReporteCriterios criterios, CancellationToken cancellationToken);
    Task<ReporteResponse<EstadoCuentaClienteResponse>> CuentasPorCobrarAsync(ReporteCriterios criterios, CancellationToken cancellationToken);
}
public sealed record VentasReporteQuery(ReporteCriterios Criterios) : IRequest<Result<ReporteResponse<VentaReporteFila>>>;
public sealed record InventarioReporteQuery(ReporteCriterios Criterios) : IRequest<Result<ReporteResponse<InventarioReporteFila>>>;
public sealed record TopProductosReporteQuery(ReporteCriterios Criterios) : IRequest<Result<ReporteResponse<TopProductoReporteFila>>>;
public sealed record CuentasPorCobrarReporteQuery(ReporteCriterios Criterios) : IRequest<Result<ReporteResponse<EstadoCuentaClienteResponse>>>;
