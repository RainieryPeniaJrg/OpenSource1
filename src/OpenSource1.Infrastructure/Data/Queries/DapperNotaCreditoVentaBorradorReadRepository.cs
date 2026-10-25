using Dapper;
using OpenSource1.Application.Data;
using OpenSource1.Application.Features.NotasCreditoVenta.Borradores;
using OpenSource1.Application.Features.NotasCreditoVenta.Borradores.Dtos;
using OpenSource1.Core.Common;
using OpenSource1.Core.Enums;

namespace OpenSource1.Infrastructure.Data.Queries;

/// <summary>
/// Borradores de notas de crédito (Task 8.6): cabeceras con los códigos de los socios, de la CxC y el total de la factura; líneas con
/// lo facturado y lo acreditado por notas posteadas; líneas acreditables de la factura. Mismo patrón que
/// <see cref="DapperFacturaVentaBorradorReadRepository"/> (subconsulta aplanada <c>b</c>, orden por allow-list).
/// </summary>
public sealed class DapperNotaCreditoVentaBorradorReadRepository(IDbSession session) : INotaCreditoVentaBorradorReadRepository
{
    private static readonly ColumnasPermitidas ColumnasPermitidas =
        new("Numero", "FacturaVentaNumero", "NombreFacturacion", "FechaDocumento", "FechaRegistro", "CreatedAtUtc");

    private const string Base = """
        SELECT n."Id", n."Numero", n."FacturaVentaNumero", f."ImporteTotal" AS "FacturaImporteTotal",
               f."FechaRegistro" AS "FacturaFechaRegistro",
               n."SocioNegocioId", sv."Codigo" AS "SocioNegocioCodigo", sv."NombreComercial" AS "SocioNegocioNombre",
               n."SocioNegocioFacturarAId", sf."Codigo" AS "SocioNegocioFacturarACodigo",
               n."NombreFacturacion", n."RazonSocialFacturacion", n."TipoDocumentoFiscal", n."NumeroDocumentoFiscal",
               n."DireccionFacturacionLinea1", n."DireccionFacturacionLinea2", n."CiudadFacturacion", n."PaisCodigoFacturacion",
               n."FechaRegistro", n."FechaDocumento", n."GrupoNegocioId", n."GrupoIvaNegocioId", n."GrupoClienteContableId",
               n."CuentaCxCId", c."Numero" AS "CuentaCxCNumero", n."Moneda", n."Descripcion",
               (SELECT COUNT(*) FROM "LineasNotaCreditoVentaBorrador" l
                WHERE l."NotaCreditoVentaBorradorId" = n."Id" AND l."IsDeleted" = false)::int AS "NumeroLineas",
               COALESCE((SELECT SUM(g."Base" + ROUND(g."Base" * g."PorcentajeIva" / 100, 2))
                         FROM (SELECT ROUND(SUM(l."ImporteLinea"), 2) AS "Base", l."PorcentajeIva"
                               FROM "LineasNotaCreditoVentaBorrador" l
                               WHERE l."NotaCreditoVentaBorradorId" = n."Id" AND l."IsDeleted" = false AND l."Tipo" <> 3
                               GROUP BY l."IdentificadorIva", l."PorcentajeIva") g), 0) AS "ImporteTotal",
               n."Estado", n."SerieBorradorId", sb."Codigo" AS "SerieBorradorCodigo",
               n."SerieRegistroId", sr."Codigo" AS "SerieRegistroCodigo", n."NotaCreditoVentaNumero",
               n.xmin::text::bigint AS "Xmin", n."CreatedAtUtc", n."UpdatedAtUtc", n."CreatedBy", n."UpdatedBy"
        FROM "NotasCreditoVentaBorrador" n
        JOIN "FacturasVenta" f ON f."Numero" = n."FacturaVentaNumero"
        LEFT JOIN "SociosNegocio" sv ON sv."Id" = n."SocioNegocioId"
        LEFT JOIN "SociosNegocio" sf ON sf."Id" = n."SocioNegocioFacturarAId"
        LEFT JOIN "CuentasContables" c ON c."Id" = n."CuentaCxCId"
        LEFT JOIN "Series" sb ON sb."Id" = n."SerieBorradorId"
        LEFT JOIN "Series" sr ON sr."Id" = n."SerieRegistroId"
        WHERE n."IsDeleted" = false
        """;

    // Acreditado de la línea de factura por notas POSTEADAS, sin la propia nota de un borrador Posteada (no se resta a sí mismo).
    private const string ColumnasLinea = """
        l."Id", l."NotaCreditoVentaBorradorId", l."LineaFacturaVentaId", l."NumeroLinea", l."Tipo",
        l."ProductoId", p."Codigo" AS "ProductoCodigo", l."CuentaContableId", c."Numero" AS "CuentaContableNumero",
        l."Descripcion", l."AlmacenId", a."Codigo" AS "AlmacenCodigo", l."UnidadMedidaId", u."Codigo" AS "UnidadMedidaCodigo",
        l."CantidadPorUnidadMedida", l."Cantidad", lf."Cantidad" AS "CantidadFacturada",
        COALESCE((SELECT SUM(ln."Cantidad") FROM "LineasNotaCreditoVenta" ln
                  WHERE ln."LineaFacturaVentaId" = l."LineaFacturaVentaId"
                    AND ln."NotaCreditoVentaNumero" IS DISTINCT FROM (
                        SELECT nb."NotaCreditoVentaNumero" FROM "NotasCreditoVentaBorrador" nb WHERE nb."Id" = l."NotaCreditoVentaBorradorId")), 0)
            AS "CantidadAcreditada",
        l."PrecioUnitario", l."PorcentajeDescuentoLinea", l."ImporteDescuentoLinea", l."ImporteLinea",
        l."GrupoProductoId", l."GrupoIvaProductoId", l."GrupoInventarioId", l."IdentificadorIva", l."PorcentajeIva",
        l."DevolverInventario", l.xmin::text::bigint AS "Xmin", l."CreatedAtUtc", l."UpdatedAtUtc", l."CreatedBy", l."UpdatedBy"
        """;

    private const string DesdeLinea = """
        FROM "LineasNotaCreditoVentaBorrador" l
        JOIN "LineasFacturaVenta" lf ON lf."Id" = l."LineaFacturaVentaId"
        LEFT JOIN "Productos" p ON p."Id" = l."ProductoId"
        LEFT JOIN "CuentasContables" c ON c."Id" = l."CuentaContableId"
        LEFT JOIN "Almacenes" a ON a."Id" = l."AlmacenId"
        LEFT JOIN "UnidadesMedida" u ON u."Id" = l."UnidadMedidaId"
        """;

    public async Task<NotaCreditoVentaBorradorResponse?> GetByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await session.EnsureOpenAsync(cancellationToken);
        return await session.Connection.QuerySingleOrDefaultAsync<NotaCreditoVentaBorradorResponse>(new CommandDefinition(
            $"""SELECT * FROM ({Base}) b WHERE "Id" = @Id""", new { Id = id }, session.CurrentTransaction, cancellationToken: cancellationToken));
    }

    public async Task<PagedResult<NotaCreditoVentaBorradorResponse>> ListAsync(
        NotaCreditoVentaBorradorSearchCriteria search, PageRequest paginacion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(search);
        var pagina = paginacion.Normalizar();
        var filters = new List<string>();
        var parameters = new DynamicParameters();

        FilterExpressionBuilder.AddTextFilter(filters, parameters, ColumnasPermitidas, "Numero", search.Numero);
        FilterExpressionBuilder.AddTextFilter(filters, parameters, ColumnasPermitidas, "FacturaVentaNumero", search.FacturaVentaNumero);
        FilterExpressionBuilder.AddTextFilter(filters, parameters, ColumnasPermitidas, "NombreFacturacion", search.NombreFacturacion);
        if (search.SocioNegocioId is { } socioId)
        {
            filters.Add("(\"SocioNegocioId\" = @SocioNegocioId OR \"SocioNegocioFacturarAId\" = @SocioNegocioId)");
            parameters.Add("SocioNegocioId", socioId);
        }

        // Sin estado, solo los Abierta (los Posteada se consultan con estado=3).
        filters.Add("\"Estado\" = @Estado");
        parameters.Add("Estado", (short)(search.Estado ?? EstadoNotaCreditoBorrador.Abierta));

        var whereSql = filters.Count == 0 ? string.Empty : Environment.NewLine + "WHERE " + string.Join(" AND ", filters);
        var ordenColumna = ColumnasPermitidas.EsValida(pagina.OrdenarPor) ? pagina.OrdenarPor! : "CreatedAtUtc";
        var direccionSql = pagina.Descendente ? "DESC" : "ASC";
        parameters.Add("TamanoPagina", pagina.TamanoPagina);
        parameters.Add("Offset", pagina.Offset);

        await session.EnsureOpenAsync(cancellationToken);
        var total = await session.Connection.ExecuteScalarAsync<long>(new CommandDefinition(
            $"SELECT COUNT(*) FROM ({Base}) b {whereSql}", parameters, session.CurrentTransaction, cancellationToken: cancellationToken));
        var items = await session.Connection.QueryAsync<NotaCreditoVentaBorradorResponse>(new CommandDefinition(
            $"""
            SELECT * FROM ({Base}) b
            {whereSql}
            ORDER BY {ColumnasPermitidas.Citar(ordenColumna)} {direccionSql}, "Id" ASC
            LIMIT @TamanoPagina OFFSET @Offset
            """,
            parameters, session.CurrentTransaction, cancellationToken: cancellationToken));

        return new PagedResult<NotaCreditoVentaBorradorResponse>(items.AsList(), pagina.Pagina, pagina.TamanoPagina, total);
    }

    public async Task<LineaNotaCreditoVentaBorradorResponse?> GetLineaByIdAsync(Guid id, CancellationToken cancellationToken = default)
    {
        await session.EnsureOpenAsync(cancellationToken);
        return await session.Connection.QuerySingleOrDefaultAsync<LineaNotaCreditoVentaBorradorResponse>(new CommandDefinition(
            $"""
            SELECT {ColumnasLinea}
            {DesdeLinea}
            WHERE l."Id" = @Id AND l."IsDeleted" = false
            """,
            new { Id = id }, session.CurrentTransaction, cancellationToken: cancellationToken));
    }

    public async Task<IReadOnlyList<LineaNotaCreditoVentaBorradorResponse>> ListLineasAsync(
        Guid notaCreditoVentaBorradorId, CancellationToken cancellationToken = default)
    {
        await session.EnsureOpenAsync(cancellationToken);
        var items = await session.Connection.QueryAsync<LineaNotaCreditoVentaBorradorResponse>(new CommandDefinition(
            $"""
            SELECT {ColumnasLinea}
            {DesdeLinea}
            WHERE l."NotaCreditoVentaBorradorId" = @Id AND l."IsDeleted" = false
            ORDER BY l."NumeroLinea"
            """,
            new { Id = notaCreditoVentaBorradorId }, session.CurrentTransaction, cancellationToken: cancellationToken));
        return items.AsList();
    }

    public async Task<IReadOnlyList<LineaFacturaAcreditableResponse>> ListLineasAcreditablesAsync(
        Guid notaCreditoVentaBorradorId, CancellationToken cancellationToken = default)
    {
        await session.EnsureOpenAsync(cancellationToken);
        var items = await session.Connection.QueryAsync<LineaFacturaAcreditableResponse>(new CommandDefinition(
            """
            SELECT x.*, x."CantidadFacturada" - x."CantidadAcreditada" AS "CantidadPendiente"
            FROM (
                SELECT lf."Id" AS "LineaFacturaVentaId", lf."NumeroLinea", lf."Tipo", lf."ProductoId", p."Codigo" AS "ProductoCodigo",
                       lf."CuentaContableId", c."Numero" AS "CuentaContableNumero", lf."Descripcion", u."Codigo" AS "UnidadMedidaCodigo",
                       lf."PrecioUnitario", lf."PorcentajeDescuentoLinea", lf."Cantidad" AS "CantidadFacturada",
                       COALESCE((SELECT SUM(ln."Cantidad") FROM "LineasNotaCreditoVenta" ln
                                 WHERE ln."LineaFacturaVentaId" = lf."Id"
                                   AND ln."NotaCreditoVentaNumero" IS DISTINCT FROM n."NotaCreditoVentaNumero"), 0)
                           AS "CantidadAcreditada",
                       lb."Id" AS "LineaNotaId", lb."Cantidad" AS "CantidadEnBorrador"
                FROM "NotasCreditoVentaBorrador" n
                JOIN "LineasFacturaVenta" lf ON lf."FacturaVentaNumero" = n."FacturaVentaNumero"
                LEFT JOIN "LineasNotaCreditoVentaBorrador" lb
                    ON lb."NotaCreditoVentaBorradorId" = n."Id" AND lb."LineaFacturaVentaId" = lf."Id" AND lb."IsDeleted" = false
                LEFT JOIN "Productos" p ON p."Id" = lf."ProductoId"
                LEFT JOIN "CuentasContables" c ON c."Id" = lf."CuentaContableId"
                LEFT JOIN "UnidadesMedida" u ON u."Id" = lf."UnidadMedidaId"
                WHERE n."Id" = @Id AND n."IsDeleted" = false AND lf."Tipo" <> 3
            ) x
            ORDER BY x."NumeroLinea"
            """,
            new { Id = notaCreditoVentaBorradorId }, session.CurrentTransaction, cancellationToken: cancellationToken));
        return items.AsList();
    }
}
