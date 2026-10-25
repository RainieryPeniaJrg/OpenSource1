using Dapper;
using OpenSource1.Application.Data;
using OpenSource1.Application.Features.Busqueda;
using OpenSource1.Application.Features.Busqueda.Dtos;

namespace OpenSource1.Infrastructure.Data.Queries;

/// <summary>
/// Búsqueda global (Fix-Features A3). Un SELECT por tipo con <c>LIMIT @Limite</c>; el término se escapa con
/// <see cref="FilterExpressionBuilder.EscaparMetacaracteresLike"/> y se envuelve en <c>%…%</c> (sin sintaxis de comodines del
/// usuario: todo es literal). Socios, productos y borradores excluyen el borrado lógico; los documentos posteados no lo tienen. Los
/// borradores <c>Posteada</c> (3) de factura y de nota tampoco salen (spec no-series, ruling F14): se encuentran por su documento.
/// </summary>
public sealed class DapperBusquedaGlobalRepository(IDbSession session) : IBusquedaGlobalRepository
{
    private const string SqlClientes = """
        SELECT s."Id"::text AS "Id", s."NombreComercial" AS "Titulo",
               concat_ws(' · ', s."Codigo", NULLIF(s."RazonSocial", ''), NULLIF(s."NumeroDocumentoFiscal", '')) AS "Subtitulo"
        FROM "SociosNegocio" s
        WHERE s."IsDeleted" = false
          AND (s."Codigo" ILIKE @Patron ESCAPE '\' OR s."NombreComercial" ILIKE @Patron ESCAPE '\'
               OR s."RazonSocial" ILIKE @Patron ESCAPE '\' OR s."NumeroDocumentoFiscal" ILIKE @Patron ESCAPE '\')
        ORDER BY s."NombreComercial", s."Id"
        LIMIT @Limite
        """;

    private const string SqlProductos = """
        SELECT p."Id"::text AS "Id", p."Nombre" AS "Titulo", p."Codigo" AS "Subtitulo"
        FROM "Productos" p
        WHERE p."IsDeleted" = false
          AND (p."Codigo" ILIKE @Patron ESCAPE '\' OR p."Nombre" ILIKE @Patron ESCAPE '\')
        ORDER BY p."Nombre", p."Id"
        LIMIT @Limite
        """;

    private const string SqlFacturas = """
        SELECT f."Numero" AS "Id", f."Numero" AS "Titulo",
               concat_ws(' · ', f."NombreFacturacion", to_char(f."FechaRegistro", 'YYYY-MM-DD')) AS "Subtitulo"
        FROM "FacturasVenta" f
        WHERE f."Numero" ILIKE @Patron ESCAPE '\' OR f."NombreFacturacion" ILIKE @Patron ESCAPE '\'
        ORDER BY f."Numero" DESC
        LIMIT @Limite
        """;

    private const string SqlBorradoresFactura = """
        SELECT b."Id"::text AS "Id", b."Numero" AS "Titulo", b."NombreFacturacion" AS "Subtitulo"
        FROM "FacturasVentaBorrador" b
        WHERE b."IsDeleted" = false AND b."Estado" <> 3
          AND (b."Numero" ILIKE @Patron ESCAPE '\' OR b."NombreFacturacion" ILIKE @Patron ESCAPE '\')
        ORDER BY b."CreatedAtUtc" DESC, b."Id"
        LIMIT @Limite
        """;

    private const string SqlNotasCredito = """
        SELECT n."Numero" AS "Id", n."Numero" AS "Titulo",
               concat_ws(' · ', n."NombreFacturacion", 'Factura ' || n."FacturaVentaNumero") AS "Subtitulo"
        FROM "NotasCreditoVenta" n
        WHERE n."Numero" ILIKE @Patron ESCAPE '\'
        ORDER BY n."Numero" DESC
        LIMIT @Limite
        """;

    private const string SqlBorradoresNotaCredito = """
        SELECT n."Id"::text AS "Id", n."Numero" AS "Titulo",
               concat_ws(' · ', n."NombreFacturacion", 'Factura ' || n."FacturaVentaNumero") AS "Subtitulo"
        FROM "NotasCreditoVentaBorrador" n
        WHERE n."IsDeleted" = false AND n."Estado" <> 3 AND n."Numero" ILIKE @Patron ESCAPE '\'
        ORDER BY n."CreatedAtUtc" DESC, n."Id"
        LIMIT @Limite
        """;

    public async Task<BusquedaGlobalResponse> BuscarAsync(string termino, int limite, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(termino);
        var parametros = new { Patron = $"%{FilterExpressionBuilder.EscaparMetacaracteresLike(termino)}%", Limite = limite };
        await session.EnsureOpenAsync(cancellationToken);

        return new BusquedaGlobalResponse(
        [
            await GrupoAsync(TiposResultadoBusqueda.Clientes, "Clientes", SqlClientes, id => $"/clientes/{id}"),
            await GrupoAsync(TiposResultadoBusqueda.Productos, "Productos", SqlProductos, id => $"/productos/{id}"),
            await GrupoAsync(TiposResultadoBusqueda.Facturas, "Facturas", SqlFacturas, id => $"/facturas-venta/{Uri.EscapeDataString(id)}"),
            await GrupoAsync(TiposResultadoBusqueda.BorradoresFactura, "Facturas en preparación", SqlBorradoresFactura, id => $"/facturas-venta/en-preparacion/{id}"),
            await GrupoAsync(TiposResultadoBusqueda.NotasCredito, "Notas de crédito", SqlNotasCredito, id => $"/notas-credito-venta/{Uri.EscapeDataString(id)}"),
            await GrupoAsync(TiposResultadoBusqueda.BorradoresNotaCredito, "Notas de crédito en preparación", SqlBorradoresNotaCredito, id => $"/notas-credito-venta/en-preparacion/{id}"),
        ]);

        async Task<GrupoResultadosBusqueda> GrupoAsync(string tipo, string titulo, string sql, Func<string, string> ruta)
        {
            var filas = await session.Connection.QueryAsync<Fila>(
                new CommandDefinition(sql, parametros, session.CurrentTransaction, cancellationToken: cancellationToken));
            return new GrupoResultadosBusqueda(
                tipo, titulo, filas.Select(f => new ResultadoBusqueda(tipo, f.Id, f.Titulo, f.Subtitulo, ruta(f.Id))).ToList());
        }
    }

    private sealed class Fila
    {
        public string Id { get; set; } = string.Empty;
        public string Titulo { get; set; } = string.Empty;
        public string? Subtitulo { get; set; }
    }
}
