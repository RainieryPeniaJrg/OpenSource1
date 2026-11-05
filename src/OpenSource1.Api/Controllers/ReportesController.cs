using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenSource1.Api.Infrastructure;
using OpenSource1.Application.Features.Reportes;
using OpenSource1.Application.Security;

namespace OpenSource1.Api.Controllers;

/// <summary>Posted ledger reports. Dates are inclusive; inventory and receivables use Hasta as historical cutoff.
/// Sales product/warehouse filters select whole matching documents; product ranking amounts exclude VAT.
/// Sales and ranking are restricted to Moneda (DOP by default); inventory and receivables use base currency DOP.
/// Original contract: responses expose full filtered totals and page count; export clients must request every page.
/// Paged views retain that contract. The exportacion routes return every row and its totals from one server-side snapshot.</summary>
[ApiController]
[Route("api/reportes")]
[Authorize(Policy = ApplicationPolicies.CanConsult)]
public sealed class ReportesController(ISender sender) : ControllerBase
{
    [HttpGet("ventas/exportacion")]
    public async Task<IActionResult> ExportarVentas([FromQuery] ReporteCriterios criterios,
        [FromServices] IReportesExportacion exportacion, CancellationToken ct)
    {
        var r = await exportacion.VentasAsync(criterios, ct);
        return r.EsFallo ? r.ToActionResult() : Ok(r.Valor);
    }

    [HttpGet("inventario/exportacion")]
    public async Task<IActionResult> ExportarInventario([FromQuery] ReporteCriterios criterios,
        [FromServices] IReportesExportacion exportacion, CancellationToken ct)
    {
        var r = await exportacion.InventarioAsync(criterios, ct);
        return r.EsFallo ? r.ToActionResult() : Ok(r.Valor);
    }

    [HttpGet("top-productos/exportacion")]
    public async Task<IActionResult> ExportarTopProductos([FromQuery] ReporteCriterios criterios,
        [FromServices] IReportesExportacion exportacion, CancellationToken ct)
    {
        var r = await exportacion.TopProductosAsync(criterios, ct);
        return r.EsFallo ? r.ToActionResult() : Ok(r.Valor);
    }

    [HttpGet("cuentas-por-cobrar/exportacion")]
    public async Task<IActionResult> ExportarCuentasPorCobrar([FromQuery] ReporteCriterios criterios,
        [FromServices] IReportesExportacion exportacion, CancellationToken ct)
    {
        var r = await exportacion.CuentasPorCobrarAsync(criterios, ct);
        return r.EsFallo ? r.ToActionResult() : Ok(r.Valor);
    }

    [HttpGet("ventas")]
    public async Task<IActionResult> Ventas([FromQuery] ReporteCriterios criterios, CancellationToken ct)
    {
        var r = await sender.Send(new VentasReporteQuery(criterios), ct);
        return r.EsFallo ? r.ToActionResult() : Ok(r.Valor);
    }
    [HttpGet("inventario")]
    public async Task<IActionResult> Inventario([FromQuery] ReporteCriterios criterios, CancellationToken ct)
    {
        var r = await sender.Send(new InventarioReporteQuery(criterios), ct);
        return r.EsFallo ? r.ToActionResult() : Ok(r.Valor);
    }
    [HttpGet("top-productos")]
    public async Task<IActionResult> TopProductos([FromQuery] ReporteCriterios criterios, CancellationToken ct)
    {
        var r = await sender.Send(new TopProductosReporteQuery(criterios), ct);
        return r.EsFallo ? r.ToActionResult() : Ok(r.Valor);
    }
    [HttpGet("cuentas-por-cobrar")]
    public async Task<IActionResult> CuentasPorCobrar([FromQuery] ReporteCriterios criterios, CancellationToken ct)
    {
        var r = await sender.Send(new CuentasPorCobrarReporteQuery(criterios), ct);
        return r.EsFallo ? r.ToActionResult() : Ok(r.Valor);
    }
}
