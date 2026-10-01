extern alias BlazorApp;
using System.ComponentModel.DataAnnotations;
using System.Net;
using System.Text;
using System.Text.Json;
using BlazorApp::OpenSource1.Blazor.Components;
using BlazorApp::OpenSource1.Blazor.Services;
using Microsoft.Extensions.Logging.Abstractions;

namespace OpenSource1.SmokeTests.Blazor;

/// <summary>
/// <see cref="NotaCreditoVentaApiClient"/> (Task 8.7) frente a respuestas HTTP simuladas: los cuerpos exactos del controlador (alta
/// desde la factura, líneas con cantidad/devolución/Xmin, cabecera con "null = conservar"), la traducción de errores (400 con el
/// número de línea y el de fechas no permitidas, 403 de un POST forzado sin permiso, 404 como null) y la validación de
/// <see cref="LineaNotaCreditoForm"/>.
/// </summary>
public sealed class NotaCreditoVentaApiClientTests
{
    [Fact]
    public async Task CreateBorrador_EnviaLaFacturaYLasOpciones()
    {
        var handler = new Grabador(HttpStatusCode.Created, BorradorJson);
        var client = Cliente(handler);

        var resultado = await client.CreateBorradorAsync(new NotaCreditoBorradorInput("00000007", new DateOnly(2026, 9, 25), null, "Devolución", true, true));

        Assert.True(resultado.Succeeded);
        Assert.Equal(HttpMethod.Post, handler.Metodo);
        Assert.EndsWith("api/notas-credito-venta/borradores", handler.Uri!.AbsolutePath);
        using var cuerpo = JsonDocument.Parse(handler.Cuerpo!);
        Assert.Equal("00000007", cuerpo.RootElement.GetProperty("facturaVentaNumero").GetString());
        Assert.Equal("2026-09-25", cuerpo.RootElement.GetProperty("fechaRegistro").GetString());
        Assert.Equal(JsonValueKind.Null, cuerpo.RootElement.GetProperty("fechaDocumento").ValueKind);
        Assert.True(cuerpo.RootElement.GetProperty("copiarLineas").GetBoolean());
        Assert.True(cuerpo.RootElement.GetProperty("devolverInventario").GetBoolean());
    }

    [Fact]
    public async Task ContarEnPreparacion_PideUnaPaginaDeUno_SinEstado_YDevuelveElTotal()
    {
        var handler = new Grabador(HttpStatusCode.OK, """{"items":[],"pagina":1,"tamanoPagina":1,"total":7}""");
        var client = Cliente(handler);

        var total = await client.ContarEnPreparacionAsync();

        Assert.Equal(7L, total);
        Assert.EndsWith("api/notas-credito-venta/borradores", handler.Uri!.AbsolutePath);
        Assert.Contains("tamanoPagina=1", handler.Uri.Query);
        Assert.DoesNotContain("estado=", handler.Uri.Query);
    }

    [Fact]
    public async Task Lineas_AltaYModificacion_EnvianCantidadDevolucionYXmin()
    {
        var alta = new Grabador(HttpStatusCode.Created, "{}");
        var borradorId = Guid.NewGuid();
        await Cliente(alta).CreateLineaAsync(borradorId, 42, 1.5m, devolverInventario: true);

        Assert.EndsWith($"api/notas-credito-venta/borradores/{borradorId}/lineas", alta.Uri!.AbsolutePath);
        using (var cuerpo = JsonDocument.Parse(alta.Cuerpo!))
        {
            Assert.Equal(42, cuerpo.RootElement.GetProperty("lineaFacturaVentaId").GetInt64());
            Assert.Equal(1.5m, cuerpo.RootElement.GetProperty("cantidad").GetDecimal());
            Assert.True(cuerpo.RootElement.GetProperty("devolverInventario").GetBoolean());
        }

        var modificacion = new Grabador(HttpStatusCode.OK, "{}");
        var lineaId = Guid.NewGuid();
        await Cliente(modificacion).UpdateLineaAsync(lineaId, 2m, devolverInventario: false, xmin: 91);

        Assert.Equal(HttpMethod.Put, modificacion.Metodo);
        Assert.EndsWith($"api/notas-credito-venta/lineas-borrador/{lineaId}", modificacion.Uri!.AbsolutePath);
        using (var cuerpo = JsonDocument.Parse(modificacion.Cuerpo!))
        {
            Assert.Equal(91, cuerpo.RootElement.GetProperty("xmin").GetInt64());
            Assert.Equal(2m, cuerpo.RootElement.GetProperty("cantidad").GetDecimal());
            Assert.False(cuerpo.RootElement.GetProperty("devolverInventario").GetBoolean());
        }
    }

    [Fact]
    public async Task UpdateBorrador_EnviaFechasDescripcionSerieDeRegistroYXmin()
    {
        var handler = new Grabador(HttpStatusCode.OK, BorradorJson);
        var id = Guid.NewGuid();
        var serieRegistro = Guid.NewGuid();

        await Cliente(handler).UpdateBorradorAsync(
            id, new NotaCreditoCabeceraInput(new DateOnly(2026, 9, 26), new DateOnly(2026, 9, 26), "", serieRegistro), xmin: 12);

        Assert.EndsWith($"api/notas-credito-venta/borradores/{id}", handler.Uri!.AbsolutePath);
        using var cuerpo = JsonDocument.Parse(handler.Cuerpo!);
        Assert.Equal(12, cuerpo.RootElement.GetProperty("xmin").GetInt64());
        Assert.Equal("2026-09-26", cuerpo.RootElement.GetProperty("fechaDocumento").GetString());
        Assert.Equal("", cuerpo.RootElement.GetProperty("descripcion").GetString());
        Assert.Equal(serieRegistro, cuerpo.RootElement.GetProperty("serieRegistroId").GetGuid());
    }

    [Fact]
    public async Task UpdateBorrador_SinSerieDeRegistro_EnviaNull_QueLaApiConserva()
    {
        var handler = new Grabador(HttpStatusCode.OK, BorradorJson);

        await Cliente(handler).UpdateBorradorAsync(Guid.NewGuid(), new NotaCreditoCabeceraInput(null, null, "x"), xmin: 1);

        using var cuerpo = JsonDocument.Parse(handler.Cuerpo!);
        Assert.Equal(JsonValueKind.Null, cuerpo.RootElement.GetProperty("serieRegistroId").ValueKind);
    }

    [Fact]
    public async Task Postear_400_DevuelveLosMensajesReales_Y403ElDePermisos()
    {
        const string cuerpo = """
            {"title":"Los datos enviados no son válidos.","status":400,
             "errors":{"FechaRegistro":["La fecha de registro 25/09/2026 no está permitida: el rango general permitido es desde el 26/09/2026."],
                       "Lineas[10000].Cantidad":["Línea 10000: La cantidad 5 excede lo pendiente de acreditar de la línea 10000 de la factura."]}}
            """;

        var fallo = await Cliente(new Grabador(HttpStatusCode.BadRequest, cuerpo)).PostearAsync(Guid.NewGuid());
        var prohibido = await Cliente(new Grabador(HttpStatusCode.Forbidden, "")).PostearAsync(Guid.NewGuid());

        Assert.False(fallo.Succeeded);
        Assert.Equal(2, fallo.Errors!.Count);
        Assert.Contains(fallo.Errors, e => e.Contains("no está permitida", StringComparison.Ordinal));
        Assert.Contains(fallo.Errors, e => e.StartsWith("Línea 10000:", StringComparison.Ordinal));
        Assert.False(prohibido.Succeeded);
        Assert.Equal("No tiene permisos para realizar esta operación.", prohibido.Message);
    }

    [Fact]
    public async Task Consultas_404_SonNulas()
    {
        var client = Cliente(new Grabador(HttpStatusCode.NotFound, ""));

        Assert.Null(await client.GetBorradorAsync(Guid.NewGuid()));
        Assert.Null(await client.GetTotalesAsync(Guid.NewGuid()));
        Assert.Null(await client.ListLineasAcreditablesAsync(Guid.NewGuid()));
        Assert.Null(await client.GetNotaAsync("00000001"));
    }

    [Theory]
    [InlineData(null, "La cantidad es obligatoria.")]
    [InlineData("abc", "La cantidad")]
    [InlineData("1,5", null)]
    [InlineData("2", null)]
    public void LineaNotaCreditoForm_ValidaLaCantidad(string? texto, string? error)
    {
        var form = new LineaNotaCreditoForm { CantidadTexto = texto };
        var resultados = new List<ValidationResult>();

        var valido = Validator.TryValidateObject(form, new ValidationContext(form), resultados, validateAllProperties: true);

        if (error is null)
        {
            Assert.True(valido);
            Assert.NotNull(form.Cantidad);
        }
        else
        {
            Assert.False(valido);
            Assert.StartsWith(error, Assert.Single(resultados).ErrorMessage, StringComparison.Ordinal);
            Assert.Null(form.Cantidad);
        }
    }

    private const string BorradorJson = """{"id":"6f1c0c3e-0000-0000-0000-000000000009","numero":"NC-BORR-000001","facturaVentaNumero":"00000007","xmin":13}""";

    private static NotaCreditoVentaApiClient Cliente(HttpMessageHandler handler) =>
        new(new HttpClient(handler) { BaseAddress = new Uri("http://api.test/") }, NullLogger<NotaCreditoVentaApiClient>.Instance);

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
