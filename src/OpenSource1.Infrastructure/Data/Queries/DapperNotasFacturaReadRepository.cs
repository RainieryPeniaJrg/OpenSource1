using System.Data;
using Dapper;
using OpenSource1.Application.Data;
using OpenSource1.Application.Features.FacturasVenta.Notas;
using OpenSource1.Application.Features.NotasCreditoVenta.Borradores.Dtos;
using OpenSource1.Application.Features.NotasCreditoVenta.Posteadas.Dtos;

namespace OpenSource1.Infrastructure.Data.Queries;

/// <summary>Three fixed queries, exact invoice match, no hidden page limit, one repeatable-read snapshot.</summary>
public sealed class DapperNotasFacturaReadRepository(IDbSession session) : INotasFacturaReadRepository
{
    public async Task<NotasFacturaResponse?> GetAsync(string numero, CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(numero);
        if (session.HayTransaccionActiva)
        {
            throw new InvalidOperationException("La consulta de notas de factura requiere su propia instantánea de lectura.");
        }

        await session.EnsureOpenAsync(cancellationToken);
        await using var transaction = await session.Connection.BeginTransactionAsync(IsolationLevel.RepeatableRead, cancellationToken);
        var parameters = new { Numero = numero };
        // LEFT JOIN leaves one sentinel row for an existing invoice without notes, and no rows for a missing invoice.
        var rows = (await session.Connection.QueryAsync<string, NotaCreditoVentaResponse, NotaCreditoVentaResponse?>(
            new CommandDefinition("""
                SELECT f."Numero" AS "FacturaConsultada", n."Numero" AS "NotaInicio", n.*,
                       sv."Codigo" AS "SocioNegocioCodigo", sv."NombreComercial" AS "SocioNegocioNombre",
                       sf."Codigo" AS "SocioNegocioFacturarACodigo", r."NumeroRegistro" AS "NumeroRegistroContable",
                       nb."Id" AS "NotaCreditoVentaBorradorId",
                       (SELECT COUNT(*) FROM "LineasNotaCreditoVenta" l WHERE l."NotaCreditoVentaNumero" = n."Numero")::int AS "NumeroLineas"
                FROM "FacturasVenta" f
                LEFT JOIN "NotasCreditoVenta" n ON n."FacturaVentaNumero" = f."Numero"
                LEFT JOIN "SociosNegocio" sv ON sv."Id" = n."SocioNegocioId"
                LEFT JOIN "SociosNegocio" sf ON sf."Id" = n."SocioNegocioFacturarAId"
                LEFT JOIN "RegistrosContables" r ON r."Id" = n."RegistroContableId"
                LEFT JOIN "NotasCreditoVentaBorrador" nb ON nb."NotaCreditoVentaNumero" = n."Numero" AND nb."IsDeleted" = false
                WHERE f."Numero" = @Numero
                ORDER BY n."Numero" COLLATE "C"
                """, parameters, transaction, cancellationToken: cancellationToken), (_, nota) => nota, splitOn: "NotaInicio")).AsList();
        if (rows.Count == 0)
        {
            return null;
        }

        var lines = await session.Connection.QueryAsync<CreditoLineaFacturaResponse>(new CommandDefinition("""
            SELECT l."LineaFacturaVentaId", SUM(l."Cantidad") AS "Cantidad", SUM(l."ImporteLinea") AS "Importe",
                   SUM(CASE WHEN l."DevolverInventario" THEN l."Cantidad" ELSE 0 END) AS "Devuelto"
            FROM "LineasNotaCreditoVenta" l
            JOIN "NotasCreditoVenta" n ON n."Numero" = l."NotaCreditoVentaNumero"
            WHERE n."FacturaVentaNumero" = @Numero
            GROUP BY l."LineaFacturaVentaId"
            ORDER BY l."LineaFacturaVentaId"
            """, parameters, transaction, cancellationToken: cancellationToken));

        var drafts = await session.Connection.QueryAsync<NotaCreditoVentaBorradorResponse>(new CommandDefinition("""
            SELECT n.*, f."ImporteTotal" AS "FacturaImporteTotal", f."FechaRegistro" AS "FacturaFechaRegistro",
                   sv."Codigo" AS "SocioNegocioCodigo", sv."NombreComercial" AS "SocioNegocioNombre",
                   sf."Codigo" AS "SocioNegocioFacturarACodigo", c."Numero" AS "CuentaCxCNumero",
                   sb."Codigo" AS "SerieBorradorCodigo", sr."Codigo" AS "SerieRegistroCodigo",
                   n.xmin::text::bigint AS "Xmin",
                   (SELECT COUNT(*) FROM "LineasNotaCreditoVentaBorrador" l
                    WHERE l."NotaCreditoVentaBorradorId" = n."Id" AND l."IsDeleted" = false)::int AS "NumeroLineas"
            FROM "NotasCreditoVentaBorrador" n
            JOIN "FacturasVenta" f ON f."Numero" = n."FacturaVentaNumero"
            LEFT JOIN "SociosNegocio" sv ON sv."Id" = n."SocioNegocioId"
            LEFT JOIN "SociosNegocio" sf ON sf."Id" = n."SocioNegocioFacturarAId"
            LEFT JOIN "CuentasContables" c ON c."Id" = n."CuentaCxCId"
            LEFT JOIN "Series" sb ON sb."Id" = n."SerieBorradorId"
            LEFT JOIN "Series" sr ON sr."Id" = n."SerieRegistroId"
            WHERE n."FacturaVentaNumero" = @Numero AND n."IsDeleted" = false AND n."Estado" = 1
            ORDER BY n."CreatedAtUtc", n."Id"
            """, parameters, transaction, cancellationToken: cancellationToken));

        await transaction.CommitAsync(cancellationToken);
        // Dapper may materialize the null side of the LEFT JOIN when its split sentinel is not a DTO property.
        // A legal note always has a nonempty number; keep the invoice-presence row out of the document list.
        return new NotasFacturaResponse(rows.OfType<NotaCreditoVentaResponse>().Where(n => !string.IsNullOrEmpty(n.Numero)).ToList(), lines.AsList(), drafts.AsList());
    }
}
