using Dapper;
using OpenSource1.Application.Data;
using OpenSource1.Application.Features.Inventario.Consultas;
using OpenSource1.Application.Features.Inventario.Consultas.Dtos;
using OpenSource1.Core.Common;

namespace OpenSource1.Infrastructure.Data.Queries;

/// <summary>
/// Vistas del libro de inventario (Task 7.2): solo lectura, todo derivado (D1). Cada listado pagina PRIMERO sobre la tabla del
/// libro (orden por allow-list <see cref="ColumnasPermitidas"/> con desempate estable por <c>Id</c>) y solo después une los
/// maestros (producto, almacén, unidades) a las filas de la página: las columnas de código y nombre son de presentación, no de
/// orden. Sin filtro de borrado lógico en los maestros: el movimiento es historia y su producto/almacén se muestra igual.
/// </summary>
public sealed class DapperInventarioConsultasRepository(IDbSession session) : IInventarioConsultasReadRepository
{
    private static readonly ColumnasPermitidas ColumnasMovimientosProducto =
        new("Id", "FechaRegistro", "NumeroDocumento", "Cantidad", "TipoMovimiento", "TipoOrigen");

    private static readonly ColumnasPermitidas ColumnasMovimientosValor =
        new("Id", "FechaRegistro", "NumeroDocumento", "CantidadValorada", "ImporteCosto", "TipoMovimiento", "TipoOrigen");

    private static readonly ColumnasPermitidas ColumnasExistencias =
        new("ProductoCodigo", "ProductoNombre", "AlmacenCodigo", "Existencia", "Valor");

    /// <summary>
    /// Movimientos de producto. Con producto fijado, el saldo acumulado es la existencia tras cada movimiento en orden
    /// (<c>FechaRegistro</c>, <c>Id</c>): <c>SUM(Cantidad) OVER (...)</c> sobre el producto (y almacén) en el rango de fechas, más
    /// el saldo anterior a <c>desde</c>. La ventana se calcula ANTES de los filtros de tipo/origen/documento y de la paginación,
    /// así que esos filtros solo ocultan filas (el saldo sigue siendo la existencia real) y cada página arranca donde acabó la
    /// anterior.
    /// </summary>
    public async Task<PagedResult<MovimientoProductoVistaResponse>> ListMovimientosProductoAsync(
        MovimientoProductoVistaCriterios criterios, PageRequest paginacion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(criterios);
        var pagina = paginacion.Normalizar();
        var conSaldo = criterios.ProductoId is not null;
        var parameters = new DynamicParameters();
        var interior = new List<string>();
        var exterior = new List<string>();

        AgregarFiltrosComunes(
            interior, conSaldo ? exterior : interior, parameters, ColumnasMovimientosProducto, criterios.ProductoId,
            criterios.AlmacenId, criterios.Desde, criterios.Hasta, criterios.TipoMovimiento, criterios.TipoOrigen,
            criterios.NumeroDocumento);

        var saldoSql = string.Empty;
        if (conSaldo)
        {
            var saldoAnteriorSql = "0";
            if (criterios.Desde is not null)
            {
                // Índice IX_MovimientosProducto_Existencia (ProductoId, AlmacenId) INCLUDE (Cantidad, FechaRegistro).
                saldoAnteriorSql = $"""
                    (SELECT COALESCE(SUM(a."Cantidad"), 0) FROM "MovimientosProducto" a
                     WHERE a."ProductoId" = @ProductoId{(criterios.AlmacenId is null ? "" : """ AND a."AlmacenId" = @AlmacenId""")}
                       AND a."FechaRegistro" < @Desde)
                    """;
            }

            saldoSql = $""", {saldoAnteriorSql} + SUM("Cantidad") OVER (ORDER BY "FechaRegistro", "Id") AS "SaldoAcumulado" """;
        }

        var baseSql = $"""
            SELECT "Id", "FechaRegistro", "FechaDocumento", "TipoMovimiento", "TipoDocumento", "NumeroDocumento",
                   "NumeroLineaDocumento", "ProductoId", "AlmacenId", "Cantidad", "CantidadRestante", "UnidadMedidaId",
                   "CantidadPorUnidadMedida", "TipoOrigen"{saldoSql}
            FROM "MovimientosProducto"
            {Where(interior)}
            """;

        var (ordenSql, ordenPaginaSql) = Orden(ColumnasMovimientosProducto, pagina, "FechaRegistro", "b", "pg");
        parameters.Add("TamanoPagina", pagina.TamanoPagina);
        parameters.Add("Offset", pagina.Offset);

        // Todos los filtros son por fila: el total no necesita la ventana.
        var countSql = $"""
            SELECT COUNT(*) FROM "MovimientosProducto"
            {Where([.. interior, .. exterior])}
            """;

        var pageSql = $"""
            WITH pagina AS (
                SELECT * FROM ({baseSql}) b
                {Where(exterior)}
                ORDER BY {ordenSql}
                LIMIT @TamanoPagina OFFSET @Offset
            )
            SELECT pg.*, p."Codigo" AS "ProductoCodigo", p."Nombre" AS "ProductoNombre", ub."Codigo" AS "UnidadBaseCodigo",
                   al."Codigo" AS "AlmacenCodigo", al."Nombre" AS "AlmacenNombre", u."Codigo" AS "UnidadMedidaCodigo"
            FROM pagina pg
            JOIN "Productos" p ON p."Id" = pg."ProductoId"
            JOIN "Almacenes" al ON al."Id" = pg."AlmacenId"
            JOIN "UnidadesMedida" u ON u."Id" = pg."UnidadMedidaId"
            LEFT JOIN "UnidadesMedida" ub ON ub."Id" = p."UnidadMedidaBaseId"
            ORDER BY {ordenPaginaSql}
            """;

        await session.EnsureOpenAsync(cancellationToken);
        var total = await session.Connection.ExecuteScalarAsync<long>(
            new CommandDefinition(countSql, parameters, session.CurrentTransaction, cancellationToken: cancellationToken));
        var items = await session.Connection.QueryAsync<MovimientoProductoVistaResponse>(
            new CommandDefinition(pageSql, parameters, session.CurrentTransaction, cancellationToken: cancellationToken));

        return new PagedResult<MovimientoProductoVistaResponse>(items.AsList(), pagina.Pagina, pagina.TamanoPagina, total);
    }

    public async Task<PagedResult<MovimientoValorVistaResponse>> ListMovimientosValorAsync(
        MovimientoValorVistaCriterios criterios, PageRequest paginacion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(criterios);
        var pagina = paginacion.Normalizar();
        var parameters = new DynamicParameters();
        var filtros = new List<string>();

        AgregarFiltrosComunes(
            filtros, filtros, parameters, ColumnasMovimientosValor, criterios.ProductoId, criterios.AlmacenId, criterios.Desde,
            criterios.Hasta, criterios.TipoMovimiento, criterios.TipoOrigen, criterios.NumeroDocumento);
        if (criterios.SoloAjustes)
        {
            filtros.Add("\"Ajuste\"");
        }

        var (ordenSql, ordenPaginaSql) = Orden(ColumnasMovimientosValor, pagina, "FechaRegistro", "v", "pg");
        parameters.Add("TamanoPagina", pagina.TamanoPagina);
        parameters.Add("Offset", pagina.Offset);

        var countSql = $"""
            SELECT COUNT(*) FROM "MovimientosValor"
            {Where(filtros)}
            """;

        var pageSql = $"""
            WITH pagina AS (
                SELECT v."Id", v."MovimientoProductoId", v."FechaRegistro", v."TipoMovimiento", v."TipoDocumento",
                       v."NumeroDocumento", v."ProductoId", v."AlmacenId", v."CantidadValorada", v."ImporteCosto",
                       v."CostoPorUnidad", v."ImporteVenta", v."ImporteCostoPosteadoContabilidad",
                       v."ImporteCostoPosteadoContabilidad" = v."ImporteCosto" AS "Contabilizado", v."Ajuste", v."TipoValor",
                       v."TipoOrigen"
                FROM "MovimientosValor" v
                {Where(filtros)}
                ORDER BY {ordenSql}
                LIMIT @TamanoPagina OFFSET @Offset
            )
            SELECT pg.*, p."Codigo" AS "ProductoCodigo", p."Nombre" AS "ProductoNombre", al."Codigo" AS "AlmacenCodigo",
                   al."Nombre" AS "AlmacenNombre"
            FROM pagina pg
            JOIN "Productos" p ON p."Id" = pg."ProductoId"
            JOIN "Almacenes" al ON al."Id" = pg."AlmacenId"
            ORDER BY {ordenPaginaSql}
            """;

        await session.EnsureOpenAsync(cancellationToken);
        var total = await session.Connection.ExecuteScalarAsync<long>(
            new CommandDefinition(countSql, parameters, session.CurrentTransaction, cancellationToken: cancellationToken));
        var items = await session.Connection.QueryAsync<MovimientoValorVistaResponse>(
            new CommandDefinition(pageSql, parameters, session.CurrentTransaction, cancellationToken: cancellationToken));

        return new PagedResult<MovimientoValorVistaResponse>(items.AsList(), pagina.Pagina, pagina.TamanoPagina, total);
    }

    /// <summary>
    /// Existencia (<c>Σ MovimientosProducto.Cantidad</c>) y valor (<c>Σ MovimientosValor.ImporteCosto</c>) por producto y almacén
    /// con <c>FechaRegistro &lt;= fecha</c>: las mismas sumas que <c>IConsultaInventario.ExistenciaAsync</c> y que el saldo que el
    /// batch de costo lleva a la cuenta de inventario. <c>FULL JOIN</c> de ambos agregados: un par con valor y sin cantidad (o al
    /// revés) también se muestra.
    /// </summary>
    public async Task<ExistenciasVistaResponse> ListExistenciasAsync(
        ExistenciaVistaCriterios criterios, PageRequest paginacion, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(criterios);
        var fecha = criterios.Fecha ?? throw new ArgumentException("La fecha de corte es obligatoria.", nameof(criterios));
        var pagina = paginacion.Normalizar();
        var parameters = new DynamicParameters();
        parameters.Add("Fecha", fecha);

        var libro = new List<string> { "\"FechaRegistro\" <= @Fecha" };
        if (criterios.ProductoId is { } productoId)
        {
            libro.Add("\"ProductoId\" = @ProductoId");
            parameters.Add("ProductoId", productoId);
        }

        if (criterios.AlmacenId is { } almacenId)
        {
            libro.Add("\"AlmacenId\" = @AlmacenId");
            parameters.Add("AlmacenId", almacenId);
        }

        if (!string.IsNullOrWhiteSpace(criterios.Texto))
        {
            libro.Add("""
                "ProductoId" IN (SELECT pt."Id" FROM "Productos" pt
                                 WHERE pt."Codigo" ILIKE @Texto ESCAPE '\' OR pt."Nombre" ILIKE @Texto ESCAPE '\')
                """);
            parameters.Add("Texto", $"%{FilterExpressionBuilder.EscaparMetacaracteresLike(criterios.Texto.Trim())}%");
        }

        // Original: var exterior = criterios.SoloConExistencia ? "WHERE \"Existencia\" <> 0" : string.Empty;
        // A zero physical balance can still carry a residual valuation that reconciliation must display.
        var exterior = criterios.SoloConExistencia ? "WHERE \"Existencia\" <> 0 OR \"Valor\" <> 0" : string.Empty;

        var filasSql = $"""
            WITH mp AS (
                SELECT "ProductoId", "AlmacenId", SUM("Cantidad") AS "Existencia"
                FROM "MovimientosProducto" {Where(libro)}
                GROUP BY "ProductoId", "AlmacenId"
            ), mv AS (
                SELECT "ProductoId", "AlmacenId", SUM("ImporteCosto") AS "Valor"
                FROM "MovimientosValor" {Where(libro)}
                GROUP BY "ProductoId", "AlmacenId"
            ), e AS (
                SELECT COALESCE(mp."ProductoId", mv."ProductoId") AS "ProductoId",
                       COALESCE(mp."AlmacenId", mv."AlmacenId") AS "AlmacenId",
                       COALESCE(mp."Existencia", 0) AS "Existencia", COALESCE(mv."Valor", 0) AS "Valor"
                FROM mp FULL JOIN mv ON mv."ProductoId" = mp."ProductoId" AND mv."AlmacenId" = mp."AlmacenId"
            ), f AS (
                SELECT e."ProductoId", p."Codigo" AS "ProductoCodigo", p."Nombre" AS "ProductoNombre",
                       ub."Codigo" AS "UnidadBaseCodigo", e."AlmacenId", al."Codigo" AS "AlmacenCodigo",
                       al."Nombre" AS "AlmacenNombre", e."Existencia", e."Valor",
                       CASE WHEN e."Existencia" > 0 THEN ROUND(e."Valor" / e."Existencia", 6) END AS "CostoMedio"
                FROM e
                JOIN "Productos" p ON p."Id" = e."ProductoId"
                JOIN "Almacenes" al ON al."Id" = e."AlmacenId"
                LEFT JOIN "UnidadesMedida" ub ON ub."Id" = p."UnidadMedidaBaseId"
            )
            """;

        // Por defecto, por código de producto y de almacén (ascendente salvo que se pida lo contrario); desempate estable por
        // los ids del par (las filas agregadas no tienen Id propio).
        var ordenColumna = ColumnasExistencias.EsValida(pagina.OrdenarPor) ? pagina.OrdenarPor! : "ProductoCodigo";
        var direccion = pagina.Descendente ? "DESC" : "ASC";
        // La columna pedida y los códigos sin repetir (el orden por defecto ya es ProductoCodigo), y los ids del par.
        var ordenSql = string.Join(", ", new[] { ordenColumna, "ProductoCodigo", "AlmacenCodigo" }
            .Distinct(StringComparer.Ordinal)
            .Select(c => $"{ColumnasExistencias.Citar(c)} {direccion}")
            .Append($"\"ProductoId\" {direccion}")
            .Append($"\"AlmacenId\" {direccion}"));
        parameters.Add("TamanoPagina", pagina.TamanoPagina);
        parameters.Add("Offset", pagina.Offset);

        var totalesSql = $"""
            {filasSql}
            SELECT COUNT(*) AS "Total", COALESCE(SUM("Valor"), 0) AS "ValorTotal" FROM f {exterior}
            """;

        var pageSql = $"""
            {filasSql}
            SELECT * FROM f {exterior}
            ORDER BY {ordenSql}
            LIMIT @TamanoPagina OFFSET @Offset
            """;

        await session.EnsureOpenAsync(cancellationToken);
        var (total, valorTotal) = await session.Connection.QuerySingleAsync<(long, decimal)>(
            new CommandDefinition(totalesSql, parameters, session.CurrentTransaction, cancellationToken: cancellationToken));
        var items = await session.Connection.QueryAsync<ExistenciaVistaResponse>(
            new CommandDefinition(pageSql, parameters, session.CurrentTransaction, cancellationToken: cancellationToken));

        return new ExistenciasVistaResponse(
            new PagedResult<ExistenciaVistaResponse>(items.AsList(), pagina.Pagina, pagina.TamanoPagina, total), fecha, valorTotal);
    }

    /// <summary>
    /// Filtros de los dos libros (mismas columnas en ambas tablas). Producto, almacén y fechas van siempre a
    /// <paramref name="interior"/>; tipo, origen y documento, a <paramref name="secundarios"/> (el exterior de la ventana del saldo
    /// acumulado, o la misma lista si no hay ventana). Columnas sin alias: cada consulta los aplica sobre una sola tabla.
    /// </summary>
    private static void AgregarFiltrosComunes(
        List<string> interior, List<string> secundarios, DynamicParameters parameters, ColumnasPermitidas columnas,
        Guid? productoId, Guid? almacenId, DateOnly? desde, DateOnly? hasta, int? tipoMovimiento, int? tipoOrigen,
        string? numeroDocumento)
    {
        if (productoId is { } p)
        {
            interior.Add("\"ProductoId\" = @ProductoId");
            parameters.Add("ProductoId", p);
        }

        if (almacenId is { } a)
        {
            interior.Add("\"AlmacenId\" = @AlmacenId");
            parameters.Add("AlmacenId", a);
        }

        if (desde is { } d)
        {
            interior.Add("\"FechaRegistro\" >= @Desde");
            parameters.Add("Desde", d);
        }

        if (hasta is { } h)
        {
            interior.Add("\"FechaRegistro\" <= @Hasta");
            parameters.Add("Hasta", h);
        }

        if (tipoMovimiento is { } tm)
        {
            secundarios.Add("\"TipoMovimiento\" = @TipoMovimiento");
            parameters.Add("TipoMovimiento", (short)tm);
        }

        if (tipoOrigen is { } to)
        {
            secundarios.Add("\"TipoOrigen\" = @TipoOrigen");
            parameters.Add("TipoOrigen", (short)to);
        }

        FilterExpressionBuilder.AddTextFilter(secundarios, parameters, columnas, "NumeroDocumento", numeroDocumento);
    }

    /// <summary>
    /// Orden de la página (columna de la allow-list o <paramref name="porDefecto"/>, con desempate por <c>Id</c> en la misma
    /// dirección) cualificado con el alias interior y con el de la consulta final que une los maestros.
    /// </summary>
    private static (string Interior, string Pagina) Orden(
        ColumnasPermitidas columnas, PageRequest pagina, string porDefecto, string aliasInterior, string aliasPagina)
    {
        var columna = columnas.Citar(columnas.EsValida(pagina.OrdenarPor) ? pagina.OrdenarPor! : porDefecto);
        var direccion = pagina.Descendente ? "DESC" : "ASC";
        return ($"{aliasInterior}.{columna} {direccion}, {aliasInterior}.\"Id\" {direccion}",
                $"{aliasPagina}.{columna} {direccion}, {aliasPagina}.\"Id\" {direccion}");
    }

    private static string Where(IReadOnlyCollection<string> filtros) =>
        filtros.Count == 0 ? string.Empty : "WHERE " + string.Join(" AND ", filtros);
}
