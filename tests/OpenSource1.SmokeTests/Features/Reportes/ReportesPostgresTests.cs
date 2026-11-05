using Dapper;
using Moq;
using Npgsql;
using OpenSource1.Application.Data;
using OpenSource1.Application.Features.MovimientosCliente;
using OpenSource1.Application.Features.Reportes;
using OpenSource1.Infrastructure.Data.Queries;
using OpenSource1.Infrastructure.Data;
using OpenSource1.SmokeTests.TestInfrastructure;

namespace OpenSource1.SmokeTests.Features.Reportes;

[Collection(PostgresCollection.Name)]
public sealed class ReportesPostgresTests(PostgresTestFixture fixture) : IClassFixture<PostgresTestFixture>
{
    static ReportesPostgresTests() => SqlMapper.AddTypeHandler(new DapperDateOnlyTypeHandler());
    [Fact]
    public async Task Api_ProtegeLosCuatroReportes_YRechazaFiltrosInvalidos()
    {
        await using var factory = fixture.CreateFactory();
        using var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        foreach (var tipo in new[] { "ventas", "inventario", "top-productos", "cuentas-por-cobrar" })
        {
            using var anon = new HttpRequestMessage(HttpMethod.Get, $"/api/reportes/{tipo}");
            anon.Headers.Add("X-Test-Anonymous", "true");
            Assert.Equal(System.Net.HttpStatusCode.Unauthorized, (await client.SendAsync(anon)).StatusCode);
            using var forbidden = new HttpRequestMessage(HttpMethod.Get, $"/api/reportes/{tipo}");
            forbidden.Headers.Add("X-Test-Roles", "Desconocido");
            Assert.Equal(System.Net.HttpStatusCode.Forbidden, (await client.SendAsync(forbidden)).StatusCode);
            Assert.Equal(System.Net.HttpStatusCode.BadRequest, (await client.GetAsync($"/api/reportes/{tipo}?estado=invalido")).StatusCode);
        }
        Assert.Equal(System.Net.HttpStatusCode.OK, (await client.GetAsync("/api/reportes/ventas")).StatusCode);
    }
    [Fact]
    public async Task Inventario_DerivaCantidadYValorHistoricos_ConFiltros()
    {
        await using var db = new NpgsqlConnection(fixture.AppConnectionString);
        await db.OpenAsync();
        var producto = Guid.NewGuid(); var almacen = Guid.NewGuid();
        await db.ExecuteAsync("""
            CREATE TEMP TABLE "Productos" ("Id" uuid, "Codigo" text, "Nombre" text, "UnidadMedidaBaseId" uuid);
            CREATE TEMP TABLE "Almacenes" ("Id" uuid, "Codigo" text);
            CREATE TEMP TABLE "UnidadesMedida" ("Id" uuid, "Codigo" text);
            CREATE TEMP TABLE "MovimientosProducto" ("ProductoId" uuid, "AlmacenId" uuid, "FechaRegistro" date, "Cantidad" numeric);
            CREATE TEMP TABLE "MovimientosValor" ("ProductoId" uuid, "AlmacenId" uuid, "FechaRegistro" date, "ImporteCosto" numeric);
            INSERT INTO "Productos" VALUES (@producto,'P1','Producto',NULL);
            INSERT INTO "Almacenes" VALUES (@almacen,'A1');
            INSERT INTO "MovimientosProducto" VALUES (@producto,@almacen,'2026-01-01',10),(@producto,@almacen,'2026-01-31',-3),(@producto,@almacen,'2026-02-01',100);
            INSERT INTO "MovimientosValor" VALUES (@producto,@almacen,'2026-01-01',100),(@producto,@almacen,'2026-01-31',-30),(@producto,@almacen,'2026-02-01',1000);
            """, new { producto, almacen });
        var session = new Mock<IDbSession>(); session.SetupGet(s => s.Connection).Returns(db);
        session.Setup(s => s.EnsureOpenAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var repo = new DapperReportesReadRepository(session.Object, Mock.Of<IMovimientoClienteReadRepository>());
        var r = await repo.InventarioAsync(new(Hasta: new(2026, 1, 31), ProductoId: producto, AlmacenId: almacen), default);
        Assert.Equal(7, Assert.Single(r.Pagina.Items).Existencia);
        Assert.Equal(70, r.Totales.Valor);
        var sin = await repo.InventarioAsync(new(Hasta: new(2026, 1, 31), Estado: "sin-existencia"), default);
        Assert.Empty(sin.Pagina.Items);
    }

    [Fact]
    public async Task TopProductos_NormalizaUnidadYNeteaDevolucion_SinIncluirCuentas()
    {
        await using var db = new NpgsqlConnection(fixture.AppConnectionString);
        await db.OpenAsync(); var producto = Guid.NewGuid(); var cliente = Guid.NewGuid();
        await db.ExecuteAsync("""
            CREATE TEMP TABLE "Productos" ("Id" uuid, "Codigo" text, "Nombre" text, "UnidadMedidaBaseId" uuid);
            CREATE TEMP TABLE "UnidadesMedida" ("Id" uuid, "Codigo" text);
            CREATE TEMP TABLE "FacturasVenta" ("Numero" text, "FechaRegistro" date, "SocioNegocioFacturarAId" uuid, "Moneda" text);
            CREATE TEMP TABLE "NotasCreditoVenta" (LIKE "FacturasVenta");
            CREATE TEMP TABLE "LineasFacturaVenta" ("FacturaVentaNumero" text, "ProductoId" uuid, "AlmacenId" uuid,
                "Cantidad" numeric, "CantidadPorUnidadMedida" numeric, "ImporteLinea" numeric);
            CREATE TEMP TABLE "LineasNotaCreditoVenta" ("NotaCreditoVentaNumero" text, "ProductoId" uuid, "AlmacenId" uuid,
                "Cantidad" numeric, "CantidadPorUnidadMedida" numeric, "ImporteLinea" numeric);
            INSERT INTO "Productos" VALUES (@producto,'P1','Producto',NULL);
            INSERT INTO "FacturasVenta" VALUES ('F1','2026-01-01',@cliente,'DOP');
            INSERT INTO "NotasCreditoVenta" VALUES ('N1','2026-01-31',@cliente,'DOP');
            INSERT INTO "LineasFacturaVenta" VALUES ('F1',@producto,NULL,2,12,240),('F1',NULL,NULL,1,1,999);
            INSERT INTO "LineasNotaCreditoVenta" VALUES ('N1',@producto,NULL,1,12,120);
            """, new { producto, cliente });
        var session = new Mock<IDbSession>(); session.SetupGet(s => s.Connection).Returns(db);
        session.Setup(s => s.EnsureOpenAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var repo = new DapperReportesReadRepository(session.Object, Mock.Of<IMovimientoClienteReadRepository>());
        var r = await repo.TopProductosAsync(new(Hasta: new(2026, 1, 31), ClienteId: cliente), default);
        var fila = Assert.Single(r.Pagina.Items);
        Assert.Equal(24, fila.CantidadVendida); Assert.Equal(12, fila.CantidadDevuelta);
        Assert.Equal(12, fila.CantidadNeta); Assert.Equal(120, fila.Neto);
        Assert.Equal(240, r.Totales.Ventas); Assert.Equal(120, r.Totales.Devoluciones);
    }
    [Fact]
    public async Task Ventas_IncluyeExtremosYDevoluciones_TotalesSuperanPagina()
    {
        await using var db = new NpgsqlConnection(fixture.AppConnectionString);
        await db.OpenAsync();
        await db.ExecuteAsync("""
            CREATE TEMP TABLE "FacturasVenta" ("Numero" text, "FechaRegistro" date, "SocioNegocioFacturarAId" uuid,
                "NombreFacturacion" text, "Moneda" text, "ImporteTotal" numeric);
            CREATE TEMP TABLE "NotasCreditoVenta" (LIKE "FacturasVenta");
            CREATE TEMP TABLE "LineasFacturaVenta" ("FacturaVentaNumero" text, "ProductoId" uuid, "AlmacenId" uuid);
            CREATE TEMP TABLE "LineasNotaCreditoVenta" ("NotaCreditoVentaNumero" text, "ProductoId" uuid, "AlmacenId" uuid);
            INSERT INTO "FacturasVenta" VALUES ('F1','2026-01-01','11111111-1111-1111-1111-111111111111','Cliente','DOP',100),
                ('F2','2026-01-31','11111111-1111-1111-1111-111111111111','Cliente','DOP',200),
                ('F3','2026-02-01','11111111-1111-1111-1111-111111111111','Cliente','DOP',999);
            INSERT INTO "NotasCreditoVenta" VALUES ('N1','2026-01-31','11111111-1111-1111-1111-111111111111','Cliente','DOP',30);
            """);
        var session = new Mock<IDbSession>();
        session.SetupGet(s => s.Connection).Returns(db);
        session.Setup(s => s.EnsureOpenAsync(It.IsAny<CancellationToken>())).Returns(Task.CompletedTask);
        var repo = new DapperReportesReadRepository(session.Object, Mock.Of<IMovimientoClienteReadRepository>());
        var r = await repo.VentasAsync(new(Desde: new(2026, 1, 1), Hasta: new(2026, 1, 31), TamanoPagina: 1), default);
        Assert.Single(r.Pagina.Items);
        Assert.Equal(3, r.Pagina.Total);
        Assert.Equal(300, r.Totales.Ventas);
        Assert.Equal(30, r.Totales.Devoluciones);
        Assert.Equal(270, r.Totales.Neto);
        var empty = await repo.VentasAsync(new(Desde: new(2026, 1, 1), Hasta: new(2026, 1, 31), Pagina: 20, TamanoPagina: 1), default);
        Assert.Empty(empty.Pagina.Items);
        Assert.Equal(r.Totales, empty.Totales);
        var cliente = await repo.VentasAsync(new(Hasta: new(2026, 1, 31), ClienteId: Guid.NewGuid()), default);
        Assert.Empty(cliente.Pagina.Items);
        Assert.Equal(0, cliente.Totales.Neto);
    }
}
