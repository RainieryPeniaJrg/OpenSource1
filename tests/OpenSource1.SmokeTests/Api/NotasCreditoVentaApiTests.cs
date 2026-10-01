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
using OpenSource1.Application.Features.FacturasVenta.Calculo;
using OpenSource1.Application.Features.FacturasVenta.Posteo;
using OpenSource1.Application.Features.Inventario.Consultas.Dtos;
using OpenSource1.Application.Features.MovimientosCliente.Dtos;
using OpenSource1.Application.Features.NotasCreditoVenta.Borradores.Dtos;
using OpenSource1.Application.Features.NotasCreditoVenta.Posteadas.Dtos;
using OpenSource1.Application.Features.NotasCreditoVenta.Posteo;
using OpenSource1.Application.Services.Contabilidad;
using OpenSource1.Core.Common;
using OpenSource1.Core.Entities.Contabilidad;
using OpenSource1.Core.Enums;
using OpenSource1.SmokeTests.TestInfrastructure;

namespace OpenSource1.SmokeTests.Api;

/// <summary>
/// Notas de crédito de venta por la API (Task 8.6), con su propia base y los procesos reales: Review Focus 3 (saldo del cliente tras
/// factura + nota de crédito parcial aplicada automáticamente + pago = derivado exacto; el estado de cuenta, los movimientos del
/// cliente, el balance y las existencias cuadran en cada fecha de corte, patrón de <see cref="CoherenciaVistasApiTests"/>) y el
/// recorrido de los endpoints (borrador desde la factura, líneas acreditables, totales, posteo, consultas y permisos).
/// REQUIERE DOCKER.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class NotasCreditoVentaApiTests(PostgresTestFixture fixture) : IClassFixture<PostgresTestFixture>, IAsyncLifetime
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
    public async Task ReviewFocus3_FacturaNotaParcialYPago_SaldoDerivadoExacto_YLasVistasCuadranEnCadaCorte()
    {
        var ingresos = await CrearCuentaAsync("4191");
        var producto = await ProductoClasificadoAsync();
        var almacen = await _prueba.SembrarAlmacenAsync();
        var socio = await CrearSocioAsync("Cliente nota de crédito");

        // Entrada 10 u a 5 (05/01). Factura 10/01: 2 u a 50 + 20 a la cuenta (ITBIS 18 %): 141.6.
        await _prueba.RegistrarOkAsync(LibroInventarioPrueba.Entrada(producto, almacen, 10m, 5m, new DateOnly(2022, 1, 5)));
        var (factura, movimientoFactura) = await PostearFacturaAsync(socio, new DateOnly(2022, 1, 10), (ingresos, 20m), (producto, 2m, 50m, almacen));

        // Nota parcial 20/01 por la API: 1 u con devolución (50 + 9 = 59), aplicada automáticamente a la factura.
        var creado = await _client.PostAsJsonAsync("/api/notas-credito-venta/borradores", new { facturaVentaNumero = factura, fechaRegistro = "2022-01-20", copiarLineas = false });
        Assert.True(creado.StatusCode == HttpStatusCode.Created, await creado.Content.ReadAsStringAsync());
        var borrador = (await creado.Content.ReadFromJsonAsync<NotaCreditoVentaBorradorResponse>())!;
        Assert.Equal((factura, 141.6m, 0), (borrador.FacturaVentaNumero, borrador.FacturaImporteTotal, borrador.NumeroLineas));
        var acreditables = await GetAsync<List<LineaFacturaAcreditableResponse>>($"/api/notas-credito-venta/borradores/{borrador.Id}/lineas-acreditables");
        Assert.Equal([(TipoLineaFactura.Producto, 2m, 2m), (TipoLineaFactura.CuentaContable, 1m, 1m)],
            acreditables.Select(a => (a.Tipo, a.CantidadFacturada, a.CantidadPendiente)));
        var linea = await _client.PostAsJsonAsync($"/api/notas-credito-venta/borradores/{borrador.Id}/lineas", new
        {
            lineaFacturaVentaId = acreditables.Single(a => a.Tipo == TipoLineaFactura.Producto).LineaFacturaVentaId, cantidad = 1m, devolverInventario = true,
        });
        Assert.True(linea.StatusCode == HttpStatusCode.Created, await linea.Content.ReadAsStringAsync());
        var totales = await GetAsync<TotalesFactura>($"/api/notas-credito-venta/borradores/{borrador.Id}/totales");
        Assert.Equal(59m, totales.ImporteTotal);
        var posteo = await _client.PostAsync($"/api/notas-credito-venta/borradores/{borrador.Id}/postear", null);
        Assert.True(posteo.StatusCode == HttpStatusCode.OK, await posteo.Content.ReadAsStringAsync());
        var nota = (await posteo.Content.ReadFromJsonAsync<ResultadoPosteoNotaCredito>())!;
        Assert.Equal((59m, 59m), (nota.ImporteTotal, nota.ImporteAplicado));
        Assert.NotNull(nota.RegistroContable);

        // Cobro 28/02 de 50 aplicado el 01/03; batch de costo (salida de la factura y entrada de la devolución).
        var cobro = await RegistrarPagoAsync(socio, 50m, new DateOnly(2022, 2, 28));
        await AplicarAsync(movimientoFactura, cobro, 50m, new DateOnly(2022, 3, 1));
        await PostearCostoAsync(producto);

        var fallos = new StringBuilder();
        foreach (var corte in new[] { "2022-01-10", "2022-01-19", "2022-01-20", "2022-01-31", "2022-02-28", "2022-03-01", "2022-12-31" })
        {
            await ComprobarCorteAsync(DateOnly.Parse(corte), socio, fallos);
        }

        Assert.True(fallos.Length == 0, fallos.ToString());

        // Saldo del cliente = 141.6 − 59 − 50 = 32.6: todo en la factura; la nota y el cobro, aplicados del todo.
        var movimientos = await GetAsync<PagedResult<MovimientoClienteResponse>>($"/api/clientes/{socio}/movimientos?tamanoPagina=50");
        Assert.Equal(
            [(TipoDocumentoCliente.Factura, 141.6m, 32.6m), (TipoDocumentoCliente.NotaCredito, -59m, 0m), (TipoDocumentoCliente.Pago, -50m, 0m)],
            movimientos.Items.OrderBy(m => m.TipoDocumento).Select(m => (m.TipoDocumento, m.ImporteOriginal, m.ImporteRestante)));
        var estado = await GetAsync<EstadoCuentaResponse>("/api/clientes/estado-cuenta?fechaCorte=2022-12-31");
        Assert.Equal(32.6m, estado.Pagina.Items.Single(f => f.SocioNegocioId == socio).Total);
        var existencias = await GetAsync<ExistenciasVistaResponse>("/api/inventario/existencias?fecha=2022-12-31");
        Assert.Equal((9m, 45m), (existencias.Pagina.Items.Single().Existencia, existencias.ValorTotal));

        // Consultas de la nota posteada.
        var listado = await GetAsync<PagedResult<NotaCreditoVentaResponse>>($"/api/notas-credito-venta?facturaVentaNumero={factura}");
        var cabecera = Assert.Single(listado.Items);
        Assert.Equal((nota.Numero, 59m, nota.RegistroContable), (cabecera.Numero, cabecera.ImporteTotal, cabecera.NumeroRegistroContable));
        var detalle = await GetAsync<NotaCreditoVentaDetalleResponse>($"/api/notas-credito-venta/{nota.Numero}");
        var lineaNota = Assert.Single(detalle.Lineas);
        Assert.True(lineaNota.DevolverInventario);
        Assert.NotNull(lineaNota.MovimientoProductoId);
        Assert.Equal(("ITBIS18", 50m, 9m), (detalle.LineasIva.Single().IdentificadorIva, detalle.LineasIva.Single().BaseImponible, detalle.LineasIva.Single().ImporteIva));
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync("/api/notas-credito-venta/NOEXISTE")).StatusCode);
        // Re-postear el borrador (ahora Posteada, de solo lectura): 409 nota_credito_borrador.posteada.conflicto.
        var reposteo = await _client.PostAsync($"/api/notas-credito-venta/borradores/{borrador.Id}/postear", null);
        Assert.Equal(HttpStatusCode.Conflict, reposteo.StatusCode);
        Assert.Contains("ya se posteó", await reposteo.Content.ReadAsStringAsync());
    }

    [Fact]
    public async Task ListadoDeBorradores_Estado3_SoloPosteados_YColumnasDeSerie()
    {
        var ingresos = await CrearCuentaAsync("4193");
        var socio = await CrearSocioAsync("Cliente listado de borradores");
        var (factura, _) = await PostearFacturaAsync(socio, new DateOnly(2023, 6, 10), (ingresos, 100m), null);
        var creado = await _client.PostAsJsonAsync("/api/notas-credito-venta/borradores", new
        {
            facturaVentaNumero = factura, fechaRegistro = "2023-06-12", copiarLineas = true,
        });
        Assert.True(creado.StatusCode == HttpStatusCode.Created, await creado.Content.ReadAsStringAsync());
        var borrador = (await creado.Content.ReadFromJsonAsync<NotaCreditoVentaBorradorResponse>())!;
        Assert.Equal((EstadoNotaCreditoBorrador.Abierta, "NC"), (borrador.Estado, borrador.SerieRegistroCodigo));
        var posteo = await _client.PostAsync($"/api/notas-credito-venta/borradores/{borrador.Id}/postear", null);
        Assert.True(posteo.StatusCode == HttpStatusCode.OK, await posteo.Content.ReadAsStringAsync());
        var nota = (await posteo.Content.ReadFromJsonAsync<ResultadoPosteoNotaCredito>())!;

        var abiertos = await GetAsync<PagedResult<NotaCreditoVentaBorradorResponse>>(
            $"/api/notas-credito-venta/borradores?facturaVentaNumero={factura}");
        var posteados = await GetAsync<PagedResult<NotaCreditoVentaBorradorResponse>>(
            $"/api/notas-credito-venta/borradores?facturaVentaNumero={factura}&estado=3");

        Assert.DoesNotContain(abiertos.Items, b => b.Id == borrador.Id);
        Assert.All(abiertos.Items, b => Assert.Equal(EstadoNotaCreditoBorrador.Abierta, b.Estado));
        var posteado = Assert.Single(posteados.Items, b => b.Id == borrador.Id);
        Assert.Equal((EstadoNotaCreditoBorrador.Posteada, nota.Numero, "NC", "NC-BORR"),
            (posteado.Estado, posteado.NotaCreditoVentaNumero, posteado.SerieRegistroCodigo, posteado.SerieBorradorCodigo));
        Assert.All(posteados.Items, b => Assert.Equal((EstadoNotaCreditoBorrador.Posteada, true), (b.Estado, b.NotaCreditoVentaNumero is not null)));

        // Un estado que no es 1 ni 3: 400 con el campo Estado.
        foreach (var invalido in new[] { 0, 2, 99, 65537 })
        {
            var respuesta = await _client.GetAsync($"/api/notas-credito-venta/borradores?facturaVentaNumero={factura}&estado={invalido}");
            var cuerpo = await respuesta.Content.ReadAsStringAsync();
            Assert.True(respuesta.StatusCode == HttpStatusCode.BadRequest, cuerpo);
            using var problema = System.Text.Json.JsonDocument.Parse(cuerpo);
            Assert.True(problema.RootElement.GetProperty("errors").TryGetProperty("Estado", out _), cuerpo);
        }

        // La nota posteada enlaza a su borrador.
        var cabecera = Assert.Single((await GetAsync<PagedResult<NotaCreditoVentaResponse>>(
            $"/api/notas-credito-venta?facturaVentaNumero={factura}")).Items);
        Assert.Equal(borrador.Id, cabecera.NotaCreditoVentaBorradorId);
    }

    [Fact]
    public async Task Endpoints_Errores400_404_409_YPermisos()
    {
        var ingresos = await CrearCuentaAsync("4192");
        var socio = await CrearSocioAsync("Cliente endpoints");
        var (factura, _) = await PostearFacturaAsync(socio, new DateOnly(2023, 5, 10), (ingresos, 100m), null);

        var invalida = await _client.PostAsJsonAsync("/api/notas-credito-venta/borradores", new { facturaVentaNumero = "NOEXISTE" });
        Assert.Equal(HttpStatusCode.BadRequest, invalida.StatusCode);
        Assert.Contains("La factura de venta posteada indicada no existe", await invalida.Content.ReadAsStringAsync());

        var creado = await _client.PostAsJsonAsync("/api/notas-credito-venta/borradores", new
        {
            facturaVentaNumero = factura, fechaRegistro = "2023-05-12", copiarLineas = true,
        });
        Assert.Equal(HttpStatusCode.Created, creado.StatusCode);
        var borrador = (await creado.Content.ReadFromJsonAsync<NotaCreditoVentaBorradorResponse>())!;
        Assert.Equal(1, borrador.NumeroLineas);
        var lineas = await GetAsync<List<LineaNotaCreditoVentaBorradorResponse>>($"/api/notas-credito-venta/borradores/{borrador.Id}/lineas");
        var linea = Assert.Single(lineas);

        // Cantidad que excede: 400 con el campo; Xmin viejo: 409.
        var excede = await _client.PutAsJsonAsync($"/api/notas-credito-venta/lineas-borrador/{linea.Id}", new { xmin = linea.Xmin, cantidad = 2m });
        Assert.Equal(HttpStatusCode.BadRequest, excede.StatusCode);
        Assert.Contains("excede lo pendiente de acreditar", await excede.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.OK,
            (await _client.PutAsJsonAsync($"/api/notas-credito-venta/lineas-borrador/{linea.Id}", new { xmin = linea.Xmin, cantidad = 0.5m })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,
            (await _client.PutAsJsonAsync($"/api/notas-credito-venta/lineas-borrador/{linea.Id}", new { xmin = linea.Xmin, cantidad = 0.4m })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict,
            (await _client.PutAsJsonAsync($"/api/notas-credito-venta/borradores/{borrador.Id}", new { xmin = borrador.Xmin + 1, descripcion = "x" })).StatusCode);
        var fecha = await _client.PutAsJsonAsync($"/api/notas-credito-venta/borradores/{borrador.Id}", new { xmin = borrador.Xmin, fechaRegistro = "2023-05-01" });
        Assert.Equal(HttpStatusCode.BadRequest, fecha.StatusCode);
        Assert.Contains("no puede ser anterior a la de su factura", await fecha.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.NotFound, (await _client.GetAsync($"/api/notas-credito-venta/borradores/{Guid.NewGuid()}")).StatusCode);

        // Permisos: el ejecutor consulta pero no postea; sin sesión, 401.
        using var ejecutor = _factory.CreateClient();
        ejecutor.DefaultRequestHeaders.Add("X-Test-User", "ejecutor");
        ejecutor.DefaultRequestHeaders.Add("X-Test-Roles", "Ejecutor");
        Assert.Equal(HttpStatusCode.OK, (await ejecutor.GetAsync($"/api/notas-credito-venta/borradores/{borrador.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ejecutor.PostAsync($"/api/notas-credito-venta/borradores/{borrador.Id}/postear", null)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await ejecutor.DeleteAsync($"/api/notas-credito-venta/borradores/{borrador.Id}")).StatusCode);
        using var anonimo = _factory.CreateClient();
        anonimo.DefaultRequestHeaders.Add("X-Test-Anonymous", "true");
        Assert.Equal(HttpStatusCode.Unauthorized, (await anonimo.GetAsync("/api/notas-credito-venta")).StatusCode);

        // Borrado: 204 y después 404.
        Assert.Equal(HttpStatusCode.NoContent, (await _client.DeleteAsync($"/api/notas-credito-venta/borradores/{borrador.Id}")).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await _client.DeleteAsync($"/api/notas-credito-venta/borradores/{borrador.Id}")).StatusCode);

        // Borrar un socio con notas de crédito (aquí en borrador) -> 409.
        var otro = await _client.PostAsJsonAsync("/api/notas-credito-venta/borradores", new { facturaVentaNumero = factura, fechaRegistro = "2023-05-12" });
        Assert.Equal(HttpStatusCode.Created, otro.StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await _client.DeleteAsync($"/api/socios-negocio/{socio}")).StatusCode);
    }

    private async Task ComprobarCorteAsync(DateOnly corte, Guid socio, StringBuilder fallos)
    {
        void Igual(decimal esperado, decimal real, string que)
        {
            if (esperado != real)
            {
                fallos.AppendLine($"{corte:yyyy-MM-dd} {que}: esperado {esperado}, real {real}");
            }
        }

        var estado = await GetAsync<EstadoCuentaResponse>($"/api/clientes/estado-cuenta?fechaCorte={corte:yyyy-MM-dd}&tamanoPagina=200");
        var t = estado.Totales;
        Igual(t.Total, t.Corriente + t.Dias1a30 + t.Dias31a60 + t.Dias61a90 + t.Mas90 + t.SinAplicar, "Σ tramos vs total");
        var movimientos = await GetAsync<PagedResult<MovimientoClienteResponse>>(
            $"/api/clientes/{socio}/movimientos?fechaCorte={corte:yyyy-MM-dd}&tamanoPagina=200");
        var fila = estado.Pagina.Items.SingleOrDefault(f => f.SocioNegocioId == socio);
        Igual(movimientos.Items.Sum(m => m.ImporteRestante), fila?.Total ?? 0m, "Σ restantes de los movimientos vs total del cliente");

        var balance = await GetAsync<BalanceComprobacionResponse>($"/api/contabilidad/balance-comprobacion?hasta={corte:yyyy-MM-dd}");
        Igual(t.Total, SaldoFinal(balance, CuentaContableIds.CxC), "total del estado de cuenta vs saldo de la CxC");
        var existencias = await GetAsync<ExistenciasVistaResponse>($"/api/inventario/existencias?fecha={corte:yyyy-MM-dd}&tamanoPagina=200");
        Igual(existencias.ValorTotal, SaldoFinal(balance, CuentaContableIds.Inventario), "valor de existencias vs saldo de la 1301");
    }

    private static decimal SaldoFinal(BalanceComprobacionResponse balance, Guid cuenta) =>
        balance.Filas.SingleOrDefault(f => f.CuentaContableId == cuenta)?.SaldoFinal ?? 0m;

    private async Task<T> GetAsync<T>(string url)
    {
        using var respuesta = await _client.GetAsync(url);
        Assert.True(respuesta.StatusCode == HttpStatusCode.OK, $"{url} → {(int)respuesta.StatusCode}: {await respuesta.Content.ReadAsStringAsync()}");
        return (await respuesta.Content.ReadFromJsonAsync<T>())!;
    }

    [Fact]
    public async Task AltaRapida_SinOpciones_CopiaTodoLoPendienteConDevolucion_YSeriesSoloElAdministrador()
    {
        var ingresos = await CrearCuentaAsync("4195");
        var producto = await ProductoClasificadoAsync();
        var almacen = await _prueba.SembrarAlmacenAsync();
        var socio = await CrearSocioAsync("Cliente nota rapida");
        await _prueba.RegistrarOkAsync(LibroInventarioPrueba.Entrada(producto, almacen, 10m, 5m, new DateOnly(2023, 7, 5)));
        var (factura, _) = await PostearFacturaAsync(socio, new DateOnly(2023, 7, 10), (ingresos, 20m), (producto, 2m, 50m, almacen));
        var (serieBorrador, _, _) = await SeriesPrueba.CrearAsync(fixture.AppConnectionString, TipoDocumentoSerie.BorradorNotaCreditoVenta);
        var (serieRegistro, _, _) = await SeriesPrueba.CrearAsync(fixture.AppConnectionString, TipoDocumentoSerie.NotaCreditoVenta);

        // Sin copiarLineas ni devolverInventario: nota total con devolución y las series configuradas.
        var creado = await _client.PostAsJsonAsync("/api/notas-credito-venta/borradores", new { facturaVentaNumero = factura, fechaRegistro = "2023-07-12" });
        Assert.True(creado.StatusCode == HttpStatusCode.Created, await creado.Content.ReadAsStringAsync());
        var borrador = (await creado.Content.ReadFromJsonAsync<NotaCreditoVentaBorradorResponse>())!;
        Assert.Equal((2, "NC-BORR", "NC"), (borrador.NumeroLineas, borrador.SerieBorradorCodigo, borrador.SerieRegistroCodigo));
        var lineas = await GetAsync<List<LineaNotaCreditoVentaBorradorResponse>>($"/api/notas-credito-venta/borradores/{borrador.Id}/lineas");
        Assert.Equal([(TipoLineaFactura.Producto, 2m, true), (TipoLineaFactura.CuentaContable, 1m, false)],
            lineas.OrderBy(l => l.Tipo).Select(l => (l.Tipo, l.Cantidad, l.DevolverInventario)));

        // Serie de borradores: 400 para todos. Serie de registro: 400 para el Ejecutor, 201 para el Administrador.
        var conSerieBorrador = await _client.PostAsJsonAsync("/api/notas-credito-venta/borradores",
            new { facturaVentaNumero = factura, copiarLineas = false, serieBorradorId = serieBorrador });
        Assert.Equal(HttpStatusCode.BadRequest, conSerieBorrador.StatusCode);
        Assert.Contains("SerieBorradorId", await conSerieBorrador.Content.ReadAsStringAsync());
        using var ejecutor = _factory.CreateClient();
        ejecutor.DefaultRequestHeaders.Add("X-Test-User", "ejecutor");
        ejecutor.DefaultRequestHeaders.Add("X-Test-Roles", "Ejecutor");
        var deEjecutor = await ejecutor.PostAsJsonAsync("/api/notas-credito-venta/borradores",
            new { facturaVentaNumero = factura, copiarLineas = false, serieRegistroId = serieRegistro });
        Assert.Equal(HttpStatusCode.BadRequest, deEjecutor.StatusCode);
        Assert.Contains("administrador", await deEjecutor.Content.ReadAsStringAsync());
        var deAdministrador = await _client.PostAsJsonAsync("/api/notas-credito-venta/borradores",
            new { facturaVentaNumero = factura, copiarLineas = false, serieRegistroId = serieRegistro });
        Assert.Equal(HttpStatusCode.Created, deAdministrador.StatusCode);
        Assert.Equal(serieRegistro, (await deAdministrador.Content.ReadFromJsonAsync<NotaCreditoVentaBorradorResponse>())!.SerieRegistroId);
    }

    private async Task<Guid> CrearCuentaAsync(string numero)
    {
        var response = await _client.PostAsJsonAsync("/api/cuentas-contables", new
        {
            numero, nombre = "Ingresos notas de crédito", tipoCuenta = TipoCuentaContable.Posteo,
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

    private async Task<Guid> ProductoClasificadoAsync()
    {
        var producto = await _prueba.SembrarProductoAsync();
        await using var conexion = _prueba.NuevaConexion();
        await conexion.ExecuteAsync(
            """UPDATE "Productos" SET "GrupoInventarioId" = @Gi, "GrupoProductoId" = @Gp, "GrupoIvaProductoId" = @Giva WHERE "Id" = @Id""",
            new { Gi = GrupoContableIds.InventarioGeneral, Gp = GrupoContableIds.ProductoBienes, Giva = GrupoContableIds.IvaProductoItbis18, Id = producto });
        return producto;
    }

    /// <summary>Factura posteada por la API con una línea de cuenta y, opcionalmente, una de producto. Devuelve su número y el Id de su movimiento de cliente.</summary>
    private async Task<(string Numero, long Movimiento)> PostearFacturaAsync(
        Guid socio, DateOnly fechaRegistro, (Guid Cuenta, decimal Importe) lineaCuenta, (Guid Producto, decimal Cantidad, decimal Precio, Guid Almacen)? lineaProducto)
    {
        var creado = await _client.PostAsJsonAsync("/api/facturas-venta/borradores", new
        {
            socioNegocioId = socio, fechaRegistro,
        });
        Assert.True(creado.StatusCode == HttpStatusCode.Created, await creado.Content.ReadAsStringAsync());
        var borrador = (await creado.Content.ReadFromJsonAsync<FacturaVentaBorradorResponse>())!;

        if (lineaProducto is { } p)
        {
            var producto = await _client.PostAsJsonAsync($"/api/facturas-venta/borradores/{borrador.Id}/lineas", new
            {
                tipo = TipoLineaFactura.Producto, productoId = p.Producto, almacenId = p.Almacen, cantidad = p.Cantidad, precioUnitario = p.Precio,
            });
            Assert.True(producto.StatusCode == HttpStatusCode.Created, await producto.Content.ReadAsStringAsync());
        }

        var cuenta = await _client.PostAsJsonAsync($"/api/facturas-venta/borradores/{borrador.Id}/lineas", new
        {
            tipo = TipoLineaFactura.CuentaContable, cuentaContableId = lineaCuenta.Cuenta, cantidad = 1m,
            precioUnitario = lineaCuenta.Importe, grupoIvaProductoId = GrupoContableIds.IvaProductoItbis18
        });
        Assert.True(cuenta.StatusCode == HttpStatusCode.Created, await cuenta.Content.ReadAsStringAsync());

        var posteo = await _client.PostAsync($"/api/facturas-venta/borradores/{borrador.Id}/postear", null);
        Assert.True(posteo.StatusCode == HttpStatusCode.OK, await posteo.Content.ReadAsStringAsync());
        var numero = (await posteo.Content.ReadFromJsonAsync<ResultadoPosteoFactura>())!.Numero;
        var facturas = await GetAsync<PagedResult<MovimientoClienteResponse>>($"/api/clientes/{socio}/movimientos?tipoDocumento=1");
        return (numero, facturas.Items.Single(m => m.NumeroDocumento == numero).Id);
    }

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

    private async Task PostearCostoAsync(Guid producto)
    {
        await using var scope = _prueba.Provider.CreateAsyncScope();
        var batch = await scope.ServiceProvider.GetRequiredService<IPosteoCostoInventario>().PostearAsync(producto);
        Assert.Empty(batch.Pendientes);
        Assert.True(batch.Asientos > 0, "El batch de costo no asentó nada.");
    }
}
