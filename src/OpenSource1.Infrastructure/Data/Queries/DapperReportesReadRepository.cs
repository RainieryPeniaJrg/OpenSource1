using Dapper;
using OpenSource1.Application.Data;
using OpenSource1.Application.Features.Reportes;
using OpenSource1.Application.Features.MovimientosCliente;
using OpenSource1.Application.Features.MovimientosCliente.Dtos;
using OpenSource1.Core.Common;

namespace OpenSource1.Infrastructure.Data.Queries;

public sealed class DapperReportesReadRepository(IDbSession session, IMovimientoClienteReadRepository clientes) : IReportesReadRepository
{
    public Task<ReporteResponse<VentaReporteFila>> VentasAsync(ReporteCriterios c, CancellationToken ct) =>
        Consultar<VentaReporteFila>(c, Documentos(c, false), "\"Fecha\", \"TipoDocumento\", \"Numero\"", "\"Venta\"", "\"Devolucion\"", "0", "0", ct);

    public Task<ReporteResponse<TopProductoReporteFila>> TopProductosAsync(ReporteCriterios c, CancellationToken ct)
    {
        var sql = $"""
            SELECT d."ProductoId", p."Codigo" AS "ProductoCodigo", p."Nombre" AS "ProductoNombre",
                COALESCE(u."Codigo", '') AS "UnidadBaseCodigo",
                SUM(d."CantidadVendida") AS "CantidadVendida", SUM(d."CantidadDevuelta") AS "CantidadDevuelta",
                SUM(d."CantidadVendida" - d."CantidadDevuelta") AS "CantidadNeta",
                SUM(d."Venta") AS "Ventas", SUM(d."Devolucion") AS "Devoluciones", SUM(d."Venta" - d."Devolucion") AS "Neto"
            FROM ({Documentos(c, true)}) d
            JOIN "Productos" p ON p."Id" = d."ProductoId"
            LEFT JOIN "UnidadesMedida" u ON u."Id" = p."UnidadMedidaBaseId"
            GROUP BY d."ProductoId", p."Codigo", p."Nombre", u."Codigo"
            """;
        return Consultar<TopProductoReporteFila>(c, sql, "\"Neto\" DESC, \"ProductoCodigo\", \"ProductoId\"", "\"Ventas\"", "\"Devoluciones\"", "0", "0", ct);
    }

    public Task<ReporteResponse<InventarioReporteFila>> InventarioAsync(ReporteCriterios c, CancellationToken ct)
    {
        const string sql = """
            WITH mp AS (
                SELECT "ProductoId", "AlmacenId", SUM("Cantidad") AS cantidad FROM "MovimientosProducto"
                WHERE "FechaRegistro" <= @Hasta AND (@ProductoId::uuid IS NULL OR "ProductoId" = @ProductoId)
                  AND (@AlmacenId::uuid IS NULL OR "AlmacenId" = @AlmacenId) GROUP BY "ProductoId", "AlmacenId"
            ), mv AS (
                SELECT "ProductoId", "AlmacenId", SUM("ImporteCosto") AS valor FROM "MovimientosValor"
                WHERE "FechaRegistro" <= @Hasta AND (@ProductoId::uuid IS NULL OR "ProductoId" = @ProductoId)
                  AND (@AlmacenId::uuid IS NULL OR "AlmacenId" = @AlmacenId) GROUP BY "ProductoId", "AlmacenId"
            )
            SELECT COALESCE(mp."ProductoId", mv."ProductoId") AS "ProductoId", p."Codigo" AS "ProductoCodigo",
                p."Nombre" AS "ProductoNombre", COALESCE(mp."AlmacenId", mv."AlmacenId") AS "AlmacenId",
                a."Codigo" AS "AlmacenCodigo", COALESCE(u."Codigo", '') AS "UnidadBaseCodigo",
                COALESCE(mp.cantidad, 0) AS "Existencia", COALESCE(mv.valor, 0) AS "Valor"
            FROM mp FULL JOIN mv ON mp."ProductoId" = mv."ProductoId" AND mp."AlmacenId" = mv."AlmacenId"
            JOIN "Productos" p ON p."Id" = COALESCE(mp."ProductoId", mv."ProductoId")
            JOIN "Almacenes" a ON a."Id" = COALESCE(mp."AlmacenId", mv."AlmacenId")
            LEFT JOIN "UnidadesMedida" u ON u."Id" = p."UnidadMedidaBaseId"
            WHERE (@Estado = 'todos' OR (@Estado = 'con-existencia' AND COALESCE(mp.cantidad, 0) <> 0)
                OR (@Estado = 'sin-existencia' AND COALESCE(mp.cantidad, 0) = 0))
            """;
        return Consultar<InventarioReporteFila>(c, sql, "\"ProductoCodigo\", \"AlmacenCodigo\", \"ProductoId\", \"AlmacenId\"", "0", "0", "\"Existencia\"", "\"Valor\"", ct);
    }

    public async Task<ReporteResponse<EstadoCuentaClienteResponse>> CuentasPorCobrarAsync(ReporteCriterios c, CancellationToken ct)
    {
        var r = await clientes.GetEstadoCuentaAsync(new(c.Hasta, c.ClienteId, SoloConSaldo: c.Estado == "con-saldo"), new(c.Pagina, c.TamanoPagina, Descendente: false), ct);
        return new(r.Pagina, new(CuentasPorCobrar: r.Totales.Total), r.FechaCorte);
    }

    private async Task<ReporteResponse<T>> Consultar<T>(ReporteCriterios c, string filas, string orden,
        string ventas, string devoluciones, string existencia, string valor, CancellationToken ct)
    {
        var parametros = new { c.Desde, c.Hasta, c.ClienteId, c.ProductoId, c.AlmacenId, c.Estado, c.TamanoPagina, c.Moneda,
            Offset = new PageRequest(c.Pagina, c.TamanoPagina).Offset };
        await session.EnsureOpenAsync(ct);
        // A single PostgreSQL statement gives totals and page one consistent snapshot, including empty/out-of-range pages.
        var sql = $"""
            WITH filas AS ({filas}), resumen AS (
                SELECT COUNT(*) AS total, COALESCE(SUM({ventas}),0) AS ventas, COALESCE(SUM({devoluciones}),0) AS devoluciones,
                    COALESCE(SUM({existencia}),0) AS existencia, COALESCE(SUM({valor}),0) AS valor FROM filas
            )
            SELECT (SELECT row_to_json(resumen) FROM resumen)::text AS resumen,
                COALESCE((SELECT json_agg(pagina) FROM (SELECT * FROM filas ORDER BY {orden} LIMIT @TamanoPagina OFFSET @Offset) pagina), '[]'::json)::text AS filas
            """;
        var row = await session.Connection.QuerySingleAsync<ResultadoJson>(new CommandDefinition(sql, parametros, session.CurrentTransaction, cancellationToken: ct));
        var opciones = new System.Text.Json.JsonSerializerOptions { PropertyNameCaseInsensitive = true };
        var resumen = System.Text.Json.JsonSerializer.Deserialize<Resumen>(row.Resumen, opciones)!;
        var items = System.Text.Json.JsonSerializer.Deserialize<List<T>>(row.Filas, opciones)!;
        return new(new(items, c.Pagina, c.TamanoPagina, resumen.Total),
            new(resumen.Ventas, resumen.Devoluciones, resumen.Ventas - resumen.Devoluciones, resumen.Existencia, resumen.Valor), c.Hasta!.Value);
    }

    private static string Documentos(ReporteCriterios c, bool productos)
    {
        // Identifiers below are fixed source-code constants; every user filter remains a bound parameter.
        string Parte(bool nota)
        {
            var tabla = nota ? "NotasCreditoVenta" : "FacturasVenta";
            var lineas = nota ? "LineasNotaCreditoVenta" : "LineasFacturaVenta";
            var fk = nota ? "NotaCreditoVentaNumero" : "FacturaVentaNumero";
            var estado = nota ? "devoluciones" : "ventas";
            var importe = productos ? "l.\"ImporteLinea\"" : "d.\"ImporteTotal\"";
            var cantidad = "l.\"Cantidad\" * l.\"CantidadPorUnidadMedida\"";
            var seleccion = productos
                ? $"l.\"ProductoId\", {(nota ? "0" : cantidad)} AS \"CantidadVendida\", {(nota ? cantidad : "0")} AS \"CantidadDevuelta\","
                : $"d.\"Numero\", '{(nota ? "Nota de crédito" : "Factura")}' AS \"TipoDocumento\", d.\"FechaRegistro\" AS \"Fecha\", d.\"SocioNegocioFacturarAId\" AS \"ClienteId\", d.\"NombreFacturacion\" AS \"ClienteNombre\", d.\"Moneda\",";
            return $"""
                SELECT {seleccion} {(nota ? "0" : importe)} AS "Venta", {(nota ? importe : "0")} AS "Devolucion", {(nota ? "-" : "")}{importe} AS "Neto"
                FROM "{tabla}" d {(productos ? $"JOIN \"{lineas}\" l ON l.\"{fk}\" = d.\"Numero\"" : "")}
                WHERE (@Desde::date IS NULL OR d."FechaRegistro" >= @Desde) AND d."FechaRegistro" <= @Hasta
                  AND d."Moneda" = @Moneda
                  AND (@ClienteId::uuid IS NULL OR d."SocioNegocioFacturarAId" = @ClienteId)
                  AND (@Estado = 'todos' OR @Estado = '{estado}')
                  {(productos ? "AND l.\"ProductoId\" IS NOT NULL AND (@ProductoId::uuid IS NULL OR l.\"ProductoId\" = @ProductoId) AND (@AlmacenId::uuid IS NULL OR l.\"AlmacenId\" = @AlmacenId)" : $"AND ((@ProductoId::uuid IS NULL AND @AlmacenId::uuid IS NULL) OR EXISTS (SELECT 1 FROM \"{lineas}\" l WHERE l.\"{fk}\" = d.\"Numero\" AND (@ProductoId::uuid IS NULL OR l.\"ProductoId\" = @ProductoId) AND (@AlmacenId::uuid IS NULL OR l.\"AlmacenId\" = @AlmacenId)))")}
                """;
        }
        return Parte(false) + " UNION ALL " + Parte(true);
    }
    private sealed class ResultadoJson { public string Resumen { get; set; } = ""; public string Filas { get; set; } = ""; }
    private sealed class Resumen { public long Total { get; set; } public decimal Ventas { get; set; } public decimal Devoluciones { get; set; } public decimal Existencia { get; set; } public decimal Valor { get; set; } }
}
