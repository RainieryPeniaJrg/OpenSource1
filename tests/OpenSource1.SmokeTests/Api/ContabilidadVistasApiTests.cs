using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OpenSource1.Api;
using OpenSource1.Application.Data;
using OpenSource1.Application.Features.Contabilidad.Dtos;
using OpenSource1.Application.Features.FacturasVenta.Borradores.Dtos;
using OpenSource1.Application.Services.Contabilidad;
using OpenSource1.Core.Common;
using OpenSource1.Core.Entities.Contabilidad;
using OpenSource1.Core.Enums;
using OpenSource1.SmokeTests.TestInfrastructure;

namespace OpenSource1.SmokeTests.Api;

/// <summary>
/// Vistas contables (Task 7.4): <c>GET api/contabilidad/movimientos</c> con los filtros nuevos (<c>tipoDocumento</c>,
/// <c>numeroDocumento</c>, <c>socioId</c>, <c>registroId</c>) y <c>GET api/contabilidad/balance-comprobacion</c> (saldo inicial,
/// débitos, créditos y saldo final por cuenta de Posteo, más las cuentas de encabezado como títulos), ambas CanConsult. Los
/// asientos son REALES: facturas posteadas con el motor por la API, cobros por <c>api/cobros</c> y el batch de costo de
/// inventario. Review Focus 3 (el balance cuadra en cualquier rango, saldo inicial + débitos − créditos = saldo final, sin
/// cierre de ejercicio) y 5 (filtros inválidos → 400 o valor por defecto, nunca 500).
/// Fechas: el escenario del Review Focus 3 vive en 2024 y el resto de los tests de la clase en 2025 o después, para que los
/// importes exactos de los rangos de 2024 no dependan del orden de ejecución. REQUIERE DOCKER.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ContabilidadVistasApiTests(PostgresTestFixture fixture) : IClassFixture<PostgresTestFixture>, IAsyncLifetime
{
    private const string UrlMovimientos = "/api/contabilidad/movimientos";
    private const string UrlBalance = "/api/contabilidad/balance-comprobacion";

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
        foreach (var url in new[] { UrlBalance, $"{UrlBalance}?desde=2025-01-01&hasta=2025-12-31", $"{UrlMovimientos}?tipoDocumento=2&socioId={Guid.NewGuid()}" })
        {
            var anon = new HttpRequestMessage(HttpMethod.Get, url);
            anon.Headers.Add("X-Test-Anonymous", "true");
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(anon)).StatusCode);

            var ejecutor = new HttpRequestMessage(HttpMethod.Get, url);
            ejecutor.Headers.Add("X-Test-User", "ejecutor");
            ejecutor.Headers.Add("X-Test-Roles", "Ejecutor");
            Assert.Equal(HttpStatusCode.OK, (await client.SendAsync(ejecutor)).StatusCode);
        }
    }

    // ── Review Focus 3: balance de comprobación ────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ReviewFocus3_Balance_CuadraEnCualquierRango_ConAsientosRealesDeFacturasCobrosYBatchDeCosto()
    {
        var e = await SembrarEscenario2024Async();

        // Enero 2024: nada antes (saldo inicial 0 en todas); factura F1 (producto + cuenta propia), entrada y costo de la venta.
        var enero = await BalanceAsync("desde=2024-01-01&hasta=2024-01-31");
        Assert.Equal(
            [
                ("1201", 0m, 141.6m, 0m, 141.6m),     // CxC: F1 = 2 × 50 + 20 + ITBIS 21.6
                ("1301", 0m, 50m, 10m, 40m),          // Inventario: entrada 10 × 5 (batch), costo de la venta 2 × 5
                ("2101", 0m, 0m, 21.6m, -21.6m),      // ITBIS por pagar
                ("4101", 0m, 0m, 100m, -100m),        // Ventas (línea de producto)
                (e.NumeroIngresos, 0m, 0m, 20m, -20m),// Ingresos propios (línea de cuenta)
                ("5101", 0m, 10m, 0m, 10m),           // Costo de ventas
                ("5201", 0m, 0m, 50m, -50m),          // Ajustes de inventario (contrapartida de la entrada)
            ],
            Posteo(enero).Select(f => (f.Numero, f.SaldoInicial!.Value, f.Debitos!.Value, f.Creditos!.Value, f.SaldoFinal!.Value)));
        Assert.Equal((0m, 201.6m, 201.6m, 0m), (enero.Totales.SaldoInicial, enero.Totales.Debitos, enero.Totales.Creditos, enero.Totales.SaldoFinal));

        // Febrero-diciembre: el saldo inicial es el final de enero; F2 y el cobro de F1 en el rango. Las cuentas de resultado
        // (4101, 5101, 5201, la propia) arrastran su saldo: no hay cierre de ejercicio.
        var resto = await BalanceAsync("desde=2024-02-01&hasta=2024-12-31");
        Assert.Equal(
            [
                ("1101", 0m, 141.6m, 0m, 141.6m),       // Caja: cobro de F1
                ("1201", 141.6m, 118m, 141.6m, 118m),   // CxC: + F2 − cobro
                ("1301", 40m, 0m, 0m, 40m),
                ("2101", -21.6m, 0m, 18m, -39.6m),
                ("4101", -100m, 0m, 0m, -100m),
                (e.NumeroIngresos, -20m, 0m, 100m, -120m),
                ("5101", 10m, 0m, 0m, 10m),
                ("5201", -50m, 0m, 0m, -50m),
            ],
            Posteo(resto).Select(f => (f.Numero, f.SaldoInicial!.Value, f.Debitos!.Value, f.Creditos!.Value, f.SaldoFinal!.Value)));
        Assert.Equal((0m, 259.6m, 259.6m, 0m), (resto.Totales.SaldoInicial, resto.Totales.Debitos, resto.Totales.Creditos, resto.Totales.SaldoFinal));

        // Marzo: sin movimientos, pero las cuentas con saldo aparecen (saldo inicial = final, sin débitos ni créditos).
        var marzo = await BalanceAsync("desde=2024-03-01&hasta=2024-03-31");
        Assert.Equal(8, Posteo(marzo).Count);
        Assert.All(Posteo(marzo), f => Assert.Equal((f.SaldoInicial!.Value, 0m, 0m, 0), (f.SaldoFinal!.Value, f.Debitos!.Value, f.Creditos!.Value, f.Movimientos!.Value)));
        Assert.Equal(118m, Posteo(marzo).Single(f => f.Numero == "1201").SaldoFinal);

        // Antes de todo: ninguna cuenta de Posteo; los encabezados siguen como títulos.
        var antes = await BalanceAsync("desde=2023-01-01&hasta=2023-12-31");
        Assert.Empty(Posteo(antes));
        Assert.Equal((0m, 0m, 0m, 0m), (antes.Totales.SaldoInicial, antes.Totales.Debitos, antes.Totales.Creditos, antes.Totales.SaldoFinal));

        // Movimientos por rango: cuenta y rango del balance → la suma de débitos y créditos coincide con su fila.
        var cxcResto = await GetAsync<PagedResult<MovimientoContableResponse>>(
            $"{UrlMovimientos}?cuentaId={CuentaContableIds.CxC}&desde=2024-02-01&hasta=2024-12-31");
        Assert.Equal((118m, 141.6m), (cxcResto.Items.Sum(m => m.Debito), cxcResto.Items.Sum(m => m.Credito)));

        // Cualquier rango (incluidos los abiertos por un lado y sin fechas): cuadra y coincide con el libro por SQL.
        foreach (var query in new[]
                 {
                     "desde=2024-01-01&hasta=2024-01-31", "desde=2024-02-01&hasta=2024-12-31", "desde=2024-01-10&hasta=2024-02-15",
                     "desde=2024-01-11&hasta=2024-01-11", "desde=2024-02-20", "hasta=2024-01-10", "", "desde=2023-01-01&hasta=2023-12-31",
                 })
        {
            var balance = await BalanceAsync(query);
            await AssertCuadraYCoincideConElLibroAsync(balance, query);
        }

        // Encabezados: filas de título (sin importes, con su sangría) intercaladas por número de cuenta.
        var titulos = enero.Filas.Where(f => f.EsEncabezado).ToList();
        Assert.Equal(["1", "2", "4", "5"], titulos.Select(f => f.Numero));
        Assert.All(titulos, f => Assert.Equal((0, (decimal?)null, (decimal?)null, (decimal?)null, (decimal?)null),
            (f.Sangria, f.SaldoInicial, f.Debitos, f.Creditos, f.SaldoFinal)));
        Assert.Equal(["1", "1201", "1301", "2", "2101", "4", "4101", e.NumeroIngresos, "5", "5101", "5201"], enero.Filas.Select(f => f.Numero));
        var ventas = enero.Filas.Single(f => f.Numero == "4101");
        Assert.Equal(("Ventas", TipoCuentaContable.Posteo, TipoResultadoCuenta.Resultado, 1, false), (ventas.Nombre, ventas.TipoCuenta, ventas.TipoResultado, ventas.Sangria, ventas.EsEncabezado));
    }

    [Fact]
    public async Task Balance_CuentaSaldadaSinMovimientosEnElRango_NoAparece_YConMovimientosEnElRangoSi()
    {
        var a = await CrearCuentaAsync(TipoResultadoCuenta.Balance);
        var b = await CrearCuentaAsync(TipoResultadoCuenta.Resultado);
        var c = await CrearCuentaAsync(TipoResultadoCuenta.Balance);
        await RegistrarAsync(new DateOnly(2025, 5, 1), (a.Id, 30m), (b.Id, -30m));
        await RegistrarAsync(new DateOnly(2025, 5, 2), (a.Id, -30m), (c.Id, 30m));

        // Tras mayo, "a" queda en cero y sin movimientos en junio: no aparece; "b" y "c" tienen saldo: sí.
        var junio = await BalanceAsync("desde=2025-06-01&hasta=2025-06-30");
        Assert.DoesNotContain(junio.Filas, f => f.CuentaContableId == a.Id);
        Assert.Equal((-30m, -30m), (Fila(junio, b.Id).SaldoInicial!.Value, Fila(junio, b.Id).SaldoFinal!.Value));
        Assert.Equal((30m, 30m), (Fila(junio, c.Id).SaldoInicial!.Value, Fila(junio, c.Id).SaldoFinal!.Value));

        // En mayo "a" tiene movimientos (30 al débito y 30 al crédito, saldo final 0): sí aparece.
        var mayo = await BalanceAsync("desde=2025-05-01&hasta=2025-05-31");
        var filaA = Fila(mayo, a.Id);
        Assert.Equal((0m, 30m, 30m, 0m, 2), (filaA.SaldoInicial!.Value, filaA.Debitos!.Value, filaA.Creditos!.Value, filaA.SaldoFinal!.Value, filaA.Movimientos!.Value));
        Assert.Equal(a.Numero, filaA.Numero);
        await AssertCuadraYCoincideConElLibroAsync(mayo, "desde=2025-05-01&hasta=2025-05-31");
        await AssertCuadraYCoincideConElLibroAsync(junio, "desde=2025-06-01&hasta=2025-06-30");
    }

    // ── Movimientos contables: filtros nuevos ───────────────────────────────────────────────────────────────────

    [Fact]
    public async Task Movimientos_FiltrosTipoDocumentoNumeroSocioYRegistro_YContratoAnteriorIntacto()
    {
        var ingresos = await CrearCuentaAsync(TipoResultadoCuenta.Resultado);
        var socio = await CrearSocioAsync("Libro contable Alfa");
        var otro = await CrearSocioAsync("Libro contable Beta");
        var fecha = new DateOnly(2025, 3, 10);
        var factura = await PostearFacturaAsync(socio, fecha, (ingresos.Id, 100m));
        var facturaOtro = await PostearFacturaAsync(otro, fecha, (ingresos.Id, 10m));
        var cobro = await RegistrarPagoAsync(socio, 50m, new DateOnly(2025, 3, 12));

        // Socio: las líneas de su factura (CxC, ingresos, ITBIS) y las de su cobro (caja, CxC); con código y nombre del socio.
        var delSocio = await GetAsync<PagedResult<MovimientoContableResponse>>($"{UrlMovimientos}?socioId={socio}");
        Assert.Equal(5, delSocio.Total);
        Assert.All(delSocio.Items, m => Assert.Equal((socio, "Libro contable Alfa"), (m.SocioNegocioId!.Value, m.SocioNombre)));
        Assert.All(delSocio.Items, m => Assert.False(string.IsNullOrEmpty(m.SocioCodigo)));
        Assert.Equal(
            [(TipoDocumentoContable.FacturaVenta, 118m), (TipoDocumentoContable.FacturaVenta, -100m), (TipoDocumentoContable.FacturaVenta, -18m),
             (TipoDocumentoContable.Cobro, 50m), (TipoDocumentoContable.Cobro, -50m)],
            delSocio.Items.Select(m => (m.TipoDocumento, m.Importe)));

        // Tipo de documento: solo el cobro.
        var cobros = await GetAsync<PagedResult<MovimientoContableResponse>>($"{UrlMovimientos}?socioId={socio}&tipoDocumento=3");
        Assert.Equal([(CuentaContableIds.Caja, 50m), (CuentaContableIds.CxC, -50m)], cobros.Items.Select(m => (m.CuentaContableId, m.Importe)));
        Assert.All(cobros.Items, m => Assert.Equal(cobro.RegistroContableId, m.RegistroContableId));

        // Número de documento (por contenido, sin distinguir mayúsculas): solo las tres líneas de la factura del socio.
        var porNumero = await GetAsync<PagedResult<MovimientoContableResponse>>(
            $"{UrlMovimientos}?tipoDocumento=2&numeroDocumento={factura.Numero}");
        Assert.Equal(3, porNumero.Total);
        Assert.All(porNumero.Items, m => Assert.Equal((factura.Numero, factura.RegistroContableId), (m.NumeroDocumento, m.RegistroContableId)));

        // Registro: el asiento completo (todas sus líneas, que suman 0) aunque se filtre también por fecha que lo incluye.
        var registro = await GetAsync<PagedResult<MovimientoContableResponse>>(
            $"{UrlMovimientos}?registroId={facturaOtro.RegistroContableId}&desde=2025-03-10&hasta=2025-03-10");
        Assert.Equal(3, registro.Total);
        Assert.Equal(0m, registro.Items.Sum(m => m.Importe));
        Assert.All(registro.Items, m => Assert.Equal(otro, m.SocioNegocioId));

        // Cuenta + socio: las líneas del socio en la CxC (factura +118, cobro −50).
        var cxc = await GetAsync<PagedResult<MovimientoContableResponse>>($"{UrlMovimientos}?cuentaId={CuentaContableIds.CxC}&socioId={socio}");
        Assert.Equal([118m, -50m], cxc.Items.Select(m => m.Importe));

        // Filtros sin coincidencias (socio, registro o documento inexistentes) → 200 vacío, no 404: no son entidades de ruta.
        Assert.Equal(0, (await GetAsync<PagedResult<MovimientoContableResponse>>($"{UrlMovimientos}?socioId={Guid.NewGuid()}")).Total);
        Assert.Equal(0, (await GetAsync<PagedResult<MovimientoContableResponse>>($"{UrlMovimientos}?registroId=987654321")).Total);
        Assert.Equal(0, (await GetAsync<PagedResult<MovimientoContableResponse>>($"{UrlMovimientos}?numeroDocumento=NOEXISTE%25")).Total);

        // Contrato anterior: cuenta + rango, orden cronológico y paginación siguen igual.
        var ingresosMarzo = await GetAsync<PagedResult<MovimientoContableResponse>>(
            $"{UrlMovimientos}?cuentaId={ingresos.Id}&desde=2025-03-01&hasta=2025-03-31&tamanoPagina=1&pagina=2");
        Assert.Equal(2, ingresosMarzo.Total);
        Assert.Equal(-10m, Assert.Single(ingresosMarzo.Items).Importe);
    }

    // ── Review Focus 5: filtros inválidos ────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData(UrlMovimientos + "?desde=2025-05-01&hasta=2025-04-01", "Desde")]
    [InlineData(UrlMovimientos + "?tipoDocumento=9", "TipoDocumento")]
    [InlineData(UrlMovimientos + "?tipoDocumento=-1", "TipoDocumento")]
    [InlineData(UrlMovimientos + "?tipoDocumento=70000", "TipoDocumento")]
    [InlineData(UrlBalance + "?desde=2025-05-01&hasta=2025-04-01", "Desde")]
    public async Task FiltroInvalido_Devuelve400ConCampo(string url, string campo)
    {
        var respuesta = await _client.GetAsync(url);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.BadRequest, $"{url} → {(int)respuesta.StatusCode}: {cuerpo}");
        Assert.True(JsonDocument.Parse(cuerpo).RootElement.GetProperty("errors").TryGetProperty(campo, out _), cuerpo);
    }

    [Theory]
    [InlineData(UrlMovimientos + "?tipoDocumento=abc")]
    [InlineData(UrlMovimientos + "?desde=2025-02-31")]
    [InlineData(UrlMovimientos + "?socioId=no-guid")]
    [InlineData(UrlMovimientos + "?registroId=abc")]
    [InlineData(UrlBalance + "?hasta=abc")]
    public async Task ValorNoEnlazable_Devuelve400(string url)
    {
        var respuesta = await _client.GetAsync(url);
        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
    }

    [Fact]
    public async Task PaginaCeroTamanoExcesivoYOrdenNoPermitido_SeNormalizan()
    {
        var a = await CrearCuentaAsync(TipoResultadoCuenta.Balance);
        var b = await CrearCuentaAsync(TipoResultadoCuenta.Balance);
        await RegistrarAsync(new DateOnly(2025, 8, 2), (a.Id, 5m), (b.Id, -5m));
        await RegistrarAsync(new DateOnly(2025, 8, 1), (a.Id, 7m), (b.Id, -7m));

        var cero = await GetAsync<PagedResult<MovimientoContableResponse>>($"{UrlMovimientos}?cuentaId={a.Id}&pagina=0&tamanoPagina=1000");
        Assert.Equal((1, PageRequest.TamanoMaximo), (cero.Pagina, cero.TamanoPagina));
        var negativo = await GetAsync<PagedResult<MovimientoContableResponse>>($"{UrlMovimientos}?cuentaId={a.Id}&pagina=-5&tamanoPagina=0");
        Assert.Equal((1, PageRequest.TamanoPorDefecto), (negativo.Pagina, negativo.TamanoPagina));

        var inyeccion = await GetAsync<PagedResult<MovimientoContableResponse>>(
            $"{UrlMovimientos}?cuentaId={a.Id}&ordenarPor={Uri.EscapeDataString("\"Importe\"; DROP TABLE x --")}");
        Assert.Equal([7m, 5m], inyeccion.Items.Select(m => m.Importe));
        var porDocumento = await GetAsync<PagedResult<MovimientoContableResponse>>(
            $"{UrlMovimientos}?cuentaId={a.Id}&ordenarPor=NumeroDocumento&descendente=true");
        Assert.Equal(2, porDocumento.Total);
    }

    // ── Escenario del Review Focus 3 ────────────────────────────────────────────────────────────────────────────

    private sealed record Escenario(string NumeroIngresos);

    /// <summary>
    /// 2024, con los procesos reales: entrada de 10 u a 5 (05/01, diario de inventario); F1 del 10/01 = 2 u del producto a 50
    /// + 20 a una cuenta de ingresos propia (ITBIS 18 %: 141.6); F2 del 15/02 = 100 a la cuenta propia (118); cobro de 141.6 el
    /// 20/02 a la caja por defecto; batch de costo del producto (asienta la entrada y el costo de la venta en sus fechas).
    /// </summary>
    private async Task<Escenario> SembrarEscenario2024Async()
    {
        var ingresos = await CrearCuentaAsync(TipoResultadoCuenta.Resultado, numero: "4150");
        var producto = await ProductoClasificadoAsync();
        var almacen = await _prueba.SembrarAlmacenAsync();
        var socio = await CrearSocioAsync("Balance 2024");
        await _prueba.RegistrarOkAsync(LibroInventarioPrueba.Entrada(producto, almacen, 10m, 5m, new DateOnly(2024, 1, 5)));

        await PostearFacturaAsync(socio, new DateOnly(2024, 1, 10), (ingresos.Id, 20m), (producto, 2m, 50m, almacen));
        await PostearFacturaAsync(socio, new DateOnly(2024, 2, 15), (ingresos.Id, 100m));
        await RegistrarPagoAsync(socio, 141.6m, new DateOnly(2024, 2, 20));

        await using (var scope = _prueba.Provider.CreateAsyncScope())
        {
            var batch = await scope.ServiceProvider.GetRequiredService<IPosteoCostoInventario>().PostearAsync(producto);
            Assert.Empty(batch.Pendientes);
            Assert.Equal(2, batch.Asientos);
        }

        return new Escenario(ingresos.Numero);
    }

    // ── Comprobaciones ────────────────────────────────────────────────────────────────────────────────────────

    private static List<BalanceComprobacionFilaResponse> Posteo(BalanceComprobacionResponse balance) =>
        [.. balance.Filas.Where(f => !f.EsEncabezado)];

    private static BalanceComprobacionFilaResponse Fila(BalanceComprobacionResponse balance, Guid cuenta) =>
        balance.Filas.Single(f => f.CuentaContableId == cuenta);

    /// <summary>
    /// Review Focus 3: Σ saldos iniciales = Σ saldos finales = 0, Σ débitos = Σ créditos, saldo inicial + débitos − créditos =
    /// saldo final por cuenta, totales = suma de filas, y cada fila igual al libro leído por SQL (independiente de la API).
    /// </summary>
    private async Task AssertCuadraYCoincideConElLibroAsync(BalanceComprobacionResponse balance, string query)
    {
        var filas = Posteo(balance);
        Assert.True(filas.Sum(f => f.SaldoInicial!.Value) == 0m, $"Σ saldo inicial ≠ 0 en '{query}'");
        Assert.True(filas.Sum(f => f.SaldoFinal!.Value) == 0m, $"Σ saldo final ≠ 0 en '{query}'");
        Assert.Equal(filas.Sum(f => f.Debitos!.Value), filas.Sum(f => f.Creditos!.Value));
        Assert.All(filas, f => Assert.Equal(f.SaldoFinal, f.SaldoInicial + f.Debitos - f.Creditos));
        Assert.Equal(
            (filas.Sum(f => f.SaldoInicial!.Value), filas.Sum(f => f.Debitos!.Value), filas.Sum(f => f.Creditos!.Value), filas.Sum(f => f.SaldoFinal!.Value)),
            (balance.Totales.SaldoInicial, balance.Totales.Debitos, balance.Totales.Creditos, balance.Totales.SaldoFinal));

        var parametros = System.Web.HttpUtility.ParseQueryString(query);
        DateOnly? desde = parametros["desde"] is { } d ? DateOnly.Parse(d) : null;
        DateOnly? hasta = parametros["hasta"] is { } h ? DateOnly.Parse(h) : null;
        Assert.Equal((desde, hasta), (balance.Desde, balance.Hasta));

        await using var conexion = _prueba.NuevaConexion();
        var libro = (await conexion.QueryAsync<(Guid Cuenta, decimal Inicial, decimal Debitos, decimal Creditos, decimal Final)>(
            """
            SELECT "CuentaContableId",
                   COALESCE(SUM("Importe") FILTER (WHERE @desde::date IS NOT NULL AND "FechaRegistro" < @desde::date), 0),
                   COALESCE(SUM("Debito") FILTER (WHERE @desde::date IS NULL OR "FechaRegistro" >= @desde::date), 0),
                   COALESCE(SUM("Credito") FILTER (WHERE @desde::date IS NULL OR "FechaRegistro" >= @desde::date), 0),
                   COALESCE(SUM("Importe"), 0)
            FROM "MovimientosContables"
            WHERE @hasta::date IS NULL OR "FechaRegistro" <= @hasta::date
            GROUP BY "CuentaContableId"
            """,
            new { desde = desde?.ToDateTime(TimeOnly.MinValue), hasta = hasta?.ToDateTime(TimeOnly.MinValue) }))
            .Where(x => x.Inicial != 0m || x.Debitos != 0m || x.Creditos != 0m)
            .OrderBy(x => x.Cuenta)
            .ToList();
        Assert.Equal(
            libro,
            filas.Select(f => (f.CuentaContableId, f.SaldoInicial!.Value, f.Debitos!.Value, f.Creditos!.Value, f.SaldoFinal!.Value)).OrderBy(x => x.CuentaContableId));
    }

    // ── Helpers ─────────────────────────────────────────────────────────────────────────────────────────────────

    private async Task<T> GetAsync<T>(string url)
    {
        using var respuesta = await _client.GetAsync(url);
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, $"{url} → {(int)respuesta.StatusCode}: {await respuesta.Content.ReadAsStringAsync()}");
        return (await respuesta.Content.ReadFromJsonAsync<T>())!;
    }

    private Task<BalanceComprobacionResponse> BalanceAsync(string query) =>
        GetAsync<BalanceComprobacionResponse>(string.IsNullOrEmpty(query) ? UrlBalance : $"{UrlBalance}?{query}");

    private sealed record CuentaCreada(Guid Id, string Numero);

    private async Task<CuentaCreada> CrearCuentaAsync(TipoResultadoCuenta tipoResultado, string? numero = null)
    {
        numero ??= $"9{Random.Shared.Next(100_000, 999_999)}";
        var response = await _client.PostAsJsonAsync("/api/cuentas-contables", new
        {
            numero, nombre = $"Vistas contables {numero}", tipoCuenta = TipoCuentaContable.Posteo, tipoResultado,
            posteoDirecto = true, bloqueada = false, sangria = 1
        });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return new CuentaCreada(JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid(), numero);
    }

    private async Task<Guid> CrearSocioAsync(string nombre)
    {
        var response = await _client.PostAsJsonAsync("/api/socios-negocio", new { nombreComercial = nombre });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
    }

    /// <summary>Producto con los grupos BIENES/GENERAL/ITBIS18 (los de los setups comodín: venta a la 4101, costo a la 5101/1301).</summary>
    private async Task<Guid> ProductoClasificadoAsync()
    {
        var producto = await _prueba.SembrarProductoAsync();
        await using var conexion = _prueba.NuevaConexion();
        await conexion.ExecuteAsync(
            """UPDATE "Productos" SET "GrupoInventarioId" = @Gi, "GrupoProductoId" = @Gp, "GrupoIvaProductoId" = @Giva WHERE "Id" = @Id""",
            new { Gi = GrupoContableIds.InventarioGeneral, Gp = GrupoContableIds.ProductoBienes, Giva = GrupoContableIds.IvaProductoItbis18, Id = producto });
        return producto;
    }

    private sealed record FacturaPosteada(string Numero, long RegistroContableId);

    /// <summary>Borrador con una línea de cuenta (ITBIS 18 %) y, opcionalmente, una de producto, posteado con el motor real.</summary>
    private async Task<FacturaPosteada> PostearFacturaAsync(
        Guid socio, DateOnly fechaRegistro, (Guid Cuenta, decimal Importe) lineaCuenta,
        (Guid Producto, decimal Cantidad, decimal Precio, Guid Almacen)? lineaProducto = null)
    {
        var creado = await _client.PostAsJsonAsync("/api/facturas-venta/borradores", new
        {
            socioNegocioId = socio, fechaRegistro,
        });
        Assert.True(creado.StatusCode == HttpStatusCode.Created, await creado.Content.ReadAsStringAsync());
        var borrador = (await creado.Content.ReadFromJsonAsync<FacturaVentaBorradorResponse>())!;

        if (lineaProducto is { } lp)
        {
            var producto = await _client.PostAsJsonAsync($"/api/facturas-venta/borradores/{borrador.Id}/lineas", new
            {
                tipo = TipoLineaFactura.Producto, productoId = lp.Producto, almacenId = lp.Almacen, cantidad = lp.Cantidad, precioUnitario = lp.Precio,
            });
            Assert.True(producto.StatusCode == HttpStatusCode.Created, await producto.Content.ReadAsStringAsync());
        }

        var linea = await _client.PostAsJsonAsync($"/api/facturas-venta/borradores/{borrador.Id}/lineas", new
        {
            tipo = TipoLineaFactura.CuentaContable, cuentaContableId = lineaCuenta.Cuenta, cantidad = 1m,
            precioUnitario = lineaCuenta.Importe, grupoIvaProductoId = GrupoContableIds.IvaProductoItbis18
        });
        Assert.True(linea.StatusCode == HttpStatusCode.Created, await linea.Content.ReadAsStringAsync());
        var posteo = await _client.PostAsync($"/api/facturas-venta/borradores/{borrador.Id}/postear", null);
        var cuerpo = await posteo.Content.ReadAsStringAsync();
        Assert.True(posteo.StatusCode == HttpStatusCode.OK, cuerpo);
        var numero = JsonDocument.Parse(cuerpo).RootElement.GetProperty("numero").GetString()!;

        await using var conexion = _prueba.NuevaConexion();
        var registro = await conexion.ExecuteScalarAsync<long>(
            """SELECT "RegistroContableId" FROM "FacturasVenta" WHERE "Numero" = @numero""", new { numero });
        return new FacturaPosteada(numero, registro);
    }

    private sealed record CobroRegistrado(long RegistroContableId);

    private async Task<CobroRegistrado> RegistrarPagoAsync(Guid socio, decimal importe, DateOnly fechaRegistro)
    {
        var response = await _client.PostAsJsonAsync("/api/cobros", new { socioNegocioId = socio, importe, fechaRegistro });
        var cuerpo = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, cuerpo);
        var numeroRegistro = JsonDocument.Parse(cuerpo).RootElement.GetProperty("registroContable").GetString();
        await using var conexion = _prueba.NuevaConexion();
        return new CobroRegistrado(await conexion.ExecuteScalarAsync<long>(
            """SELECT "Id" FROM "RegistrosContables" WHERE "NumeroRegistro" = @numeroRegistro""", new { numeroRegistro }));
    }

    private async Task RegistrarAsync(DateOnly fecha, params (Guid Cuenta, decimal Importe)[] lineas)
    {
        await using var scope = _factory.Services.CreateAsyncScope();
        var sesion = scope.ServiceProvider.GetRequiredService<IDbSession>();
        var registro = scope.ServiceProvider.GetRequiredService<IRegistroContable>();

        await using var tx = await sesion.BeginTransactionAsync();
        var resultado = await registro.RegistrarAsync(new AsientoContable(
            fecha, fecha, TipoDocumentoContable.Ninguno, null, "Asiento de las vistas", TipoOrigenMovimiento.Diario,
            $"VIS-{Guid.NewGuid():N}"[..30], [.. lineas.Select(x => new LineaAsiento(x.Cuenta, x.Importe, null))]));
        Assert.True(resultado.EsExito, resultado.EsFallo ? string.Join("; ", resultado.Errores.Select(e => e.Mensaje)) : "");
        await sesion.CommitAsync();
    }
}
