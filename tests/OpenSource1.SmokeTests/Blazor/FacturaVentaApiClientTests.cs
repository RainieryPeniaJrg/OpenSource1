extern alias BlazorApp;
using System.Net;
using System.Text;
using System.Text.Json;
using BlazorApp::OpenSource1.Blazor.Services;
using Microsoft.Extensions.Logging.Abstractions;
using OpenSource1.Core.Enums;

namespace OpenSource1.SmokeTests.Blazor;

/// <summary>
/// <see cref="FacturaVentaApiClient"/> y <see cref="CobroApiClient"/> (Task 6.6) frente a respuestas HTTP simuladas: el cuerpo que
/// envían (la cabecera SIEMPRE con ambos socios, porque la API usa "null = conservar") y la traducción de errores (todos los
/// mensajes de un 400 con su número de línea, el 403 real de un POST forzado sin permiso, el 409 de concurrencia).
/// </summary>
public sealed class FacturaVentaApiClientTests
{
    [Fact]
    public async Task UpdateBorrador_EnviaSiempreAmbosSocios_YElXmin()
    {
        var handler = new Grabador(HttpStatusCode.OK, BorradorJson);
        var client = new FacturaVentaApiClient(Http(handler), NullLogger<FacturaVentaApiClient>.Instance);
        var venderA = Guid.NewGuid();
        var facturarA = Guid.NewGuid();
        var input = new BorradorCabeceraInput(venderA, facturarA, new DateOnly(2026, 9, 26), new DateOnly(2026, 9, 26), null, null, "");

        var resultado = await client.UpdateBorradorAsync(Guid.NewGuid(), input, xmin: 77);

        Assert.True(resultado.Succeeded);
        Assert.Equal(HttpMethod.Put, handler.Metodo);
        using var cuerpo = JsonDocument.Parse(handler.Cuerpo!);
        Assert.Equal(venderA, cuerpo.RootElement.GetProperty("socioNegocioId").GetGuid());
        Assert.Equal(facturarA, cuerpo.RootElement.GetProperty("socioNegocioFacturarAId").GetGuid());
        Assert.Equal(77, cuerpo.RootElement.GetProperty("xmin").GetInt64());
        Assert.Equal("", cuerpo.RootElement.GetProperty("descripcion").GetString());
        Assert.Equal(JsonValueKind.Null, cuerpo.RootElement.GetProperty("fechaVencimiento").ValueKind);
    }

    [Fact]
    public async Task CreateBorrador_ConPrimeraLinea_EnviaLaLineaConElTipoComoEntero()
    {
        var handler = new Grabador(HttpStatusCode.Created, BorradorJson);
        var client = new FacturaVentaApiClient(Http(handler), NullLogger<FacturaVentaApiClient>.Instance);
        var socio = Guid.NewGuid();
        var producto = Guid.NewGuid();

        await client.CreateBorradorAsync(new BorradorCabeceraInput(
            socio, null, null, null, null, null, null,
            PrimeraLinea: new LineaFacturaInput(TipoLineaFactura.Producto, producto, null, null, null, null, 2m, 10m, null, null)));

        Assert.EndsWith("api/facturas-venta/borradores", handler.Uri!.AbsolutePath);
        using var cuerpo = JsonDocument.Parse(handler.Cuerpo!);
        var linea = cuerpo.RootElement.GetProperty("primeraLinea");
        Assert.Equal((1, producto, 2m), (linea.GetProperty("tipo").GetInt32(), linea.GetProperty("productoId").GetGuid(), linea.GetProperty("cantidad").GetDecimal()));
        Assert.Equal(JsonValueKind.Null, cuerpo.RootElement.GetProperty("almacenId").ValueKind);
    }

    [Fact]
    public async Task ContarEnPreparacion_PideUnaPaginaDeUno_SinEstado_YDevuelveElTotal()
    {
        var handler = new Grabador(HttpStatusCode.OK, """{"items":[],"pagina":1,"tamanoPagina":1,"total":7}""");
        var client = new FacturaVentaApiClient(Http(handler), NullLogger<FacturaVentaApiClient>.Instance);

        var total = await client.ContarEnPreparacionAsync();

        Assert.Equal(7L, total);
        Assert.EndsWith("api/facturas-venta/borradores", handler.Uri!.AbsolutePath);
        Assert.Contains("tamanoPagina=1", handler.Uri.Query);
        Assert.DoesNotContain("estado=", handler.Uri.Query);
    }

    [Fact]
    public async Task CreateLinea_EnviaElTipoComoEntero_ALaRutaDelBorrador()
    {
        var handler = new Grabador(HttpStatusCode.Created, "{}");
        var client = new FacturaVentaApiClient(Http(handler), NullLogger<FacturaVentaApiClient>.Instance);
        var borradorId = Guid.NewGuid();

        await client.CreateLineaAsync(borradorId, new LineaFacturaInput(TipoLineaFactura.Comentario, null, null, "Nota", null, null, null, null, null, null));

        Assert.EndsWith($"api/facturas-venta/borradores/{borradorId}/lineas", handler.Uri!.AbsolutePath);
        using var cuerpo = JsonDocument.Parse(handler.Cuerpo!);
        Assert.Equal(3, cuerpo.RootElement.GetProperty("tipo").GetInt32());
        Assert.Equal("Nota", cuerpo.RootElement.GetProperty("descripcion").GetString());
    }

    [Fact]
    public async Task Postear_400ConErroresDeVariasLineas_DevuelveTodosLosMensajesConSuNumeroDeLinea()
    {
        const string cuerpo = """
            {"title":"Los datos enviados no son válidos.","status":400,
             "errors":{"Lineas[1].Cantidad":["Línea 1: La existencia del producto P1 en el almacén GENERAL es insuficiente."],
                       "Lineas[3].GrupoIvaProductoId":["Línea 3: No hay setup de IVA para ITBIS18 × EXENTO."]}}
            """;
        var client = new FacturaVentaApiClient(Http(new Grabador(HttpStatusCode.BadRequest, cuerpo)), NullLogger<FacturaVentaApiClient>.Instance);

        var resultado = await client.PostearAsync(Guid.NewGuid());

        Assert.False(resultado.Succeeded);
        Assert.Null(resultado.Valor);
        Assert.Equal(2, resultado.Errors!.Count);
        Assert.Contains(resultado.Errors, e => e.StartsWith("Línea 1:", StringComparison.Ordinal));
        Assert.Contains(resultado.Errors, e => e.StartsWith("Línea 3:", StringComparison.Ordinal));
    }

    [Fact]
    public async Task Postear_403_DevuelveElMensajeDePermisos()
    {
        var client = new FacturaVentaApiClient(Http(new Grabador(HttpStatusCode.Forbidden, "")), NullLogger<FacturaVentaApiClient>.Instance);

        var resultado = await client.PostearAsync(Guid.NewGuid());

        Assert.False(resultado.Succeeded);
        Assert.Equal("No tiene permisos para realizar esta operación.", resultado.Message);
    }

    [Fact]
    public async Task Postear_409_DevuelveElMensajeRealDeLaApi()
    {
        const string cuerpo = """{"title":"El borrador fue modificado o posteado por otro usuario.","status":409}""";
        var client = new FacturaVentaApiClient(Http(new Grabador(HttpStatusCode.Conflict, cuerpo)), NullLogger<FacturaVentaApiClient>.Instance);

        var resultado = await client.PostearAsync(Guid.NewGuid());

        Assert.False(resultado.Succeeded);
        Assert.Equal("El borrador fue modificado o posteado por otro usuario.", resultado.Message);
    }

    [Fact]
    public async Task Postear_200_DevuelveNumeroTotalYRegistroContable()
    {
        const string cuerpo = """{"numero":"FV-000001","importeTotal":1180.00,"registroContable":"CONTAB-000004"}""";
        var client = new FacturaVentaApiClient(Http(new Grabador(HttpStatusCode.OK, cuerpo)), NullLogger<FacturaVentaApiClient>.Instance);

        var resultado = await client.PostearAsync(Guid.NewGuid());

        Assert.True(resultado.Succeeded);
        Assert.Equal("FV-000001", resultado.Valor!.Numero);
        Assert.Equal(1180.00m, resultado.Valor.ImporteTotal);
        Assert.Equal("CONTAB-000004", resultado.Valor.RegistroContable);
    }

    [Fact]
    public async Task Postear_200SinCuerpo_EsUnErrorConMensajeClaro()
    {
        var client = new FacturaVentaApiClient(Http(new Grabador(HttpStatusCode.OK, "null")), NullLogger<FacturaVentaApiClient>.Instance);

        var resultado = await client.PostearAsync(Guid.NewGuid());

        Assert.False(resultado.Succeeded);
        Assert.Null(resultado.Valor);
        Assert.Equal("La API no devolvió el resultado esperado (posteo de facturas).", resultado.Message);
    }

    [Fact]
    public async Task UpdateLinea_409_DevuelveElMensajeRealDeConcurrencia()
    {
        const string cuerpo = """{"title":"El registro fue modificado por otro usuario.","status":409}""";
        var handler = new Grabador(HttpStatusCode.Conflict, cuerpo);
        var client = new FacturaVentaApiClient(Http(handler), NullLogger<FacturaVentaApiClient>.Instance);
        var lineaId = Guid.NewGuid();

        var resultado = await client.UpdateLineaAsync(
            lineaId, new LineaFacturaInput(TipoLineaFactura.Comentario, null, null, "Nota", null, null, null, null, null, null), xmin: 5);

        Assert.False(resultado.Succeeded);
        Assert.Equal("El registro fue modificado por otro usuario.", resultado.Message);
        Assert.Empty(resultado.Errors!);
        Assert.Equal(HttpMethod.Put, handler.Metodo);
        Assert.EndsWith($"api/facturas-venta/lineas-borrador/{lineaId}", handler.Uri!.AbsolutePath);
        using var json = JsonDocument.Parse(handler.Cuerpo!);
        Assert.Equal(5, json.RootElement.GetProperty("xmin").GetInt64());
    }

    [Fact]
    public async Task GetBorrador_404_EsNulo()
    {
        var client = new FacturaVentaApiClient(
            Http(new Grabador(HttpStatusCode.NotFound, """{"status":404}""")), NullLogger<FacturaVentaApiClient>.Instance);

        Assert.Null(await client.GetBorradorAsync(Guid.NewGuid()));
    }

    [Fact]
    public async Task RegistrarPago_EnviaElCuerpo_YDevuelveElNumeroDeCobro()
    {
        var handler = new Grabador(HttpStatusCode.OK, """{"numero":"COBRO-000001","movimientoClienteId":12,"registroContable":"CONTAB-000009"}""");
        var client = new CobroApiClient(Http(handler), NullLogger<CobroApiClient>.Instance);
        var socio = Guid.NewGuid();
        var cuenta = Guid.NewGuid();

        var resultado = await client.RegistrarPagoAsync(new RegistrarPagoInput(socio, 500.25m, new DateOnly(2026, 9, 26), cuenta, "Abono"));

        Assert.True(resultado.Succeeded);
        Assert.Equal("COBRO-000001", resultado.Valor!.Numero);
        Assert.Equal(12, resultado.Valor.MovimientoClienteId);
        Assert.EndsWith("api/cobros", handler.Uri!.AbsolutePath);
        using var cuerpo = JsonDocument.Parse(handler.Cuerpo!);
        Assert.Equal(socio, cuerpo.RootElement.GetProperty("socioNegocioId").GetGuid());
        Assert.Equal(500.25m, cuerpo.RootElement.GetProperty("importe").GetDecimal());
        Assert.Equal(cuenta, cuerpo.RootElement.GetProperty("cuentaCajaId").GetGuid());
    }

    [Fact]
    public async Task AplicarPago_400_DevuelveLosMensajesReales()
    {
        const string cuerpo = """{"status":400,"errors":{"Importe":["El importe a aplicar supera lo pendiente de la factura (100.00)."]}}""";
        var handler = new Grabador(HttpStatusCode.BadRequest, cuerpo);
        var client = new CobroApiClient(Http(handler), NullLogger<CobroApiClient>.Instance);

        var resultado = await client.AplicarPagoAsync(new AplicarPagoInput(1, 2, 150m, null));

        Assert.False(resultado.Succeeded);
        Assert.EndsWith("api/cobros/aplicaciones", handler.Uri!.AbsolutePath);
        Assert.Equal(["El importe a aplicar supera lo pendiente de la factura (100.00)."], resultado.Errors);
    }

    [Fact]
    public async Task RegistrarPago_403_DevuelveElMensajeDePermisos()
    {
        var client = new CobroApiClient(Http(new Grabador(HttpStatusCode.Forbidden, "")), NullLogger<CobroApiClient>.Instance);

        var resultado = await client.RegistrarPagoAsync(new RegistrarPagoInput(Guid.NewGuid(), 1m, new DateOnly(2026, 9, 26), null, null));

        Assert.False(resultado.Succeeded);
        Assert.Equal("No tiene permisos para realizar esta operación.", resultado.Message);
    }

    [Fact]
    public async Task GetSaldo_404_EsNulo_Y200_LeeElSaldo()
    {
        var socio = Guid.NewGuid();
        var noEncontrado = new CobroApiClient(Http(new Grabador(HttpStatusCode.NotFound, "")), NullLogger<CobroApiClient>.Instance);
        var encontrado = new CobroApiClient(
            Http(new Grabador(HttpStatusCode.OK, $$"""{"socioNegocioId":"{{socio}}","saldo":680.00,"movimientosAbiertos":2}""")),
            NullLogger<CobroApiClient>.Instance);

        Assert.Null(await noEncontrado.GetSaldoAsync(socio));
        var saldo = await encontrado.GetSaldoAsync(socio);
        Assert.Equal(680.00m, saldo!.Saldo);
        Assert.Equal(2, saldo.MovimientosAbiertos);
    }

    private const string BorradorJson = """{"id":"6f1c0c3e-0000-0000-0000-000000000001","numero":"FV-BORR-000001","estado":1,"xmin":78}""";

    private static HttpClient Http(HttpMessageHandler handler) => new(handler) { BaseAddress = new Uri("http://api.test/") };

    private sealed class Grabador(HttpStatusCode estado, string cuerpo) : HttpMessageHandler
    {
        public HttpMethod? Metodo { get; private set; }
        public Uri? Uri { get; private set; }
        public string? Cuerpo { get; private set; }

        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Metodo = request.Method;
            Uri = request.RequestUri;
            Cuerpo = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            return new HttpResponseMessage(estado) { Content = new StringContent(cuerpo, Encoding.UTF8, "application/json") };
        }
    }
}
