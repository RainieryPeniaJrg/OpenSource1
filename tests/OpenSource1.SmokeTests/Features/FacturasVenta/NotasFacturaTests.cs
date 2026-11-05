using System.Net;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Mvc.Testing;
using Moq;
using Npgsql;
using OpenSource1.Application.Data;
using OpenSource1.Application.Features.FacturasVenta.Notas;
using OpenSource1.Infrastructure.Data;
using OpenSource1.Infrastructure.Data.Queries;
using OpenSource1.SmokeTests.TestInfrastructure;

namespace OpenSource1.SmokeTests.Features.FacturasVenta;

[Collection(PostgresCollection.Name)]
public sealed class NotasFacturaTests(PostgresTestFixture fixture) : IClassFixture<PostgresTestFixture>
{
    static NotasFacturaTests() => SqlMapper.AddTypeHandler(new DapperDateOnlyTypeHandler());

    [Fact]
    public async Task Resumen_TraeMasDe200NotasYBorradores_Exactos_YAgregaCreditoYDevolucion()
    {
        await using var db = new NpgsqlConnection(fixture.AppConnectionString);
        await db.OpenAsync();
        await CrearTablasAsync(db);
        await db.ExecuteAsync("""
            INSERT INTO "FacturasVenta" VALUES ('FV1',1000,'2026-01-01'),('FV10',1000,'2026-01-01'),('VACIA',0,'2026-01-01');
            INSERT INTO "NotasCreditoVenta" ("Numero","FacturaVentaNumero")
                SELECT 'NC'||lpad(i::text,4,'0'),'FV1' FROM generate_series(1,201) i;
            INSERT INTO "NotasCreditoVenta" ("Numero","FacturaVentaNumero") VALUES ('NC0','FV10');
            INSERT INTO "LineasNotaCreditoVenta" VALUES ('NC0001',10,2,20,true),('NC0002',10,1,10,false),('NC0201',11,4,40,true),('NC0',10,99,990,true);
            INSERT INTO "NotasCreditoVentaBorrador" ("Id","Numero","FacturaVentaNumero","Estado","IsDeleted","CreatedAtUtc")
                SELECT md5(i::text)::uuid,'B'||i,'FV1',1,false,'2026-01-01' FROM generate_series(1,201) i;
            INSERT INTO "NotasCreditoVentaBorrador" ("Id","Numero","FacturaVentaNumero","Estado","IsDeleted","CreatedAtUtc") VALUES
                (md5('otro')::uuid,'OTRO','FV10',1,false,'2026-01-01'),
                (md5('borrado')::uuid,'BORRADO','FV1',1,true,'2026-01-01'),
                (md5('posteado')::uuid,'POSTEADO','FV1',3,false,'2026-01-01');
            """);

        var repo = new DapperNotasFacturaReadRepository(Sesion(db));
        var resumen = await repo.GetAsync("FV1");

        Assert.NotNull(resumen);
        Assert.Equal(201, resumen.Notas.Count);
        Assert.Equal(201, resumen.Borradores.Count);
        Assert.DoesNotContain(resumen.Notas, n => n.Numero == "NC0");
        Assert.DoesNotContain(resumen.Borradores, b => b.Numero is "OTRO" or "BORRADO" or "POSTEADO");
        var first = Assert.Single(resumen.Lineas, l => l.LineaFacturaVentaId == 10);
        Assert.Equal((3m,30m,2m), (first.Cantidad,first.Importe,first.Devuelto));
        var last = Assert.Single(resumen.Lineas, l => l.LineaFacturaVentaId == 11);
        Assert.Equal((4m,40m,4m), (last.Cantidad,last.Importe,last.Devuelto));
        Assert.Null(await repo.GetAsync("NO-EXISTE"));
        Assert.Null(await repo.GetAsync("FV1' OR 1=1 --"));
        var empty = await repo.GetAsync("VACIA");
        Assert.NotNull(empty);
        Assert.Empty(empty.Notas);
        Assert.Empty(empty.Lineas);
        Assert.Empty(empty.Borradores);
    }

    private static IDbSession Sesion(NpgsqlConnection db)
    {
        var session = new Mock<IDbSession>();
        session.SetupGet(s => s.Connection).Returns(db);
        session.Setup(s => s.EnsureOpenAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        return session.Object;
    }

    private static Task CrearTablasAsync(NpgsqlConnection db) => db.ExecuteAsync("""
        CREATE TEMP TABLE "FacturasVenta" ("Numero" text,"ImporteTotal" numeric,"FechaRegistro" date);
        CREATE TEMP TABLE "SociosNegocio" ("Id" uuid,"Codigo" text,"NombreComercial" text);
        CREATE TEMP TABLE "RegistrosContables" ("Id" bigint,"NumeroRegistro" text);
        CREATE TEMP TABLE "CuentasContables" ("Id" uuid,"Numero" text);
        CREATE TEMP TABLE "Series" ("Id" uuid,"Codigo" text);
        CREATE TEMP TABLE "NotasCreditoVenta" ("Numero" text,"FacturaVentaNumero" text,"SocioNegocioId" uuid,"SocioNegocioFacturarAId" uuid,"RegistroContableId" bigint);
        CREATE TEMP TABLE "LineasNotaCreditoVenta" ("NotaCreditoVentaNumero" text,"LineaFacturaVentaId" bigint,"Cantidad" numeric,"ImporteLinea" numeric,"DevolverInventario" boolean);
        CREATE TEMP TABLE "NotasCreditoVentaBorrador" ("Id" uuid,"Numero" text,"FacturaVentaNumero" text,"SocioNegocioId" uuid,"SocioNegocioFacturarAId" uuid,"CuentaCxCId" uuid,"SerieBorradorId" uuid,"SerieRegistroId" uuid,"Estado" smallint,"IsDeleted" boolean,"NotaCreditoVentaNumero" text,"CreatedAtUtc" timestamp);
        CREATE TEMP TABLE "LineasNotaCreditoVentaBorrador" ("NotaCreditoVentaBorradorId" uuid,"IsDeleted" boolean);
        """);
}
