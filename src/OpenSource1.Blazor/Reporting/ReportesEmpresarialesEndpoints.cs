using Microsoft.AspNetCore.Mvc;
using OpenSource1.Application.Features.Reportes;
using OpenSource1.Application.Security;
using OpenSource1.Blazor.Services;

namespace OpenSource1.Blazor.Reporting;

public static class ReportesEmpresarialesEndpoints
{
    public static void MapReportesEmpresariales(this WebApplication app)
    {
        app.MapGet("/reports/empresarial/{tipo}.{formato}", async (
            string tipo, string formato, [AsParameters] ReporteDescargaFiltros filtros,
            ReportesEmpresarialesApiClient client, ReporteEmpresarialDocumentService documentos, CancellationToken ct) =>
        {
            if (!ReportesEmpresarialesApiClient.Tipos.Contains(tipo) || formato is not ("pdf" or "xlsx")) return Results.NotFound();
            try
            {
                var tabla = await client.TablaAsync(tipo, filtros.Criterios(), true, ct);
                var archivo = formato == "pdf" ? documentos.Pdf(tabla) : documentos.Excel(tabla);
                return Results.File(archivo.Content, archivo.ContentType, $"{tipo}.{formato}");
            }
            catch (HttpRequestException error)
            {
                return Results.Problem(detail: error.Message, statusCode: (int?)error.StatusCode ?? 503);
            }
        }).RequireAuthorization(ApplicationPolicies.CanConsult);
    }
}

public sealed class ReporteDescargaFiltros
{
    public DateOnly? Desde { get; set; }
    public DateOnly? Hasta { get; set; }
    public Guid? ClienteId { get; set; }
    public Guid? ProductoId { get; set; }
    public Guid? AlmacenId { get; set; }
    public string? Estado { get; set; }
    public string? Moneda { get; set; }
    public ReporteCriterios Criterios() => new(Desde, Hasta, ClienteId, ProductoId, Estado ?? "todos", AlmacenId, Moneda: Moneda ?? "DOP");
}
