using MediatR;
using OpenSource1.Application.Features.MovimientosCliente.Dtos;
using OpenSource1.Core.Common;

namespace OpenSource1.Application.Features.Reportes;

public static class ReportesValidacion
{
    public static List<Error> Validar(ReporteCriterios c, string reporte)
    {
        var errores = new List<Error>();
        if (c.Moneda is null || c.Moneda.Length != 3 || c.Moneda.Any(ch => ch is < 'A' or > 'Z'))
            errores.Add(new("reporte.moneda", "La moneda debe tener tres letras mayúsculas.", "Moneda"));
        if (reporte is "inventario" or "cuentas-por-cobrar" && c.Moneda != "DOP")
            errores.Add(new("reporte.moneda_base", "El libro de inventario y clientes está expresado en la moneda base DOP.", "Moneda"));
        if (c.Desde > c.Hasta) errores.Add(new("reporte.fechas", "Desde no puede ser posterior a Hasta.", "Desde"));
        if (reporte is "inventario" or "cuentas-por-cobrar" && c.Desde is not null)
            errores.Add(new("reporte.fecha_acumulada", "Este reporte usa Hasta como fecha de corte; no admite Desde.", "Desde"));
        var estados = reporte switch
        {
            "inventario" => new[] { "todos", "con-existencia", "sin-existencia" },
            "cuentas-por-cobrar" => ["todos", "con-saldo"],
            _ => ["todos", "ventas", "devoluciones"]
        };
        if (!estados.Contains(c.Estado, StringComparer.Ordinal)) errores.Add(new("reporte.estado", "Estado no válido para el reporte.", "Estado"));
        if (c.Pagina < 1) errores.Add(new("reporte.pagina", "La página debe ser positiva.", "Pagina"));
        if (c.TamanoPagina is < 1 or > 200) errores.Add(new("reporte.tamano", "El tamaño debe estar entre 1 y 200; recorra todas las páginas para exportar.", "TamanoPagina"));
        if (reporte == "cuentas-por-cobrar" && (c.ProductoId is not null || c.AlmacenId is not null))
            errores.Add(new("reporte.filtro", "Cuentas por cobrar no admite producto o almacén.", "ProductoId"));
        if (reporte == "inventario" && c.ClienteId is not null)
            errores.Add(new("reporte.filtro", "Inventario no admite cliente.", "ClienteId"));
        return errores;
    }
    public static async Task<Result<ReporteResponse<T>>> Ejecutar<T>(ReporteCriterios c, string tipo,
        Func<ReporteCriterios, CancellationToken, Task<ReporteResponse<T>>> consulta, CancellationToken ct)
    {
        // Original: var errores = Validar(c, tipo);
        c = c with { Hasta = c.Hasta ?? DateOnly.FromDateTime(DateTime.UtcNow) };
        var errores = Validar(c, tipo);
        return errores.Count > 0 ? Result<ReporteResponse<T>>.Fallo([.. errores])
            // Original: : Result<ReporteResponse<T>>.Exito(await consulta(c with { Hasta = c.Hasta ?? DateOnly.FromDateTime(DateTime.UtcNow) }, ct));
            : Result<ReporteResponse<T>>.Exito(await consulta(c, ct));
    }
}
public sealed class VentasReporteHandler(IReportesReadRepository repository) : IRequestHandler<VentasReporteQuery, Result<ReporteResponse<VentaReporteFila>>>
{
    public Task<Result<ReporteResponse<VentaReporteFila>>> Handle(VentasReporteQuery r, CancellationToken ct) => ReportesValidacion.Ejecutar(r.Criterios, "ventas", repository.VentasAsync, ct);
}
public sealed class InventarioReporteHandler(IReportesReadRepository repository) : IRequestHandler<InventarioReporteQuery, Result<ReporteResponse<InventarioReporteFila>>>
{
    public Task<Result<ReporteResponse<InventarioReporteFila>>> Handle(InventarioReporteQuery r, CancellationToken ct) => ReportesValidacion.Ejecutar(r.Criterios, "inventario", repository.InventarioAsync, ct);
}
public sealed class TopProductosReporteHandler(IReportesReadRepository repository) : IRequestHandler<TopProductosReporteQuery, Result<ReporteResponse<TopProductoReporteFila>>>
{
    public Task<Result<ReporteResponse<TopProductoReporteFila>>> Handle(TopProductosReporteQuery r, CancellationToken ct) => ReportesValidacion.Ejecutar(r.Criterios, "top-productos", repository.TopProductosAsync, ct);
}
public sealed class CuentasPorCobrarReporteHandler(IReportesReadRepository repository) : IRequestHandler<CuentasPorCobrarReporteQuery, Result<ReporteResponse<EstadoCuentaClienteResponse>>>
{
    public Task<Result<ReporteResponse<EstadoCuentaClienteResponse>>> Handle(CuentasPorCobrarReporteQuery r, CancellationToken ct) => ReportesValidacion.Ejecutar(r.Criterios, "cuentas-por-cobrar", repository.CuentasPorCobrarAsync, ct);
}
