using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Mvc.Testing;
using Npgsql;
using OpenSource1.Api;
using OpenSource1.Application.Features.FacturasVenta.Borradores.Dtos;
using OpenSource1.Application.Features.MovimientosCliente.Dtos;
using OpenSource1.Core.Common;
using OpenSource1.Core.Entities.Contabilidad;
using OpenSource1.Core.Enums;
using OpenSource1.SmokeTests.TestInfrastructure;

namespace OpenSource1.SmokeTests.Api;

/// <summary>
/// Vistas de clientes (Task 7.3): <c>GET api/clientes/{id}/movimientos</c> con los filtros nuevos (<c>tipoDocumento</c>,
/// <c>fechaCorte</c>) y <c>GET api/clientes/estado-cuenta</c> (antigüedad de saldos por tramos a una fecha de corte), ambas
/// CanConsult. Los datos se crean con los servicios reales por la API: facturas posteadas con el motor de posteo (vencimiento
/// por el término de pago del socio, 30 días), pagos y aplicaciones con los comandos de cobros. Review Focus 2 (restante, no
/// original; "sin aplicar" resta; la fecha de corte excluye lo posterior) y 5 (filtros inválidos → 400 o valor por defecto).
/// REQUIERE DOCKER.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class ClientesVistasApiTests(PostgresTestFixture fixture) : IClassFixture<PostgresTestFixture>, IAsyncLifetime
{
    private const string UrlEstado = "/api/clientes/estado-cuenta";

    private static readonly DateOnly Corte = new(2026, 6, 30);
    private static readonly DateOnly CorteJulio = new(2026, 7, 31);
    private static readonly DateOnly CorteAntesDelPago = new(2026, 5, 24);

    private WebApplicationFactory<Program> _factory = null!;
    private HttpClient _client = null!;
    private Guid _termino30;
    private Guid _cuentaIngresos;

    public async Task InitializeAsync()
    {
        _factory = fixture.CreateFactory();
        _client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        _client.DefaultRequestHeaders.Add("X-Test-User", "administrador");
        _client.DefaultRequestHeaders.Add("X-Test-Roles", "Administrador");
        _termino30 = await CrearTerminoAsync(30);
        _cuentaIngresos = await CrearCuentaAsync();
    }

    public async Task DisposeAsync()
    {
        _client.Dispose();
        await _factory.DisposeAsync();
    }

    [Fact]
    public async Task Vistas_ExigenAutenticacion_YCanConsultBasta()
    {
        var socio = await CrearSocioAsync("Roles vistas");
        using var client = _factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        foreach (var url in new[] { UrlEstado, $"/api/clientes/{socio}/movimientos?tipoDocumento=1&fechaCorte=2026-06-30" })
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

    // ── Review Focus 2: antigüedad ────────────────────────────────────────────────────────────────────────────────────

    [Fact]
    public async Task ReviewFocus2_Antiguedad_PorRestante_SinAplicarResta_YFechaDeCorteExcluyeLoPosterior()
    {
        var prefijo = Prefijo();
        var (alfa, beta, gamma) = await SembrarTresClientesAsync(prefijo);

        // ── Corte 30/06: FA3 (236) parcialmente pagada entra en 31-60 por su restante 136; PA2 (−80) sin aplicar resta;
        //    PA3 (05/07) y su aplicación a FA1, y la aplicación de PA2 a FA2 del 15/07, quedan fuera. Gamma (solo un pago del 10/07) no aparece.
        var estado = await EstadoAsync($"fechaCorte=2026-06-30&texto={prefijo}");
        Assert.Equal(Corte, estado.FechaCorte);
        Assert.Equal([alfa, beta], estado.Pagina.Items.Select(c => c.SocioNegocioId));
        var a = estado.Pagina.Items[0];
        Assert.Equal((59m, 11.8m, 136m, 23.6m, 118m, -80m, 268.4m, 6),
            (a.Corriente, a.Dias1a30, a.Dias31a60, a.Dias61a90, a.Mas90, a.SinAplicar, a.Total, a.DocumentosAbiertos));
        Assert.Equal($"{prefijo} Alfa", a.Nombre);
        Assert.False(string.IsNullOrEmpty(a.Codigo));
        var b = estado.Pagina.Items[1];
        Assert.Equal((0m, 0m, 0m, 0m, 0m, 0m, 0m, 0), (b.Corriente, b.Dias1a30, b.Dias31a60, b.Dias61a90, b.Mas90, b.SinAplicar, b.Total, b.DocumentosAbiertos));

        // Total por cliente = saldo a la fecha de corte (Σ detalle con fecha ≤ corte de los movimientos con fecha ≤ corte).
        Assert.Equal(await SaldoSqlAsync(alfa, Corte), a.Total);
        Assert.Equal(a.Corriente + a.Dias1a30 + a.Dias31a60 + a.Dias61a90 + a.Mas90 + a.SinAplicar, a.Total);
        Assert.Equal((59m, 11.8m, 136m, 23.6m, 118m, -80m, 268.4m),
            (estado.Totales.Corriente, estado.Totales.Dias1a30, estado.Totales.Dias31a60, estado.Totales.Dias61a90,
             estado.Totales.Mas90, estado.Totales.SinAplicar, estado.Totales.Total));

        // Los movimientos del cliente a esa fecha de corte muestran el mismo restante (la suma = el total del estado).
        var movimientos = await MovimientosAsync(alfa, "fechaCorte=2026-06-30");
        Assert.Equal(7, movimientos.Total);
        Assert.Equal(a.Total, movimientos.Items.Sum(m => m.ImporteRestante));
        Assert.Equal([118m, 23.6m, 236m, 11.8m, -100m, 59m, -80m], movimientos.Items.Select(m => m.ImporteOriginal));
        Assert.Equal([118m, 23.6m, 136m, 11.8m, 0m, 59m, -80m], movimientos.Items.Select(m => m.ImporteRestante));
        Assert.Equal([true, true, true, true, false, true, true], movimientos.Items.Select(m => m.Abierta));
        Assert.Equal(6, (await MovimientosAsync(alfa, "fechaCorte=2026-06-30&soloAbiertos=true")).Total);

        // ── Corte 31/07: PA3 aplicada a FA1 (88) y 20 de PA2 a FA2 (3.6; PA2 queda en −60); todo ha envejecido un tramo;
        //    Gamma aparece con su pago sin aplicar.
        var julio = await EstadoAsync($"fechaCorte=2026-07-31&texto={prefijo}");
        Assert.Equal([alfa, beta, gamma], julio.Pagina.Items.Select(c => c.SocioNegocioId));
        var aj = julio.Pagina.Items[0];
        Assert.Equal((0m, 59m, 11.8m, 136m, 91.6m, -60m, 238.4m, 6),
            (aj.Corriente, aj.Dias1a30, aj.Dias31a60, aj.Dias61a90, aj.Mas90, aj.SinAplicar, aj.Total, aj.DocumentosAbiertos));
        var gj = julio.Pagina.Items[2];
        Assert.Equal((-40m, -40m, 1), (gj.SinAplicar, gj.Total, gj.DocumentosAbiertos));
        Assert.Equal(238.4m - 40m, julio.Totales.Total);

        // El total a una fecha de corte posterior a todo = el saldo actual del cliente.
        var saldo = (await _client.GetFromJsonAsync<SaldoClienteResponse>($"/api/clientes/{alfa}/saldo"))!;
        Assert.Equal(saldo.Saldo, aj.Total);

        // ── Corte 24/05, antes del primer pago: FA3 entra en 1-30 por su importe original completo; FA5 aún no existe.
        var mayo = await EstadoAsync($"fechaCorte=2026-05-24&socioId={alfa}");
        var am = Assert.Single(mayo.Pagina.Items);
        Assert.Equal((11.8m, 236m, 23.6m, 0m, 118m, 0m, 389.4m, 4),
            (am.Corriente, am.Dias1a30, am.Dias31a60, am.Dias61a90, am.Mas90, am.SinAplicar, am.Total, am.DocumentosAbiertos));
        Assert.Equal(await SaldoSqlAsync(alfa, CorteAntesDelPago), am.Total);
    }

    [Fact]
    public async Task Tramos_LimitesExactosDeDiasVencidos()
    {
        var prefijo = Prefijo();
        var socio = await CrearSocioAsync($"{prefijo} Limites", _termino30);
        await PostearFacturaAsync(socio, new DateOnly(2026, 3, 1), 100m); // 118, vence el 31/03
        var vencimiento = new DateOnly(2026, 3, 31);

        var casos = new (int Dias, string Tramo)[]
        {
            (-1, "Corriente"), (0, "Corriente"), (1, "Dias1a30"), (30, "Dias1a30"), (31, "Dias31a60"), (60, "Dias31a60"),
            (61, "Dias61a90"), (90, "Dias61a90"), (91, "Mas90"), (400, "Mas90"),
        };
        foreach (var (dias, tramo) in casos)
        {
            var corte = vencimiento.AddDays(dias);
            var fila = Assert.Single((await EstadoAsync($"fechaCorte={corte:yyyy-MM-dd}&socioId={socio}")).Pagina.Items);
            var tramos = new Dictionary<string, decimal>
            {
                ["Corriente"] = fila.Corriente, ["Dias1a30"] = fila.Dias1a30, ["Dias31a60"] = fila.Dias31a60,
                ["Dias61a90"] = fila.Dias61a90, ["Mas90"] = fila.Mas90,
            };
            Assert.True(tramos[tramo] == 118m && tramos.Values.Sum() == 118m, $"{dias} días: se esperaba 118 en {tramo}: {string.Join(", ", tramos)}");
        }

        // Antes de registrar la factura, el cliente no tiene movimientos: no aparece.
        Assert.Empty((await EstadoAsync($"fechaCorte=2026-02-28&socioId={socio}")).Pagina.Items);
    }

    [Fact]
    public async Task AplicacionFechadaAntesDelPago_NoReduceLaFacturaHastaQueElPagoExiste()
    {
        var socio = await CrearSocioAsync($"{Prefijo()} Aplicacion adelantada", _termino30);
        var factura = await PostearFacturaAsync(socio, new DateOnly(2026, 6, 1), 100m); // 118, vence el 01/07
        var pago = await RegistrarPagoAsync(socio, 50m, new DateOnly(2026, 7, 10));
        // Part 1: await AplicarAsync(factura, pago, 50m, new DateOnly(2026, 6, 20));
        // New writes reject this chronology. Preserve coverage of already-existing legacy books
        // by seeding the two historical, append-only application rows directly in the test database.
        await using var legado = new NpgsqlConnection(fixture.AppConnectionString);
        await legado.ExecuteAsync("""
            INSERT INTO "MovimientosClienteDetalle" (
                "MovimientoClienteId", "TipoMovimiento", "Importe", "FechaRegistro", "MovimientoClienteAplicadoId",
                "TipoOrigen", "ClaveOrigen", "CreatedAtUtc", "CreatedBy", "UsuarioId")
            SELECT m."Id", @Tipo, v.importe, @Fecha, v.contraparte,
                   @Origen, 'test:aplicacion-heredada', m."CreatedAtUtc", m."CreatedBy", m."UsuarioId"
            FROM (VALUES (@Factura::bigint, -50::numeric, @Pago::bigint),
                         (@Pago::bigint, 50::numeric, @Factura::bigint)) v(id, importe, contraparte)
            JOIN "MovimientosCliente" m ON m."Id" = v.id
            """, new { Factura = factura, Pago = pago, Fecha = new DateOnly(2026, 6, 20),
                Tipo = (short)TipoDetalleCliente.Aplicacion, Origen = (short)TipoOrigenMovimiento.Cobro });

        // Al 30/06 el pago no existe: la factura sigue en 118 (no 68) y el total es el saldo contable de CxC a esa fecha.
        var junio = Assert.Single((await EstadoAsync($"fechaCorte=2026-06-30&socioId={socio}")).Pagina.Items);
        Assert.Equal((118m, 0m, 118m), (junio.Corriente, junio.SinAplicar, junio.Total));
        Assert.Equal([118m], (await MovimientosAsync(socio, "fechaCorte=2026-06-30")).Items.Select(m => m.ImporteRestante));

        // Al 10/07 existen los dos y la aplicación ya cuenta.
        var julio = Assert.Single((await EstadoAsync($"fechaCorte=2026-07-10&socioId={socio}")).Pagina.Items);
        Assert.Equal((68m, 0m, 68m), (julio.Dias1a30, julio.SinAplicar, julio.Total));
    }

    [Fact]
    public async Task EstadoCuenta_PaginadoPorCliente_OrdenEstable_Texto_SocioId_SoloConSaldo_YCortePorDefectoHoy()
    {
        var prefijo = Prefijo();
        var (alfa, beta, gamma) = await SembrarTresClientesAsync(prefijo);

        // Una página por cliente; los totales son de TODOS los clientes filtrados, no solo de la página.
        var nombres = new List<Guid>();
        for (var pagina = 1; pagina <= 3; pagina++)
        {
            var p = await EstadoAsync($"fechaCorte=2026-07-31&texto={prefijo}&tamanoPagina=1&pagina={pagina}");
            Assert.Equal((3L, 3, 198.4m), (p.Pagina.Total, p.Pagina.TotalPaginas, p.Totales.Total));
            nombres.Add(Assert.Single(p.Pagina.Items).SocioNegocioId);
        }

        Assert.Equal([alfa, beta, gamma], nombres);

        // Orden por total descendente y por código (el código lo asigna la serie SOCIOS al crear).
        var porTotal = await EstadoAsync($"fechaCorte=2026-07-31&texto={prefijo}&ordenarPor=Total&descendente=true");
        Assert.Equal([alfa, beta, gamma], porTotal.Pagina.Items.Select(c => c.SocioNegocioId));
        var porCodigo = await EstadoAsync($"fechaCorte=2026-07-31&texto={prefijo}&ordenarPor=Codigo&descendente=true");
        Assert.Equal(porCodigo.Pagina.Items.Select(c => c.Codigo).OrderDescending(StringComparer.Ordinal), porCodigo.Pagina.Items.Select(c => c.Codigo));

        // Texto por código (minúsculas indiferentes) y por nombre; socioId = un cliente; soloConSaldo oculta a Beta (todo en 0).
        var codigoGamma = porCodigo.Pagina.Items.Single(c => c.SocioNegocioId == gamma).Codigo;
        Assert.Equal([gamma], (await EstadoAsync($"fechaCorte=2026-07-31&texto={codigoGamma.ToLowerInvariant()}")).Pagina.Items.Select(c => c.SocioNegocioId));
        Assert.Equal([beta], (await EstadoAsync($"fechaCorte=2026-07-31&texto={Uri.EscapeDataString($"{prefijo} beta")}")).Pagina.Items.Select(c => c.SocioNegocioId));
        Assert.Equal([beta], (await EstadoAsync($"fechaCorte=2026-07-31&socioId={beta}")).Pagina.Items.Select(c => c.SocioNegocioId));
        Assert.Equal([alfa, gamma], (await EstadoAsync($"fechaCorte=2026-07-31&texto={prefijo}&soloConSaldo=true")).Pagina.Items.Select(c => c.SocioNegocioId));

        // Un socio inexistente en el filtro no es entidad de ruta: 200 vacío.
        var ninguno = await EstadoAsync($"socioId={Guid.NewGuid()}");
        Assert.Empty(ninguno.Pagina.Items);
        Assert.Equal(0m, ninguno.Totales.Total);

        // Sin fecha de corte: hoy (UTC). Un pago registrado dentro de 10 días no cuenta hoy y sí a esa fecha.
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
        await RegistrarPagoAsync(gamma, 5m, hoy.AddDays(10));
        var porDefecto = await EstadoAsync($"socioId={gamma}");
        Assert.Equal(hoy, porDefecto.FechaCorte);
        Assert.Equal(-40m, Assert.Single(porDefecto.Pagina.Items).Total);
        Assert.Equal(-45m, Assert.Single((await EstadoAsync($"socioId={gamma}&fechaCorte={hoy.AddDays(10):yyyy-MM-dd}")).Pagina.Items).Total);
    }

    [Fact]
    public async Task MovimientosCliente_FiltroTipoDocumento_FechaCorte_YContratoAnteriorIntacto()
    {
        var socio = await CrearSocioAsync($"{Prefijo()} Tipos", _termino30);
        var factura = await PostearFacturaAsync(socio, new DateOnly(2026, 4, 1), 100m);
        var pago = await RegistrarPagoAsync(socio, 30m, new DateOnly(2026, 4, 15));
        await AplicarAsync(factura, pago, 30m, new DateOnly(2026, 4, 20));

        Assert.Equal([factura], (await MovimientosAsync(socio, "tipoDocumento=1")).Items.Select(m => m.Id));
        Assert.Equal([pago], (await MovimientosAsync(socio, "tipoDocumento=3")).Items.Select(m => m.Id));
        Assert.Empty((await MovimientosAsync(socio, "tipoDocumento=2")).Items);

        // Sin fechaCorte: restante actual (contrato de la Fase 6). Con fechaCorte: restante a esa fecha y sin lo posterior.
        Assert.Equal([88m, 0m], (await MovimientosAsync(socio, "")).Items.Select(m => m.ImporteRestante));
        Assert.Equal([118m, -30m], (await MovimientosAsync(socio, "fechaCorte=2026-04-19")).Items.Select(m => m.ImporteRestante));
        Assert.Equal([118m], (await MovimientosAsync(socio, "fechaCorte=2026-04-14")).Items.Select(m => m.ImporteRestante));
        Assert.Equal([pago], (await MovimientosAsync(socio, "fechaCorte=2026-04-19&tipoDocumento=3&soloAbiertos=true")).Items.Select(m => m.Id));
        Assert.Empty((await MovimientosAsync(socio, "fechaCorte=2026-04-20&tipoDocumento=3&soloAbiertos=true")).Items);

        // Orden por restante con fecha de corte: se ordena por el restante a esa fecha.
        Assert.Equal([pago, factura], (await MovimientosAsync(socio, "fechaCorte=2026-04-19&ordenarPor=ImporteRestante&descendente=false")).Items.Select(m => m.Id));

        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/clientes/{Guid.NewGuid()}/movimientos?tipoDocumento=1")).StatusCode);
    }

    // ── Review Focus 5: filtros inválidos ─────────────────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("movimientos", "desde=2026-09-20&hasta=2026-09-10", "Desde")]
    [InlineData("movimientos", "tipoDocumento=9", "TipoDocumento")]
    [InlineData("movimientos", "tipoDocumento=0", "TipoDocumento")]
    [InlineData("movimientos", "tipoDocumento=70000", "TipoDocumento")]
    public async Task FiltroInvalido_Devuelve400ConCampo(string vista, string query, string campo)
    {
        var url = await UrlAsync(vista, query);
        var respuesta = await _client.GetAsync(url);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.BadRequest, $"{url}: {respuesta.StatusCode} {cuerpo}");
        Assert.True(JsonDocument.Parse(cuerpo).RootElement.GetProperty("errors").TryGetProperty(campo, out _), cuerpo);
    }

    /// <summary>
    /// Una página enorme no es error (mismo contrato que el resto de listados): <c>PageRequest.Offset</c> se acota y la vista
    /// responde 200 con una página vacía que conserva la página pedida (la página Blazor salta entonces a la última).
    /// </summary>
    [Theory]
    [InlineData("movimientos", "pagina=2147483647")]
    [InlineData("estado", "pagina=2147483647")]
    [InlineData("estado", "pagina=2147483647&tamanoPagina=2")]
    public async Task PaginaEnorme_Devuelve200ConPaginaVacia(string vista, string query)
    {
        var url = await UrlAsync(vista, query);
        var respuesta = await _client.GetAsync(url);
        var cuerpo = await respuesta.Content.ReadAsStringAsync();
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, $"{url}: {respuesta.StatusCode} {cuerpo}");
        var raiz = JsonDocument.Parse(cuerpo).RootElement;
        var pag = vista == "estado" ? raiz.GetProperty("pagina") : raiz;
        Assert.Equal(0, pag.GetProperty("items").GetArrayLength());
        Assert.Equal(int.MaxValue, pag.GetProperty("pagina").GetInt32());
    }

    [Theory]
    [InlineData("movimientos", "tipoDocumento=abc")]
    [InlineData("movimientos", "fechaCorte=2026-02-31")]
    [InlineData("estado", "fechaCorte=abc")]
    [InlineData("estado", "socioId=no-guid")]
    [InlineData("estado", "soloConSaldo=quizas")]
    public async Task ValorNoEnlazable_Devuelve400(string vista, string query)
    {
        var respuesta = await _client.GetAsync(await UrlAsync(vista, query));
        Assert.Equal(HttpStatusCode.BadRequest, respuesta.StatusCode);
    }

    [Theory]
    [InlineData("movimientos")]
    [InlineData("estado")]
    public async Task PaginaCeroTamanoExcesivoYOrdenNoPermitido_SeNormalizan(string vista)
    {
        var inyeccion = Uri.EscapeDataString("\"Total\"; DROP TABLE x");
        foreach (var (query, pagina, tamano) in new[]
                 {
                     ("pagina=0", 1, 50), ("tamanoPagina=1000", 1, 200), ("pagina=-5&tamanoPagina=0", 1, 50),
                     ($"ordenarPor={inyeccion}", 1, 50),
                 })
        {
            var url = await UrlAsync(vista, query);
            var respuesta = await _client.GetAsync(url);
            var cuerpo = await respuesta.Content.ReadAsStringAsync();
            Assert.True(respuesta.StatusCode == HttpStatusCode.OK, $"{url}: {respuesta.StatusCode} {cuerpo}");
            var raiz = JsonDocument.Parse(cuerpo).RootElement;
            var pag = vista == "estado" ? raiz.GetProperty("pagina") : raiz;
            Assert.Equal((pagina, tamano), (pag.GetProperty("pagina").GetInt32(), pag.GetProperty("tamanoPagina").GetInt32()));
        }
    }

    // ── Escenario ─────────────────────────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Tres clientes con término de 30 días (importes con ITBIS 18 %). Alfa: cinco facturas, un pago aplicado en parte a FA3,
    /// un pago sin aplicar al 30/06 (aplicado en parte a FA2 el 15/07) y un pago de julio aplicado a FA1. Beta: una factura pagada del todo. Gamma: solo un pago del 10/07.
    /// </summary>
    private async Task<(Guid Alfa, Guid Beta, Guid Gamma)> SembrarTresClientesAsync(string prefijo)
    {
        var alfa = await CrearSocioAsync($"{prefijo} Alfa", _termino30);
        var beta = await CrearSocioAsync($"{prefijo} Beta", _termino30);
        var gamma = await CrearSocioAsync($"{prefijo} Gamma", _termino30);

        var fa1 = await PostearFacturaAsync(alfa, new DateOnly(2026, 1, 10), 100m);  // 118, vence 09/02
        var fa2 = await PostearFacturaAsync(alfa, new DateOnly(2026, 3, 10), 20m);   // 23.6, vence 09/04
        var fa3 = await PostearFacturaAsync(alfa, new DateOnly(2026, 4, 20), 200m);  // 236, vence 20/05
        await PostearFacturaAsync(alfa, new DateOnly(2026, 5, 15), 10m);             // 11.8, vence 14/06
        await PostearFacturaAsync(alfa, new DateOnly(2026, 6, 15), 50m);             // 59, vence 15/07
        var pa1 = await RegistrarPagoAsync(alfa, 100m, new DateOnly(2026, 5, 25));
        await AplicarAsync(fa3, pa1, 100m, new DateOnly(2026, 5, 25));
        var pa2 = await RegistrarPagoAsync(alfa, 80m, new DateOnly(2026, 6, 20));   // sin aplicar al 30/06
        await AplicarAsync(fa2, pa2, 20m, new DateOnly(2026, 7, 15));               // aplicación posterior al corte de junio
        var pa3 = await RegistrarPagoAsync(alfa, 30m, new DateOnly(2026, 7, 5));
        await AplicarAsync(fa1, pa3, 30m, new DateOnly(2026, 7, 5));

        var fb1 = await PostearFacturaAsync(beta, new DateOnly(2026, 2, 1), 50m);    // 59
        var pb1 = await RegistrarPagoAsync(beta, 59m, new DateOnly(2026, 3, 1));
        await AplicarAsync(fb1, pb1, 59m, new DateOnly(2026, 3, 1));

        await RegistrarPagoAsync(gamma, 40m, new DateOnly(2026, 7, 10));
        return (alfa, beta, gamma);
    }

    private static string Prefijo() => $"EC{Guid.NewGuid():N}"[..10].ToUpperInvariant();

    private async Task<string> UrlAsync(string vista, string query) =>
        vista == "estado" ? $"{UrlEstado}?{query}" : $"/api/clientes/{await CrearSocioAsync("Filtros")}/movimientos?{query}";

    private async Task<decimal> SaldoSqlAsync(Guid socio, DateOnly corte)
    {
        await using var conexion = new NpgsqlConnection(fixture.AppConnectionString);
        return await conexion.ExecuteScalarAsync<decimal>(
            """
            SELECT COALESCE(SUM(d."Importe"), 0) FROM "MovimientosClienteDetalle" d
            JOIN "MovimientosCliente" m ON m."Id" = d."MovimientoClienteId"
            WHERE m."SocioNegocioId" = @Socio AND m."FechaRegistro" <= @Corte AND d."FechaRegistro" <= @Corte
            """, new { Socio = socio, Corte = corte });
    }

    private async Task<EstadoCuentaResponse> EstadoAsync(string query)
    {
        var response = await _client.GetAsync($"{UrlEstado}?{query}");
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<EstadoCuentaResponse>())!;
    }

    private async Task<PagedResult<MovimientoClienteResponse>> MovimientosAsync(Guid socio, string query)
    {
        var response = await _client.GetAsync($"/api/clientes/{socio}/movimientos?{query}");
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return (await response.Content.ReadFromJsonAsync<PagedResult<MovimientoClienteResponse>>())!;
    }

    private async Task<Guid> CrearTerminoAsync(int dias)
    {
        var response = await _client.PostAsJsonAsync("/api/terminos-pago", new
        {
            codigo = $"T{Guid.NewGuid():N}"[..15], descripcion = $"{dias} días", diasVencimiento = dias, diasDescuento = 0, porcentajeDescuento = 0m
        });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
    }

    private async Task<Guid> CrearCuentaAsync()
    {
        var response = await _client.PostAsJsonAsync("/api/cuentas-contables", new
        {
            numero = $"48{Random.Shared.Next(10_000, 99_999)}", nombre = "Ingresos estado de cuenta", tipoCuenta = TipoCuentaContable.Posteo,
            tipoResultado = TipoResultadoCuenta.Resultado, posteoDirecto = true, bloqueada = false, sangria = 1
        });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
    }

    private async Task<Guid> CrearSocioAsync(string nombre, Guid? terminoPagoId = null)
    {
        var response = await _client.PostAsJsonAsync("/api/socios-negocio", new { nombreComercial = nombre, terminoPagoId });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
    }

    /// <summary>Borrador con una línea de cuenta (ITBIS 18 %) posteado con el motor real; devuelve el movimiento de cliente.</summary>
    private async Task<long> PostearFacturaAsync(Guid socio, DateOnly fechaRegistro, decimal baseImponible)
    {
        var creado = await _client.PostAsJsonAsync("/api/facturas-venta/borradores", new { socioNegocioId = socio, fechaRegistro });
        Assert.True(creado.StatusCode == HttpStatusCode.Created, await creado.Content.ReadAsStringAsync());
        var borrador = (await creado.Content.ReadFromJsonAsync<FacturaVentaBorradorResponse>())!;
        var linea = await _client.PostAsJsonAsync($"/api/facturas-venta/borradores/{borrador.Id}/lineas", new
        {
            tipo = TipoLineaFactura.CuentaContable, cuentaContableId = _cuentaIngresos, cantidad = 1m,
            precioUnitario = baseImponible, grupoIvaProductoId = GrupoContableIds.IvaProductoItbis18
        });
        Assert.True(linea.StatusCode == HttpStatusCode.Created, await linea.Content.ReadAsStringAsync());
        var posteo = await _client.PostAsync($"/api/facturas-venta/borradores/{borrador.Id}/postear", null);
        Assert.True(posteo.StatusCode == HttpStatusCode.OK, await posteo.Content.ReadAsStringAsync());

        var abiertos = (await _client.GetFromJsonAsync<List<MovimientoClienteResponse>>($"/api/clientes/{socio}/movimientos-abiertos"))!;
        return abiertos.Where(m => m.TipoDocumento == TipoDocumentoCliente.Factura).Max(m => m.Id);
    }

    private async Task<long> RegistrarPagoAsync(Guid socio, decimal importe, DateOnly fechaRegistro)
    {
        var response = await _client.PostAsJsonAsync("/api/cobros", new { socioNegocioId = socio, importe, fechaRegistro });
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("movimientoClienteId").GetInt64();
    }

    private async Task AplicarAsync(long factura, long pago, decimal importe, DateOnly fechaRegistro)
    {
        var response = await _client.PostAsJsonAsync(
            "/api/cobros/aplicaciones", new { movimientoFacturaId = factura, movimientoPagoId = pago, importe, fechaRegistro });
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }
}
