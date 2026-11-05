using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OpenSource1.Api;
using OpenSource1.Application.Features.Inventario.Consultas.Dtos;
using OpenSource1.Application.Services.Contabilidad;
using OpenSource1.Core.Common;
using OpenSource1.Core.Entities.Contabilidad;
using OpenSource1.Core.Enums;
using OpenSource1.SmokeTests.TestInfrastructure;

namespace OpenSource1.SmokeTests.Api;

/// <summary>
/// Vistas de inventario (Task 7.2): <c>GET api/inventario/movimientos-producto</c>, <c>/movimientos-valor</c> y
/// <c>/existencias</c>, todas CanConsult. El libro se siembra con el registro real (<c>IRegistroMovimientosInventario</c>), la
/// rutina real de ajuste de costo y el batch real de costo de inventario. Cada test usa productos y almacenes propios. Review
/// Focus 1 (saldo acumulado entre páginas y con <c>desde</c>), 4 (existencia y valor = <c>IConsultaInventario</c> = saldo de la
/// 1301 tras el batch) y 5 (filtros inválidos → 400 o valor por defecto, nunca 500). REQUIERE DOCKER.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class InventarioVistasApiTests(PostgresTestFixture fixture) : IClassFixture<PostgresTestFixture>, IAsyncLifetime
{
    private const string UrlProducto = "/api/inventario/movimientos-producto";
    private const string UrlValor = "/api/inventario/movimientos-valor";
    private const string UrlExistencias = "/api/inventario/existencias";

    private static readonly DateOnly D1 = new(2026, 7, 1);
    private static readonly DateOnly D2 = new(2026, 7, 2);
    private static readonly DateOnly D3 = new(2026, 7, 3);
    private static readonly DateOnly D4 = new(2026, 7, 4);
    private static readonly DateOnly D5 = new(2026, 7, 5);

    private readonly LibroInventarioPrueba _prueba = new(fixture);
    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;

    public async Task InitializeAsync()
    {
        await _prueba.InicializarAsync();
        _factory = fixture.CreateFactory();
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        _client.DefaultRequestHeaders.Add("X-Test-User", "administrador");
        _client.DefaultRequestHeaders.Add("X-Test-Roles", "Administrador");
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
        await _prueba.DisposeAsync();
    }

    [Fact]
    public async Task Vistas_ExigenAutenticacion_YCanConsultBasta()
    {
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        foreach (var url in new[] { UrlProducto, UrlValor, UrlExistencias })
        {
            var anon = new HttpRequestMessage(HttpMethod.Get, url);
            anon.Headers.Add("X-Test-Anonymous", "true");
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(anon)).StatusCode);

            // Ejecutor (consultar + agregar, sin CanModify) consulta igual.
            var ejecutor = new HttpRequestMessage(HttpMethod.Get, url);
            ejecutor.Headers.Add("X-Test-User", "ejecutor");
            ejecutor.Headers.Add("X-Test-Roles", "Ejecutor");
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(ejecutor)).StatusCode);
        }
    }

    // ── Review Focus 1: saldo acumulado ─────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ReviewFocus1_SaldoAcumulado_CorrectoATravesDePaginas_ConDesde_YPorAlmacen()
    {
        var producto = await _prueba.SembrarProductoAsync();
        var a = await _prueba.SembrarAlmacenAsync();
        var b = await _prueba.SembrarAlmacenAsync();

        // Registrados FUERA de orden cronológico (D3 antes que D2): el orden del saldo es (FechaRegistro, Id), no el de alta.
        await _prueba.RegistrarOkAsync(LibroInventarioPrueba.Entrada(producto, a, 10m, 5m, D1));
        await _prueba.RegistrarOkAsync(LibroInventarioPrueba.Entrada(producto, b, 4m, 5m, D1));
        await _prueba.RegistrarOkAsync(LibroInventarioPrueba.Salida(producto, a, 3m, D3));
        await _prueba.RegistrarOkAsync(LibroInventarioPrueba.Entrada(producto, a, 1m, 5m, D2, unidadId: _prueba.UnidadCja)); // +12
        await _prueba.RegistrarOkAsync(LibroInventarioPrueba.Salida(producto, a, 2m, D2, tipo: TipoMovimientoInventario.Venta));
        await _prueba.RegistrarOkAsync(LibroInventarioPrueba.Salida(producto, b, 1m, D3));
        await _prueba.RegistrarOkAsync(LibroInventarioPrueba.Entrada(producto, a, 6m, 5m, D4));
        await _prueba.RegistrarOkAsync(LibroInventarioPrueba.Salida(producto, a, 5m, D4, tipo: TipoMovimientoInventario.Venta));
        await _prueba.RegistrarOkAsync(LibroInventarioPrueba.Salida(producto, a, 4m, D5));

        var libroA = await LibroAsync(producto, a);
        Assert.Equal(7, libroA.Count);
        var esperadoA = SaldosEsperados(libroA, saldoInicial: 0m);

        // Páginas de 2: la página 3 arranca con el saldo que dejó la 2 (la ventana se calcula antes de paginar).
        var vistos = new List<MovimientoProductoVistaResponse>();
        for (var pagina = 1; pagina <= 4; pagina++)
        {
            var resultado = await GetAsync<PagedResult<MovimientoProductoVistaResponse>>(
                $"{UrlProducto}?productoId={producto}&almacenId={a}&tamanoPagina=2&pagina={pagina}");
            Assert.Equal(7, resultado.Total);
            vistos.AddRange(resultado.Items);
        }

        Assert.Equal(libroA.Select(x => x.Id), vistos.Select(x => x.Id));
        Assert.Equal(esperadoA, vistos.Select(x => x.SaldoAcumulado!.Value));
        Assert.Equal(vistos[3].SaldoAcumulado + vistos[4].Cantidad, vistos[4].SaldoAcumulado); // cruce página 2 → 3
        Assert.Equal(await _prueba.ConsultarAsync(c => c.ExistenciaAsync(producto, a, null)), vistos[^1].SaldoAcumulado);
        Assert.Equal([10m, 22m, 20m, 17m, 23m, 18m, 14m], vistos.Select(x => x.SaldoAcumulado!.Value));

        // Con desde: el saldo inicial es lo anterior al rango (existencia a D2 - 1 día).
        var desdeD3 = await GetAsync<PagedResult<MovimientoProductoVistaResponse>>(
            $"{UrlProducto}?productoId={producto}&almacenId={a}&desde={D3:yyyy-MM-dd}&tamanoPagina=2&pagina=2");
        Assert.Equal(4, desdeD3.Total);
        var saldoAntesD3 = await _prueba.ConsultarAsync(c => c.ExistenciaAsync(producto, a, D2));
        Assert.Equal(20m, saldoAntesD3);
        Assert.Equal(
            SaldosEsperados([.. libroA.Where(x => x.FechaRegistro >= D3)], saldoAntesD3).Skip(2),
            desdeD3.Items.Select(x => x.SaldoAcumulado!.Value));

        // Con hasta: no cambia los saldos de las filas que quedan.
        var hastaD2 = await GetAsync<PagedResult<MovimientoProductoVistaResponse>>(
            $"{UrlProducto}?productoId={producto}&almacenId={a}&hasta={D2:yyyy-MM-dd}");
        Assert.Equal([10m, 22m, 20m], hastaD2.Items.Select(x => x.SaldoAcumulado!.Value));

        // Descendente: mismas filas y mismos saldos, en orden inverso.
        var descendente = await GetAsync<PagedResult<MovimientoProductoVistaResponse>>(
            $"{UrlProducto}?productoId={producto}&almacenId={a}&descendente=true");
        Assert.Equal(vistos.Select(x => (x.Id, x.SaldoAcumulado)).Reverse(), descendente.Items.Select(x => (x.Id, x.SaldoAcumulado)));

        // Filtro por tipo: solo oculta filas; el saldo sigue siendo la existencia real tras cada venta.
        var ventas = await GetAsync<PagedResult<MovimientoProductoVistaResponse>>(
            $"{UrlProducto}?productoId={producto}&almacenId={a}&tipoMovimiento={(int)TipoMovimientoInventario.Venta}");
        Assert.Equal([20m, 18m], ventas.Items.Select(x => x.SaldoAcumulado!.Value));

        // Sin almacén: el saldo es el del producto en todos los almacenes.
        var todos = await GetAsync<PagedResult<MovimientoProductoVistaResponse>>(
            $"{UrlProducto}?productoId={producto}&tamanoPagina=200");
        var libro = await LibroAsync(producto, null);
        Assert.Equal(9, todos.Total);
        Assert.Equal(SaldosEsperados(libro, 0m), todos.Items.Select(x => x.SaldoAcumulado!.Value));
        Assert.Equal(await _prueba.ConsultarAsync(c => c.ExistenciaAsync(producto, null, null)), todos.Items[^1].SaldoAcumulado);
    }

    [Fact]
    public async Task MovimientosProducto_SinProducto_NoDevuelveSaldo_YMuestraCodigosNombresYUnidades()
    {
        var producto = await _prueba.SembrarProductoAsync();
        var almacen = await _prueba.SembrarAlmacenAsync();
        var documento = $"VP{Guid.NewGuid():N}"[..20];
        await _prueba.RegistrarOkAsync(LibroInventarioPrueba.Entrada(producto, almacen, 2m, 3m, D1, unidadId: _prueba.UnidadCja) with
        {
            NumeroDocumento = documento,
        });
        await _prueba.RegistrarOkAsync(LibroInventarioPrueba.Salida(producto, almacen, 5m, D2, tipo: TipoMovimientoInventario.Venta) with
        {
            NumeroDocumento = documento, TipoOrigen = TipoOrigenMovimiento.FacturaVenta,
        });

        using var respuesta = await _client.GetAsync($"{UrlProducto}?almacenId={almacen}");
        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        var json = await respuesta.Content.ReadAsStringAsync();
        Assert.DoesNotContain("saldoAcumulado", json, StringComparison.OrdinalIgnoreCase);
        var pagina = JsonSerializer.Deserialize<PagedResult<MovimientoProductoVistaResponse>>(json, JsonSerializerOptions.Web)!;
        Assert.Equal(2, pagina.Total);

        var entrada = pagina.Items[0];
        var (codigoProducto, codigoAlmacen) = await CodigosAsync(producto, almacen);
        Assert.Equal((codigoProducto, "Producto de prueba del libro de inventario"), (entrada.ProductoCodigo, entrada.ProductoNombre));
        Assert.Equal((codigoAlmacen, "Almacén de prueba"), (entrada.AlmacenCodigo, entrada.AlmacenNombre));
        Assert.Equal((24m, 19m, "UND", "CJA", 12m), (entrada.Cantidad, entrada.CantidadRestante, entrada.UnidadBaseCodigo, entrada.UnidadMedidaCodigo, entrada.CantidadPorUnidadMedida));
        Assert.Equal((TipoMovimientoInventario.AjustePositivo, TipoOrigenMovimiento.Diario), (entrada.TipoMovimiento, entrada.TipoOrigen));

        var salida = pagina.Items[1];
        Assert.Equal((-5m, (decimal?)null, "UND"), (salida.Cantidad, salida.CantidadRestante, salida.UnidadMedidaCodigo));

        // Filtros secundarios: tipo, origen y documento (por contenido).
        Assert.Equal([salida.Id], (await GetAsync<PagedResult<MovimientoProductoVistaResponse>>(
            $"{UrlProducto}?almacenId={almacen}&tipoMovimiento=2")).Items.Select(x => x.Id));
        Assert.Equal([salida.Id], (await GetAsync<PagedResult<MovimientoProductoVistaResponse>>(
            $"{UrlProducto}?almacenId={almacen}&tipoOrigen=2")).Items.Select(x => x.Id));
        Assert.Equal(2, (await GetAsync<PagedResult<MovimientoProductoVistaResponse>>(
            $"{UrlProducto}?numeroDocumento={documento[2..12]}")).Total);
        Assert.Equal(0, (await GetAsync<PagedResult<MovimientoProductoVistaResponse>>(
            $"{UrlProducto}?almacenId={almacen}&tipoMovimiento=5")).Total);

        // Orden por una columna permitida (Cantidad ascendente) y por una no permitida (se ignora: orden por defecto).
        Assert.Equal([salida.Id, entrada.Id], (await GetAsync<PagedResult<MovimientoProductoVistaResponse>>(
            $"{UrlProducto}?almacenId={almacen}&ordenarPor=Cantidad")).Items.Select(x => x.Id));
        Assert.Equal([entrada.Id, salida.Id], (await GetAsync<PagedResult<MovimientoProductoVistaResponse>>(
            $"{UrlProducto}?almacenId={almacen}&ordenarPor={Uri.EscapeDataString("ProductoCodigo\"; DROP TABLE x --")}")).Items.Select(x => x.Id));
    }

    // ── Movimientos de valor ─────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task MovimientosValor_Columnas_SoloAjustes_YContabilizadoTrasElBatch()
    {
        var producto = await ProductoClasificadoAsync();
        var almacen = await _prueba.SembrarAlmacenAsync();
        await _prueba.RegistrarOkAsync(LibroInventarioPrueba.Entrada(producto, almacen, 10m, 10m, D1));
        await _prueba.RegistrarOkAsync(LibroInventarioPrueba.Salida(producto, almacen, 5m, D3, tipo: TipoMovimientoInventario.Venta) with
        {
            TipoOrigen = TipoOrigenMovimiento.FacturaVenta, ImporteVenta = 90m,
        });
        // Entrada retroactiva a otro costo: la rutina de ajuste inserta un movimiento de valor de ajuste (−25) sobre la venta.
        await _prueba.RegistrarOkAsync(LibroInventarioPrueba.Entrada(producto, almacen, 10m, 20m, D2));
        Assert.Equal(1, (await _prueba.AjustarOkAsync(producto)).MovimientosValorCreados);

        var todos = await GetAsync<PagedResult<MovimientoValorVistaResponse>>($"{UrlValor}?productoId={producto}");
        Assert.Equal(4, todos.Total);
        Assert.Equal([D1, D2, D3, D3], todos.Items.Select(x => x.FechaRegistro));
        Assert.All(todos.Items, x => Assert.False(x.Contabilizado));
        var venta = todos.Items[2];
        Assert.Equal((TipoMovimientoInventario.Venta, -5m, -50m, 10m, 90m, false, TipoValor.CostoDirecto, TipoOrigenMovimiento.FacturaVenta),
            (venta.TipoMovimiento, venta.CantidadValorada, venta.ImporteCosto, venta.CostoPorUnidad, venta.ImporteVenta, venta.Ajuste, venta.TipoValor, venta.TipoOrigen));
        Assert.NotNull(venta.MovimientoProductoId);
        var (codigoProducto, codigoAlmacen) = await CodigosAsync(producto, almacen);
        Assert.All(todos.Items, x => Assert.Equal((codigoProducto, codigoAlmacen), (x.ProductoCodigo, x.AlmacenCodigo)));

        var ajustes = await GetAsync<PagedResult<MovimientoValorVistaResponse>>($"{UrlValor}?productoId={producto}&soloAjustes=true");
        var ajuste = Assert.Single(ajustes.Items);
        Assert.Equal((true, -25m, D3), (ajuste.Ajuste, ajuste.ImporteCosto, ajuste.FechaRegistro));

        var entradaD2 = await GetAsync<PagedResult<MovimientoValorVistaResponse>>(
            $"{UrlValor}?productoId={producto}&desde={D2:yyyy-MM-dd}&hasta={D2:yyyy-MM-dd}&tipoMovimiento=3");
        Assert.Equal((20m, 200m), (Assert.Single(entradaD2.Items).CostoPorUnidad, entradaD2.Items[0].ImporteCosto));
        Assert.Equal([venta.Id], (await GetAsync<PagedResult<MovimientoValorVistaResponse>>(
            $"{UrlValor}?productoId={producto}&tipoOrigen=2")).Items.Select(x => x.Id));
        Assert.Equal([ajuste.Id], (await GetAsync<PagedResult<MovimientoValorVistaResponse>>(
            $"{UrlValor}?productoId={producto}&tipoOrigen=3")).Items.Select(x => x.Id));
        Assert.Equal([-50m, -25m, 100m, 200m], (await GetAsync<PagedResult<MovimientoValorVistaResponse>>(
            $"{UrlValor}?productoId={producto}&ordenarPor=ImporteCosto")).Items.Select(x => x.ImporteCosto));

        Assert.Empty((await PostearCostoAsync(producto)).Pendientes);

        var trasBatch = await GetAsync<PagedResult<MovimientoValorVistaResponse>>($"{UrlValor}?productoId={producto}");
        Assert.All(trasBatch.Items, x => Assert.True(x.Contabilizado));
        Assert.All(trasBatch.Items, x => Assert.Equal(x.ImporteCosto, x.ImporteCostoPosteadoContabilidad));
    }

    // ── Review Focus 4: existencias y valor ─────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Existencias_SoloConExistencia_ConservaValorResidualSinCantidad()
    {
        var producto = await _prueba.SembrarProductoAsync();
        var almacen = await _prueba.SembrarAlmacenAsync();
        // A rounding entry can retain value after the physical quantity has reached zero.
        await using (var contexto = _prueba.NuevoContexto())
        {
            contexto.MovimientosValor.Add(new OpenSource1.Core.Entities.Inventario.MovimientoValor
            {
                ProductoId = producto, AlmacenId = almacen, TipoValor = TipoValor.Redondeo,
                TipoMovimiento = TipoMovimientoInventario.AjustePositivo, FechaRegistro = D1,
                ImporteCosto = 0.01m, Ajuste = true, TipoOrigen = TipoOrigenMovimiento.AjusteCosto,
                ClaveOrigen = Guid.NewGuid().ToString(), CreatedBy = "test", CreatedAtUtc = DateTimeOffset.UtcNow
            });
            await contexto.SaveChangesAsync();
        }
        var r = await GetAsync<ExistenciasVistaResponse>($"{UrlExistencias}?productoId={producto}&fecha={D1:yyyy-MM-dd}&soloConExistencia=true");
        var fila = Assert.Single(r.Pagina.Items);
        Assert.Equal(0m, fila.Existencia);
        Assert.Equal(0.01m, fila.Valor);
        Assert.Equal(0.01m, r.ValorTotal);
        Assert.Null(fila.CostoMedio);
    }

    [Fact]
    public async Task ReviewFocus4_ExistenciaYValorPorAlmacen_CoincidenConConsultaInventario_YConLa1301TrasElBatch()
    {
        var p1 = await ProductoClasificadoAsync();
        var p2 = await ProductoClasificadoAsync();
        var almacen = await _prueba.SembrarAlmacenAsync();
        await _prueba.RegistrarOkAsync(LibroInventarioPrueba.Entrada(p1, almacen, 10m, 10m, D1));
        await _prueba.RegistrarOkAsync(LibroInventarioPrueba.Salida(p1, almacen, 3m, D2, tipo: TipoMovimientoInventario.Venta));
        await _prueba.RegistrarOkAsync(LibroInventarioPrueba.Salida(p1, almacen, 1m, D3));
        await _prueba.RegistrarOkAsync(LibroInventarioPrueba.Entrada(p2, almacen, 3m, 7.5m, D1));
        await _prueba.RegistrarOkAsync(LibroInventarioPrueba.Entrada(p2, almacen, 1m, 1m, D2, unidadId: _prueba.UnidadCja)); // 12 × 1
        await _prueba.RegistrarOkAsync(LibroInventarioPrueba.Salida(p2, almacen, 4m, D3, tipo: TipoMovimientoInventario.Venta));
        // Entrada retroactiva de p1 y ajuste de costo: el valor cambia sin que cambie la cantidad.
        await _prueba.RegistrarOkAsync(LibroInventarioPrueba.Entrada(p1, almacen, 2m, 16m, D1));
        await _prueba.AjustarOkAsync(p1);

        Assert.Empty((await PostearCostoAsync(p1)).Pendientes);
        Assert.Empty((await PostearCostoAsync(p2)).Pendientes);

        var fecha = new DateOnly(2026, 12, 31);
        var existencias = await GetAsync<ExistenciasVistaResponse>($"{UrlExistencias}?almacenId={almacen}&fecha={fecha:yyyy-MM-dd}");
        Assert.Equal(fecha, existencias.Fecha);
        Assert.Equal(2, existencias.Pagina.Total);
        foreach (var fila in existencias.Pagina.Items)
        {
            Assert.Equal(almacen, fila.AlmacenId);
            Assert.Equal(await _prueba.ConsultarAsync(c => c.ExistenciaAsync(fila.ProductoId, almacen, fecha)), fila.Existencia);
            var porAlmacen = await _prueba.ConsultarAsync(c => c.ExistenciasPorAlmacenAsync(fila.ProductoId));
            Assert.Equal(Assert.Single(porAlmacen).Existencia, fila.Existencia);
            Assert.Equal(await ValorLibroAsync(fila.ProductoId, almacen, fecha), fila.Valor);
            Assert.Equal(Math.Round(fila.Valor / fila.Existencia, 6), fila.CostoMedio);
        }

        Assert.Equal([8m, 11m], existencias.Pagina.Items.OrderBy(x => x.ProductoId == p1 ? 0 : 1).Select(x => x.Existencia));
        // El valor total del almacén es el saldo de la 1301 de sus productos (el batch lo llevó todo, ajuste incluido).
        Assert.Equal(existencias.Pagina.Items.Sum(x => x.Valor), existencias.ValorTotal);
        Assert.Equal(await SaldoInventarioAsync(p1, p2), existencias.ValorTotal);
        Assert.NotEqual(0m, existencias.ValorTotal);
    }

    [Fact]
    public async Task Existencias_FechaDeCorte_Transferencia_Texto_SoloConExistencia_YPaginacion()
    {
        var producto = await _prueba.SembrarProductoAsync();
        var otro = await _prueba.SembrarProductoAsync();
        var a = await _prueba.SembrarAlmacenAsync();
        var b = await _prueba.SembrarAlmacenAsync();
        await _prueba.RegistrarOkAsync(LibroInventarioPrueba.Entrada(producto, a, 10m, 4m, D1));
        await _prueba.RegistrarOkAsync(LibroInventarioPrueba.Salida(producto, a, 6m, D2, tipo: TipoMovimientoInventario.Transferencia));
        await _prueba.RegistrarOkAsync(LibroInventarioPrueba.Entrada(producto, b, 6m, null, D2, tipo: TipoMovimientoInventario.Transferencia));
        await _prueba.RegistrarOkAsync(LibroInventarioPrueba.Salida(producto, b, 6m, D3));
        await _prueba.RegistrarOkAsync(LibroInventarioPrueba.Entrada(otro, a, 1m, 2m, D1));

        async Task<ExistenciasVistaResponse> Consultar(string extra) =>
            await GetAsync<ExistenciasVistaResponse>($"{UrlExistencias}?productoId={producto}{extra}");

        // A D1 solo existe lo de A; a D2 la transferencia reparte 4 / 6; a D3 B queda en 0 (valor 0, sin costo medio).
        var d1 = await Consultar($"&fecha={D1:yyyy-MM-dd}");
        Assert.Equal([(a, 10m, 40m)], d1.Pagina.Items.Select(x => (x.AlmacenId, x.Existencia, x.Valor)));
        var d2 = await Consultar($"&fecha={D2:yyyy-MM-dd}");
        Assert.Equal(2, d2.Pagina.Total);
        Assert.Equal(40m, d2.ValorTotal);
        foreach (var fila in d2.Pagina.Items)
        {
            Assert.Equal(await _prueba.ConsultarAsync(c => c.ExistenciaAsync(producto, fila.AlmacenId, D2)), fila.Existencia);
            Assert.Equal(4m, fila.CostoMedio);
        }

        var d3 = await Consultar($"&fecha={D3:yyyy-MM-dd}");
        var enB = Assert.Single(d3.Pagina.Items, x => x.AlmacenId == b);
        Assert.Equal((0m, 0m, (decimal?)null), (enB.Existencia, enB.Valor, enB.CostoMedio));
        Assert.Equal([a], (await Consultar($"&fecha={D3:yyyy-MM-dd}&soloConExistencia=true")).Pagina.Items.Select(x => x.AlmacenId));

        // Sin fecha: hoy. Un movimiento con fecha futura no cuenta.
        var futuro = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(30);
        await _prueba.RegistrarOkAsync(LibroInventarioPrueba.Entrada(producto, a, 100m, 1m, futuro));
        var hoy = await Consultar(string.Empty);
        Assert.Equal(DateOnly.FromDateTime(DateTime.UtcNow), hoy.Fecha);
        Assert.Equal(4m, Assert.Single(hoy.Pagina.Items, x => x.AlmacenId == a).Existencia);
        Assert.Equal(104m, Assert.Single((await Consultar($"&fecha={futuro:yyyy-MM-dd}")).Pagina.Items, x => x.AlmacenId == a).Existencia);

        // Texto sobre código y nombre del producto (el código es único por test); almacén; paginación con total y valor total.
        var (codigo, _) = await CodigosAsync(producto, a);
        var porTexto = await GetAsync<ExistenciasVistaResponse>($"{UrlExistencias}?texto={Uri.EscapeDataString(codigo.ToLowerInvariant())}&fecha={D3:yyyy-MM-dd}");
        Assert.Equal(2, porTexto.Pagina.Total);
        Assert.All(porTexto.Pagina.Items, x => Assert.Equal(producto, x.ProductoId));
        var enA = await GetAsync<ExistenciasVistaResponse>($"{UrlExistencias}?almacenId={a}&fecha={D3:yyyy-MM-dd}&tamanoPagina=1&pagina=2");
        Assert.Equal((2L, 2), (enA.Pagina.Total, enA.Pagina.TotalPaginas));
        Assert.Single(enA.Pagina.Items);
        Assert.Equal(16m + 2m, enA.ValorTotal);
        var porExistenciaDesc = await GetAsync<ExistenciasVistaResponse>(
            $"{UrlExistencias}?almacenId={a}&fecha={D3:yyyy-MM-dd}&ordenarPor=Existencia&descendente=true");
        Assert.Equal([4m, 1m], porExistenciaDesc.Pagina.Items.Select(x => x.Existencia));
    }

    // ── Review Focus 5: filtros inválidos ───────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(UrlProducto + "?desde=2026-05-02&hasta=2026-05-01", "Desde")]
    [InlineData(UrlValor + "?desde=2026-05-02&hasta=2026-05-01", "Desde")]
    [InlineData(UrlProducto + "?tipoMovimiento=9", "TipoMovimiento")]
    [InlineData(UrlValor + "?tipoMovimiento=0", "TipoMovimiento")]
    [InlineData(UrlProducto + "?tipoOrigen=7", "TipoOrigen")]
    [InlineData(UrlValor + "?tipoOrigen=70000", "TipoOrigen")]
    public async Task FiltroInvalido_Devuelve400ConCampo(string url, string campo)
    {
        using var respuesta = await _client.GetAsync(url);
        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        using var json = JsonDocument.Parse(await respuesta.Content.ReadAsStringAsync());
        Assert.True(json.RootElement.GetProperty("errors").TryGetProperty(campo, out _), json.RootElement.ToString());
    }

    /// <summary>Valores que ni siquiera se pueden enlazar: 400 del model binding (mensajes en <c>errors</c>, sin campo).</summary>
    [Theory]
    [InlineData(UrlProducto + "?tipoMovimiento=abc", "abc")]
    [InlineData(UrlExistencias + "?fecha=2026-13-45", "2026-13-45")]
    [InlineData(UrlProducto + "?productoId=no-es-guid", "no-es-guid")]
    [InlineData(UrlValor + "?soloAjustes=quizas", "quizas")]
    public async Task ValorNoEnlazable_Devuelve400(string url, string valor)
    {
        using var respuesta = await _client.GetAsync(url);
        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
        Assert.Contains(valor, await respuesta.Content.ReadAsStringAsync(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(UrlProducto)]
    [InlineData(UrlValor)]
    [InlineData(UrlExistencias)]
    public async Task PaginaCeroTamanoExcesivoYOrdenNoPermitido_SeNormalizan(string url)
    {
        using var respuesta = await _client.GetAsync($"{url}?pagina=0&tamanoPagina=500&ordenarPor=Nombre%3B%20DROP%20TABLE%20x");
        Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
        using var json = JsonDocument.Parse(await respuesta.Content.ReadAsStringAsync());
        var pagina = url == UrlExistencias ? json.RootElement.GetProperty("pagina") : json.RootElement;
        Assert.Equal(1, pagina.GetProperty("pagina").GetInt32());
        Assert.Equal(PageRequest.TamanoMaximo, pagina.GetProperty("tamanoPagina").GetInt32());

        using var negativo = await _client.GetAsync($"{url}?pagina=-5&tamanoPagina=0");
        Assert.Equal(HttpStatusCode.OK, negativo.StatusCode);
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────────────────────

    private async Task<T> GetAsync<T>(string url)
    {
        using var respuesta = await _client.GetAsync(url);
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, $"{url} → {(int)respuesta.StatusCode}: {await respuesta.Content.ReadAsStringAsync()}");
        return (await respuesta.Content.ReadFromJsonAsync<T>())!;
    }

    private static List<decimal> SaldosEsperados(IReadOnlyList<(long Id, DateOnly FechaRegistro, decimal Cantidad)> libro, decimal saldoInicial)
    {
        var saldos = new List<decimal>();
        var saldo = saldoInicial;
        foreach (var fila in libro)
        {
            saldo += fila.Cantidad;
            saldos.Add(saldo);
        }

        return saldos;
    }

    /// <summary>El libro del producto (y almacén) leído directamente de la tabla, en orden (FechaRegistro, Id).</summary>
    private async Task<List<(long Id, DateOnly FechaRegistro, decimal Cantidad)>> LibroAsync(Guid productoId, Guid? almacenId)
    {
        await using var conexion = _prueba.NuevaConexion();
        var filas = await conexion.QueryAsync<(long, DateTime, decimal)>(
            """
            SELECT "Id", "FechaRegistro"::timestamp, "Cantidad" FROM "MovimientosProducto"
            WHERE "ProductoId" = @productoId AND (@almacenId::uuid IS NULL OR "AlmacenId" = @almacenId)
            ORDER BY "FechaRegistro", "Id"
            """,
            new { productoId, almacenId });
        return [.. filas.Select(f => (f.Item1, DateOnly.FromDateTime(f.Item2), f.Item3))];
    }

    private async Task<(string Producto, string Almacen)> CodigosAsync(Guid productoId, Guid almacenId)
    {
        await using var conexion = _prueba.NuevaConexion();
        return await conexion.QuerySingleAsync<(string, string)>(
            """SELECT (SELECT "Codigo" FROM "Productos" WHERE "Id" = @productoId), (SELECT "Codigo" FROM "Almacenes" WHERE "Id" = @almacenId)""",
            new { productoId, almacenId });
    }

    private async Task<decimal> ValorLibroAsync(Guid productoId, Guid almacenId, DateOnly fecha)
    {
        await using var conexion = _prueba.NuevaConexion();
        return await conexion.ExecuteScalarAsync<decimal>(
            """
            SELECT COALESCE(SUM("ImporteCosto"), 0) FROM "MovimientosValor"
            WHERE "ProductoId" = @productoId AND "AlmacenId" = @almacenId AND "FechaRegistro" <= @fecha
            """,
            new { productoId, almacenId, fecha });
    }

    private async Task<decimal> SaldoInventarioAsync(params Guid[] productos)
    {
        await using var conexion = _prueba.NuevaConexion();
        return await conexion.ExecuteScalarAsync<decimal>(
            """
            SELECT COALESCE(SUM("Importe"), 0) FROM "MovimientosContables"
            WHERE "CuentaContableId" = @cuenta AND "ProductoId" = ANY(@productos)
            """,
            new { cuenta = CuentaContableIds.Inventario, productos });
    }

    /// <summary>Producto con los grupos GENERAL/BIENES (los del setup comodín: el batch lo contabiliza en la 1301).</summary>
    private async Task<Guid> ProductoClasificadoAsync()
    {
        var producto = await _prueba.SembrarProductoAsync();
        await using var conexion = _prueba.NuevaConexion();
        await conexion.ExecuteAsync(
            """UPDATE "Productos" SET "GrupoInventarioId" = @Gi, "GrupoProductoId" = @Gp WHERE "Id" = @Id""",
            new { Gi = GrupoContableIds.InventarioGeneral, Gp = GrupoContableIds.ProductoBienes, Id = producto });
        return producto;
    }

    private async Task<ResultadoPosteoCostoInventario> PostearCostoAsync(Guid? productoId)
    {
        await using var scope = _prueba.Provider.CreateAsyncScope();
        return await scope.ServiceProvider.GetRequiredService<IPosteoCostoInventario>().PostearAsync(productoId);
    }
}
