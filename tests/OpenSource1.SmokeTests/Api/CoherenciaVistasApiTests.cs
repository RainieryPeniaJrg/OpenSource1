using System.Net;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Dapper;
using Microsoft.AspNetCore.Mvc.Testing;
using Microsoft.Extensions.DependencyInjection;
using OpenSource1.Api;
using OpenSource1.Application.Features.Contabilidad.Dtos;
using OpenSource1.Application.Features.FacturasVenta.Borradores.Dtos;
using OpenSource1.Application.Features.Inventario.Consultas.Dtos;
using OpenSource1.Application.Features.MovimientosCliente.Dtos;
using OpenSource1.Application.Features.NotasCreditoVenta.Borradores.Dtos;
using OpenSource1.Application.Features.NotasCreditoVenta.Posteo;
using OpenSource1.Application.Services.Contabilidad;
using OpenSource1.Core.Common;
using OpenSource1.Core.Entities.Contabilidad;
using OpenSource1.Core.Enums;
using OpenSource1.SmokeTests.TestInfrastructure;

namespace OpenSource1.SmokeTests.Api;

/// <summary>
/// Coherencia ENTRE las vistas de la Fase 7 (cierre de la fase), con su propia base: un escenario completo con los procesos
/// reales (dos clientes, dos facturas posteadas con producto, dos cobros —uno con sobrepago— aplicados después de cobrarse,
/// batch de costo, una entrada retroactiva, la rutina de ajuste de costo y otra vez el batch) y, para varias fechas de corte
/// intermedias y la final, todo por la API:
/// <list type="bullet">
/// <item>estado de cuenta: Σ tramos (incluido "sin aplicar") = total, por cliente y en los totales; total del cliente = Σ
/// restantes de sus movimientos a esa fecha; total de la cartera = saldo final de la CxC en el balance hasta esa fecha (con
/// y sin <c>desde</c>); en la CxC y la 1301, saldo inicial con <c>desde</c> = saldo final hasta el día anterior y saldo
/// inicial + débitos − créditos = saldo final;</item>
/// <item>existencias: <c>ValorTotal</c> = Σ valores = saldo final de la 1301 en el balance; existencia de cada fila =
/// <c>IConsultaInventario.ExistenciaAsync</c> a esa fecha.</item>
/// </list>
/// Task 8.7: el escenario incluye una nota de crédito PARCIAL con devolución de inventario (posteada por la API después del
/// ajuste de costo, aplicada automáticamente a lo que quedaba de la factura y con el resto como saldo a favor) y otra vez el
/// batch de costo: el estado de cuenta sigue igual a la CxC del balance y las existencias a la 1301 en cada corte.
/// Mutaciones de control (fallan): saldo inicial del balance ignorado o desplazado un día; aplicaciones posteriores al corte
/// contadas en el estado de cuenta. REQUIERE DOCKER.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class CoherenciaVistasApiTests(PostgresTestFixture fixture) : IClassFixture<PostgresTestFixture>, IAsyncLifetime
{
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
    public async Task EstadoCuenta_MovimientosCliente_Balance_Existencias_CoincidenEnCadaFechaDeCorte()
    {
        var ingresos = await CrearCuentaAsync();
        var producto = await ProductoClasificadoAsync();
        var almacen = await _prueba.SembrarAlmacenAsync();
        var a = await CrearSocioAsync("Coherencia A");
        var b = await CrearSocioAsync("Coherencia B");

        // Entrada 10 u a 5 (05/01). A: 2 u a 50 + 20 a la cuenta propia (ITBIS 18 %: 141.6) el 10/01; B: 1 u a 30 + 100 (153.4)
        // el 15/02. Cobros: A 100 el 28/02 (parcial; último día del mes, para que el corte con desde = 01/03 pase por el saldo
        // inicial), B 200 el 05/03 (sobrepago de 46.6). Batch de costo.
        await _prueba.RegistrarOkAsync(LibroInventarioPrueba.Entrada(producto, almacen, 10m, 5m, new DateOnly(2022, 1, 5)));
        var facturaA = await PostearFacturaAsync(a, new DateOnly(2022, 1, 10), (ingresos, 20m), (producto, 2m, 50m, almacen));
        var facturaB = await PostearFacturaAsync(b, new DateOnly(2022, 2, 15), (ingresos, 100m), (producto, 1m, 30m, almacen));
        var cobroA = await RegistrarPagoAsync(a, 100m, new DateOnly(2022, 2, 28));
        var cobroB = await RegistrarPagoAsync(b, 200m, new DateOnly(2022, 3, 5));
        await PostearCostoAsync(producto);

        // Aplicaciones fechadas después de su cobro: A 100 el 01/03; B la factura entera (153.4) el 10/03 (quedan 46.6 sin aplicar).
        await AplicarAsync(facturaA, cobroA, 100m, new DateOnly(2022, 3, 1));
        await AplicarAsync(facturaB, cobroB, 153.4m, new DateOnly(2022, 3, 10));

        // Entrada retroactiva (2 u a 16 el 05/01): cambia el costo de las dos ventas; ajuste de costo y otra vez el batch.
        await _prueba.RegistrarOkAsync(LibroInventarioPrueba.Entrada(producto, almacen, 2m, 16m, new DateOnly(2022, 1, 5)));
        var ajuste = await _prueba.AjustarOkAsync(producto);
        Assert.True(ajuste.MovimientosValorCreados > 0, "La entrada retroactiva no generó ajustes de costo.");
        await PostearCostoAsync(producto);

        // Nota de crédito parcial de A el 15/03 por la API (Task 8.7): 1 de las 2 u, con devolución (50 + 9 de ITBIS = 59). Se aplica
        // sola a lo que quedaba de la factura (41.6) y el resto (17.4) queda a favor del cliente; la entrada vuelve al costo de la
        // salida (ya ajustado) y el batch de costo la lleva a la 1301.
        var nota = await PostearNotaCreditoAsync(a, facturaA, new DateOnly(2022, 3, 15), cantidad: 1m);
        Assert.Equal((59m, 41.6m), (nota.ImporteTotal, nota.ImporteAplicado));
        await PostearCostoAsync(producto);

        var cortes = new[]
        {
            "2022-01-04", "2022-01-05", "2022-01-10", "2022-01-31", "2022-02-14", "2022-02-15", "2022-02-27", "2022-02-28",
            "2022-03-01", "2022-03-05", "2022-03-09", "2022-03-10", "2022-03-14", "2022-03-15", "2022-03-31", "2022-06-30",
            "2022-12-31",
        };
        var fallos = new StringBuilder();
        foreach (var texto in cortes)
        {
            var corte = DateOnly.Parse(texto);
            await ComprobarCorteAsync(corte, [a, b], producto, fallos);
        }

        Assert.True(fallos.Length == 0, fallos.ToString());

        // Control de que el escenario no es trivial (y de la fecha de corte de las aplicaciones, que las sumas anteriores no ven:
        // una aplicación mueve importe entre documentos del mismo cliente sin cambiar su total). El 05/03 el cobro de B (200)
        // está entero sin aplicar (su aplicación es del 10/03) y lo abierto es 41.6 de A (141.6 − 100) + 153.4 de B; el 14/03
        // solo quedan los 41.6 de A y 46.6 sin aplicar de B (cartera −5). Desde el 15/03 la nota (59) cierra la factura de A y deja
        // 17.4 a favor: nada abierto, 64 sin aplicar, cartera −64.
        foreach (var (corte, abierto, sinAplicar, cartera) in new[]
                 {
                     ("2022-03-05", 195m, -200m, -5m), ("2022-03-09", 195m, -200m, -5m), ("2022-03-14", 41.6m, -46.6m, -5m),
                     ("2022-03-15", 0m, -64m, -64m), ("2022-12-31", 0m, -64m, -64m),
                 })
        {
            var t = (await GetAsync<EstadoCuentaResponse>($"/api/clientes/estado-cuenta?fechaCorte={corte}")).Totales;
            Assert.Equal((corte, abierto, sinAplicar, cartera), (corte, t.Corriente + t.Dias1a30 + t.Dias31a60 + t.Dias61a90 + t.Mas90, t.SinAplicar, t.Total));
        }

        // Movimientos de A: la nota aparece como NotaCredito (−59, restante −17.4) y la factura queda cerrada.
        var movimientosA = await GetAsync<PagedResult<MovimientoClienteResponse>>($"/api/clientes/{a}/movimientos?tamanoPagina=50");
        Assert.Equal(
            [(TipoDocumentoCliente.Factura, 141.6m, 0m), (TipoDocumentoCliente.NotaCredito, -59m, -17.4m), (TipoDocumentoCliente.Pago, -100m, 0m)],
            movimientosA.Items.OrderBy(m => m.TipoDocumento).Select(m => (m.TipoDocumento, m.ImporteOriginal, m.ImporteRestante)));

        // Existencias: 9 u el 14/03 y 10 u (la devolución) desde el 15/03; los movimientos del producto muestran la devolución.
        Assert.Equal(9m, Assert.Single((await GetAsync<ExistenciasVistaResponse>("/api/inventario/existencias?fecha=2022-03-14")).Pagina.Items).Existencia);
        var existencias = await GetAsync<ExistenciasVistaResponse>("/api/inventario/existencias?fecha=2022-12-31");
        Assert.Equal(10m, Assert.Single(existencias.Pagina.Items).Existencia);
        Assert.True(existencias.ValorTotal > 0m);
        var devoluciones = await GetAsync<PagedResult<MovimientoProductoVistaResponse>>(
            $"/api/inventario/movimientos-producto?productoId={producto}&tipoOrigen={(int)TipoOrigenMovimiento.NotaCreditoVenta}");
        var devolucion = Assert.Single(devoluciones.Items);
        Assert.Equal((1m, TipoDocumentoInventario.NotaCreditoVenta, nota.Numero), (devolucion.Cantidad, devolucion.TipoDocumento, devolucion.NumeroDocumento));
    }

    private async Task ComprobarCorteAsync(DateOnly corte, Guid[] socios, Guid producto, StringBuilder fallos)
    {
        void Igual(decimal esperado, decimal real, string que)
        {
            if (esperado != real)
            {
                fallos.AppendLine($"{corte:yyyy-MM-dd} {que}: esperado {esperado}, real {real}");
            }
        }

        // Estado de cuenta: tramos, total por cliente y de la cartera.
        var estado = await GetAsync<EstadoCuentaResponse>($"/api/clientes/estado-cuenta?fechaCorte={corte:yyyy-MM-dd}&tamanoPagina=200");
        Igual(estado.Totales.Total, SumaTramos(estado.Totales), "Σ tramos de los totales vs total");
        Igual(estado.Totales.Total, estado.Pagina.Items.Sum(f => f.Total), "Σ totales de las filas vs total");
        foreach (var socio in socios)
        {
            var movimientos = await GetAsync<PagedResult<MovimientoClienteResponse>>(
                $"/api/clientes/{socio}/movimientos?fechaCorte={corte:yyyy-MM-dd}&tamanoPagina=200");
            var restantes = movimientos.Items.Sum(m => m.ImporteRestante);
            var fila = estado.Pagina.Items.SingleOrDefault(f => f.SocioNegocioId == socio);
            Igual(restantes, fila?.Total ?? 0m, $"socio {socio}: Σ restantes de movimientos vs total del estado de cuenta");
            if (fila is not null)
            {
                Igual(fila.Total, SumaTramos(fila), $"socio {socio}: Σ tramos vs total");
            }
        }

        // Balance hasta el corte, sin desde y con desde = día 1 del mes del corte (saldo inicial + débitos − créditos).
        var balance = await GetAsync<BalanceComprobacionResponse>($"/api/contabilidad/balance-comprobacion?hasta={corte:yyyy-MM-dd}");
        var desde = new DateOnly(corte.Year, corte.Month, 1);
        var balanceMes = await GetAsync<BalanceComprobacionResponse>(
            $"/api/contabilidad/balance-comprobacion?desde={desde:yyyy-MM-dd}&hasta={corte:yyyy-MM-dd}");
        Igual(estado.Totales.Total, SaldoFinal(balance, CuentaContableIds.CxC), "total del estado de cuenta vs saldo final de la CxC");
        Igual(estado.Totales.Total, SaldoFinal(balanceMes, CuentaContableIds.CxC), $"total del estado de cuenta vs CxC (desde {desde})");
        var balanceAnterior = await GetAsync<BalanceComprobacionResponse>(
            $"/api/contabilidad/balance-comprobacion?hasta={desde.AddDays(-1):yyyy-MM-dd}");
        foreach (var cuenta in new[] { CuentaContableIds.CxC, CuentaContableIds.Inventario })
        {
            var fila = balanceMes.Filas.SingleOrDefault(f => f.CuentaContableId == cuenta);
            var (inicial, debitos, creditos, saldoFinal) = fila is null
                ? (0m, 0m, 0m, 0m)
                : (fila.SaldoInicial ?? 0m, fila.Debitos ?? 0m, fila.Creditos ?? 0m, fila.SaldoFinal ?? 0m);
            Igual(SaldoFinal(balanceAnterior, cuenta), inicial, $"cuenta {cuenta}: saldo inicial (desde {desde}) vs saldo final hasta el día anterior");
            Igual(saldoFinal, inicial + debitos - creditos, $"cuenta {cuenta}: saldo inicial + débitos − créditos vs saldo final (desde {desde})");
        }

        // Existencias: valor total = Σ filas = saldo de la 1301; existencia de cada fila = IConsultaInventario.
        var existencias = await GetAsync<ExistenciasVistaResponse>($"/api/inventario/existencias?fecha={corte:yyyy-MM-dd}&tamanoPagina=200");
        Igual(existencias.ValorTotal, existencias.Pagina.Items.Sum(x => x.Valor), "ValorTotal vs Σ valores de las filas");
        Igual(existencias.ValorTotal, SaldoFinal(balance, CuentaContableIds.Inventario), "ValorTotal de existencias vs saldo final de la 1301");
        Igual(existencias.ValorTotal, SaldoFinal(balanceMes, CuentaContableIds.Inventario), $"ValorTotal vs 1301 (desde {desde})");
        foreach (var fila in existencias.Pagina.Items)
        {
            var consulta = await _prueba.ConsultarAsync(q => q.ExistenciaAsync(fila.ProductoId, fila.AlmacenId, corte));
            Igual(consulta, fila.Existencia, $"existencia de {fila.ProductoCodigo}/{fila.AlmacenCodigo} vs IConsultaInventario");
        }

        var total = await _prueba.ConsultarAsync(q => q.ExistenciaAsync(producto, null, corte));
        Igual(total, existencias.Pagina.Items.Where(x => x.ProductoId == producto).Sum(x => x.Existencia), "existencia total del producto");
    }

    private static decimal SumaTramos(EstadoCuentaTramos t) =>
        t.Corriente + t.Dias1a30 + t.Dias31a60 + t.Dias61a90 + t.Mas90 + t.SinAplicar;

    private static decimal SaldoFinal(BalanceComprobacionResponse balance, Guid cuenta) =>
        balance.Filas.SingleOrDefault(f => f.CuentaContableId == cuenta)?.SaldoFinal ?? 0m;

    // ── Helpers (procesos reales por la API o por sus servicios) ────────────────────────────────────────────────────

    private async Task<T> GetAsync<T>(string url)
    {
        using var respuesta = await _client.GetAsync(url);
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, $"{url} → {(int)respuesta.StatusCode}: {await respuesta.Content.ReadAsStringAsync()}");
        return (await respuesta.Content.ReadFromJsonAsync<T>())!;
    }

    private async Task<Guid> CrearCuentaAsync()
    {
        var response = await _client.PostAsJsonAsync("/api/cuentas-contables", new
        {
            numero = "4190", nombre = "Coherencia de vistas", tipoCuenta = TipoCuentaContable.Posteo,
            tipoResultado = TipoResultadoCuenta.Resultado, posteoDirecto = true, bloqueada = false, sangria = 1
        });
        Assert.True(response.StatusCode == HttpStatusCode.Created, await response.Content.ReadAsStringAsync());
        return JsonDocument.Parse(await response.Content.ReadAsStringAsync()).RootElement.GetProperty("id").GetGuid();
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

    /// <summary>Borrador con una línea de producto y una de cuenta (ITBIS 18 %), posteado con el motor real. Devuelve el Id del movimiento de cliente.</summary>
    private async Task<long> PostearFacturaAsync(
        Guid socio, DateOnly fechaRegistro, (Guid Cuenta, decimal Importe) lineaCuenta, (Guid Producto, decimal Cantidad, decimal Precio, Guid Almacen) lineaProducto)
    {
        var creado = await _client.PostAsJsonAsync("/api/facturas-venta/borradores", new
        {
            socioNegocioId = socio, fechaRegistro,
        });
        Assert.True(creado.StatusCode == HttpStatusCode.Created, await creado.Content.ReadAsStringAsync());
        var borrador = (await creado.Content.ReadFromJsonAsync<FacturaVentaBorradorResponse>())!;

        var producto = await _client.PostAsJsonAsync($"/api/facturas-venta/borradores/{borrador.Id}/lineas", new
        {
            tipo = TipoLineaFactura.Producto, productoId = lineaProducto.Producto, almacenId = lineaProducto.Almacen,
            cantidad = lineaProducto.Cantidad, precioUnitario = lineaProducto.Precio,
        });
        Assert.True(producto.StatusCode == HttpStatusCode.Created, await producto.Content.ReadAsStringAsync());
        var cuenta = await _client.PostAsJsonAsync($"/api/facturas-venta/borradores/{borrador.Id}/lineas", new
        {
            tipo = TipoLineaFactura.CuentaContable, cuentaContableId = lineaCuenta.Cuenta, cantidad = 1m,
            precioUnitario = lineaCuenta.Importe, grupoIvaProductoId = GrupoContableIds.IvaProductoItbis18
        });
        Assert.True(cuenta.StatusCode == HttpStatusCode.Created, await cuenta.Content.ReadAsStringAsync());

        var posteo = await _client.PostAsync($"/api/facturas-venta/borradores/{borrador.Id}/postear", null);
        Assert.True(posteo.StatusCode == HttpStatusCode.OK, await posteo.Content.ReadAsStringAsync());
        var facturas = await GetAsync<PagedResult<MovimientoClienteResponse>>($"/api/clientes/{socio}/movimientos?tipoDocumento=1");
        return facturas.Items.Single(m => m.FechaRegistro == fechaRegistro).Id;
    }

    /// <summary>Cobro por <c>api/cobros</c> (caja por defecto), sin aplicar. Devuelve el Id del movimiento de cliente.</summary>
    private async Task<long> RegistrarPagoAsync(Guid socio, decimal importe, DateOnly fechaRegistro)
    {
        var response = await _client.PostAsJsonAsync("/api/cobros", new { socioNegocioId = socio, importe, fechaRegistro });
        var cuerpo = await response.Content.ReadAsStringAsync();
        Assert.True(response.StatusCode == HttpStatusCode.OK, cuerpo);
        return JsonDocument.Parse(cuerpo).RootElement.GetProperty("movimientoClienteId").GetInt64();
    }

    private async Task AplicarAsync(long factura, long pago, decimal importe, DateOnly fechaRegistro)
    {
        var response = await _client.PostAsJsonAsync("/api/cobros/aplicaciones", new
        {
            movimientoFacturaId = factura, movimientoPagoId = pago, importe, fechaRegistro,
        });
        Assert.True(response.StatusCode == HttpStatusCode.OK, await response.Content.ReadAsStringAsync());
    }

    /// <summary>
    /// Nota de crédito por <c>api/notas-credito-venta</c>: borrador desde la factura del movimiento, <paramref name="cantidad"/> de la
    /// línea de producto CON devolución, y posteo.
    /// </summary>
    private async Task<ResultadoPosteoNotaCredito> PostearNotaCreditoAsync(Guid socio, long movimientoFactura, DateOnly fechaRegistro, decimal cantidad)
    {
        var facturas = await GetAsync<PagedResult<MovimientoClienteResponse>>($"/api/clientes/{socio}/movimientos?tipoDocumento=1");
        var numeroFactura = facturas.Items.Single(m => m.Id == movimientoFactura).NumeroDocumento;
        var creado = await _client.PostAsJsonAsync("/api/notas-credito-venta/borradores", new { facturaVentaNumero = numeroFactura, fechaRegistro, copiarLineas = false });
        Assert.True(creado.StatusCode == HttpStatusCode.Created, await creado.Content.ReadAsStringAsync());
        var borrador = (await creado.Content.ReadFromJsonAsync<NotaCreditoVentaBorradorResponse>())!;
        var acreditables = await GetAsync<List<LineaFacturaAcreditableResponse>>($"/api/notas-credito-venta/borradores/{borrador.Id}/lineas-acreditables");
        var linea = await _client.PostAsJsonAsync($"/api/notas-credito-venta/borradores/{borrador.Id}/lineas", new
        {
            lineaFacturaVentaId = acreditables.Single(x => x.Tipo == TipoLineaFactura.Producto).LineaFacturaVentaId, cantidad, devolverInventario = true,
        });
        Assert.True(linea.StatusCode == HttpStatusCode.Created, await linea.Content.ReadAsStringAsync());
        var posteo = await _client.PostAsync($"/api/notas-credito-venta/borradores/{borrador.Id}/postear", null);
        Assert.True(posteo.StatusCode == HttpStatusCode.OK, await posteo.Content.ReadAsStringAsync());
        return (await posteo.Content.ReadFromJsonAsync<ResultadoPosteoNotaCredito>())!;
    }

    private async Task PostearCostoAsync(Guid producto)
    {
        await using var scope = _prueba.Provider.CreateAsyncScope();
        var batch = await scope.ServiceProvider.GetRequiredService<IPosteoCostoInventario>().PostearAsync(producto);
        Assert.Empty(batch.Pendientes);
        Assert.True(batch.Asientos > 0, "El batch de costo no asentó nada.");
    }
}
