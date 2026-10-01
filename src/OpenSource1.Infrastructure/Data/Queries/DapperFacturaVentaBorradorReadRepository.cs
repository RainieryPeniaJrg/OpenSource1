using Dapper;
using OpenSource1.Application.Data;
using OpenSource1.Application.Features.FacturasVenta.Borradores;
using OpenSource1.Application.Features.FacturasVenta.Borradores.Dtos;
using OpenSource1.Core.Common;

namespace OpenSource1.Infrastructure.Data.Queries;

/// <summary>
/// Cabeceras de borradores con los códigos de socios/término/almacén/series, la factura de un borrador <c>Posteada</c> y el conteo
/// de líneas vivas. El listado sin estado devuelve solo los borradores en curso (Abierta y Liberada). La consulta se aplana en
/// una subconsulta (<c>b</c>) para que los filtros y el orden nombren columnas sin calificar sin ambigüedad (mismo motivo que
/// <c>DapperProductoReadRepository</c>).
/// </summary>
public sealed class DapperFacturaVentaBorradorReadRepository(IDbSession session) : IFacturaVentaBorradorReadRepository
{
    private static readonly ColumnasPermitidas ColumnasPermitidas =
        new("Numero", "NombreFacturacion", "FechaDocumento", "FechaRegistro", "CreatedAtUtc");

    private const string Base = """
        SELECT f."Id", f."Numero", f."SocioNegocioId", sv."Codigo" AS "SocioNegocioCodigo", sv."NombreComercial" AS "SocioNegocioNombre",
               f."SocioNegocioFacturarAId", sf."Codigo" AS "SocioNegocioFacturarACodigo",
               f."NombreFacturacion", f."RazonSocialFacturacion", f."TipoDocumentoFiscal", f."NumeroDocumentoFiscal",
               f."DireccionFacturacionLinea1", f."DireccionFacturacionLinea2", f."CiudadFacturacion", f."PaisCodigoFacturacion",
               f."FechaRegistro", f."FechaDocumento", f."FechaVencimiento", f."TerminoPagoId", t."Codigo" AS "TerminoPagoCodigo",
               f."GrupoNegocioId", (SELECT g."Codigo" FROM "GruposNegocio" g WHERE g."Id" = f."GrupoNegocioId") AS "GrupoNegocioCodigo",
               f."GrupoIvaNegocioId", (SELECT g."Codigo" FROM "GruposIvaNegocio" g WHERE g."Id" = f."GrupoIvaNegocioId") AS "GrupoIvaNegocioCodigo",
               f."GrupoClienteContableId", (SELECT g."Codigo" FROM "GruposClienteContable" g WHERE g."Id" = f."GrupoClienteContableId") AS "GrupoClienteContableCodigo",
               f."AlmacenId", a."Codigo" AS "AlmacenCodigo",
               f."Estado", f."Moneda", f."Descripcion",
               f."SerieBorradorId", sb."Codigo" AS "SerieBorradorCodigo", f."SerieRegistroId", sr."Codigo" AS "SerieRegistroCodigo",
               f."FacturaVentaNumero",
               (SELECT COUNT(*) FROM "LineasFacturaVentaBorrador" l
                WHERE l."FacturaVentaBorradorId" = f."Id" AND l."IsDeleted" = false)::int AS "NumeroLineas",
               COALESCE((SELECT SUM(g."Base" + ROUND(g."Base" * g."PorcentajeIva" / 100, 2))
                         FROM (SELECT ROUND(SUM(l."ImporteLinea"), 2) AS "Base", l."PorcentajeIva"
                               FROM "LineasFacturaVentaBorrador" l
                               WHERE l."FacturaVentaBorradorId" = f."Id" AND l."IsDeleted" = false AND l."Tipo" <> 3
                               GROUP BY l."IdentificadorIva", l."PorcentajeIva") g), 0) AS "ImporteTotal",
               f.xmin::text::bigint AS "Xmin", f."CreatedAtUtc", f."UpdatedAtUtc", f."CreatedBy", f."UpdatedBy"
        FROM "FacturasVentaBorrador" f
        LEFT JOIN "SociosNegocio" sv ON sv."Id" = f."SocioNegocioId"
        LEFT JOIN "SociosNegocio" sf ON sf."Id" = f."SocioNegocioFacturarAId"
        LEFT JOIN "TerminosPago" t ON t."Id" = f."TerminoPagoId"
        LEFT JOIN "Almacenes" a ON a."Id" = f."AlmacenId"
        LEFT JOIN "Series" sb ON sb."Id" = f."SerieBorradorId"
        LEFT JOIN "Series" sr ON sr."Id" = f."SerieRegistroId"
        WHERE f."IsDeleted" = false
        """;

    public async Task<FacturaVentaBorradorResponse?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        var sql = $"""
            SELECT * FROM ({Base}) b
            WHERE "Id" = @Id
            """;

        await session.EnsureOpenAsync(cancellationToken);
        return await session.Connection.QuerySingleOrDefaultAsync<FacturaVentaBorradorResponse>(
            new CommandDefinition(sql, new { Id = id }, session.CurrentTransaction, cancellationToken: cancellationToken));
    }

    public async Task<Result<PagedResult<FacturaVentaBorradorResponse>>> ListAsync(
        FacturaVentaBorradorSearchCriteria search, PageRequest paginacion, CancellationToken cancellationToken = default)
    {
        var pagina = paginacion.Normalizar();

        var filters = new List<string>();
        var parameters = new DynamicParameters();

        FilterExpressionBuilder.AddTextFilter(filters, parameters, ColumnasPermitidas, "Numero", search.Numero);
        FilterExpressionBuilder.AddTextFilter(filters, parameters, ColumnasPermitidas, "NombreFacturacion", search.NombreFacturacion);

        if (search.SocioNegocioId is { } socioId)
        {
            filters.Add("(\"SocioNegocioId\" = @SocioNegocioId OR \"SocioNegocioFacturarAId\" = @SocioNegocioId)");
            parameters.Add("SocioNegocioId", socioId);
        }

        if (search.Estado is { } estado)
        {
            filters.Add("\"Estado\" = @Estado");
            parameters.Add("Estado", (short)estado);
        }
        else
        {
            // Por defecto, los borradores en curso (spec no-series): los Posteada se ven con estado=3.
            filters.Add("\"Estado\" IN (1, 2)");
        }

        var whereSql = filters.Count == 0 ? string.Empty : Environment.NewLine + "WHERE " + string.Join(" AND ", filters);

        var ordenColumna = ColumnasPermitidas.EsValida(pagina.OrdenarPor) ? pagina.OrdenarPor! : "CreatedAtUtc";
        var ordenSql = ColumnasPermitidas.Citar(ordenColumna);
        var direccionSql = pagina.Descendente ? "DESC" : "ASC";

        parameters.Add("TamanoPagina", pagina.TamanoPagina);
        parameters.Add("Offset", pagina.Offset);

        var countSql = $"""
            SELECT COUNT(*) FROM ({Base}) b
            {whereSql}
            """;

        var pageSql = $"""
            SELECT * FROM ({Base}) b
            {whereSql}
            ORDER BY {ordenSql} {direccionSql}, "Id" ASC
            LIMIT @TamanoPagina OFFSET @Offset
            """;

        await session.EnsureOpenAsync(cancellationToken);

        var total = await session.Connection.ExecuteScalarAsync<long>(
            new CommandDefinition(countSql, parameters, session.CurrentTransaction, cancellationToken: cancellationToken));

        var items = await session.Connection.QueryAsync<FacturaVentaBorradorResponse>(
            new CommandDefinition(pageSql, parameters, session.CurrentTransaction, cancellationToken: cancellationToken));

        return Result<PagedResult<FacturaVentaBorradorResponse>>.Exito(
            new PagedResult<FacturaVentaBorradorResponse>(items.AsList(), pagina.Pagina, pagina.TamanoPagina, total));
    }
}
