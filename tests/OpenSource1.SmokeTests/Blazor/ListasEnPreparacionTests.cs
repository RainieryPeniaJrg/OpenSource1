extern alias BlazorApp;
using System.Net;
using BlazorApp::OpenSource1.Blazor.Services;
using Moq;
using OpenSource1.Application.Features.FacturasVenta.Borradores.Dtos;
using OpenSource1.Application.Features.FacturasVenta.Posteadas;
using OpenSource1.Application.Features.FacturasVenta.Posteadas.Dtos;
using OpenSource1.Application.Features.NotasCreditoVenta.Borradores.Dtos;
using OpenSource1.Application.Features.NotasCreditoVenta.Posteadas;
using OpenSource1.Application.Features.NotasCreditoVenta.Posteadas.Dtos;
using OpenSource1.Core.Common;
using OpenSource1.Core.Enums;
using OpenSource1.SmokeTests.TestInfrastructure;

namespace OpenSource1.SmokeTests.Blazor;

/// <summary>
/// Spec alta rápida (Parte 2, navegación): listas de facturas y notas con pestañas "Registradas" / "En preparación (n)"
/// (?estado=preparacion), la lista en preparación (cliente, creado el, líneas, total, Continuar y Descartar) y las rutas antiguas de
/// las listas de borradores, que redirigen.
/// </summary>
public sealed class ListasEnPreparacionTests
{
    private static readonly Guid IdAbierta = Guid.Parse("7d0c9a51-0000-0000-0000-0000000008a1");
    private static readonly Guid IdLiberada = Guid.Parse("7d0c9a51-0000-0000-0000-0000000008a2");
    private static readonly Guid IdNota = Guid.Parse("7d0c9a51-0000-0000-0000-0000000008a3");

    [Fact]
    public async Task Facturas_PestanasConConteo_YNuevaFactura()
    {
        using var app = Configurar(new BlazorSsrFactory());

        var registradas = HtmlSsr.Decodificar(await HtmlSsr.HtmlAsync(app.Cliente(), "/facturas-venta"));
        var preparacion = HtmlSsr.Decodificar(await HtmlSsr.HtmlAsync(app.Cliente(), "/facturas-venta?estado=preparacion"));

        Assert.Contains("data-testid=\"pestanas-documento\"", registradas);
        Assert.Contains("aria-current=\"page\" class=\"inline-block border-b-2 border-brand-600 px-1 pb-2 text-brand-700\" data-testid=\"pestana-registradas\">Registradas</a>", registradas);
        Assert.Contains(">En preparación (2)</a>", registradas);
        Assert.Contains("data-testid=\"tabla-facturas\"", registradas);
        Assert.Contains(">+ Nueva factura</a>", registradas);
        Assert.DoesNotContain("href=\"/facturas-venta/borradores\"", registradas);
        Assert.Contains("data-testid=\"pestana-preparacion\">En preparación (2)</a>", preparacion);
        Assert.Contains("data-testid=\"tabla-en-preparacion\"", preparacion);
        Assert.DoesNotContain("data-testid=\"tabla-facturas\"", preparacion);
    }

    [Fact]
    public async Task Facturas_EnPreparacion_ClienteCreadoLineasTotal_ContinuarYDescartar()
    {
        using var app = Configurar(new BlazorSsrFactory());

        var html = HtmlSsr.Decodificar(await HtmlSsr.HtmlAsync(app.Cliente(), "/facturas-venta?estado=preparacion"));

        Assert.Contains(">Cliente</th>", html);
        Assert.Contains(">Creado el</th>", html);
        Assert.Contains(">Líneas</th>", html);
        Assert.Contains(">Total</th>", html);
        Assert.Contains(">1,234.50</td>", html);
        Assert.Contains($"href=\"/facturas-venta/en-preparacion/{IdAbierta}\"", html);
        Assert.Contains(">Continuar</a>", html);
        Assert.Contains($"href=\"/facturas-venta?estado=preparacion&deleteId={IdAbierta}\"", html);
        Assert.DoesNotContain($"deleteId={IdLiberada}", html);
        Assert.Contains(">En preparación</span>", html);
        Assert.Contains(">En preparación (liberada)</span>", html);
    }

    [Fact]
    public async Task Facturas_EnPreparacion_Descartar_ConConfirmacion()
    {
        using var app = Configurar(new BlazorSsrFactory());
        app.Simular<IFacturaVentaApiClient>()
            .Setup(c => c.DeleteBorradorAsync(IdAbierta, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VentaOperationResult<bool>(true, "ok", true));
        var url = $"/facturas-venta?estado=preparacion&deleteId={IdAbierta}";

        var html = HtmlSsr.Decodificar(await HtmlSsr.HtmlAsync(app.Cliente(), url));
        var respuesta = await FormulariosSsr.EnviarAsync(app.Cliente(), url, "delete-borrador", new Dictionary<string, string> { ["DeleteInput.Id"] = IdAbierta.ToString() });

        Assert.Contains(">Descartar factura</h2>", html);
        Assert.Equal("/facturas-venta?estado=preparacion&ok=descartada", FormulariosSsr.Destino(respuesta));
    }

    [Fact]
    public async Task Facturas_ConteoQueNoCarga_PestanaSinNumero()
    {
        using var app = Configurar(new BlazorSsrFactory());
        app.Simular<IFacturaVentaApiClient>()
            .Setup(c => c.ContarEnPreparacionAsync(It.IsAny<CancellationToken>())).ThrowsAsync(new HttpRequestException("API caída"));

        var html = HtmlSsr.Decodificar(await HtmlSsr.HtmlAsync(app.Cliente(), "/facturas-venta"));

        Assert.Contains("data-testid=\"pestana-preparacion\">En preparación</a>", html);
    }

    [Fact]
    public async Task Notas_PestanasConConteo_NuevaNota_YLista()
    {
        using var app = Configurar(new BlazorSsrFactory());

        var registradas = HtmlSsr.Decodificar(await HtmlSsr.HtmlAsync(app.Cliente(), "/notas-credito-venta"));
        var preparacion = HtmlSsr.Decodificar(await HtmlSsr.HtmlAsync(app.Cliente(), "/notas-credito-venta?estado=preparacion"));

        Assert.Contains(">En preparación (1)</a>", registradas);
        Assert.Contains(">+ Nueva nota de crédito</a>", registradas);
        Assert.DoesNotContain("href=\"/notas-credito-venta/borradores\"", registradas);
        Assert.Contains("data-testid=\"tabla-en-preparacion\"", preparacion);
        Assert.Contains($"href=\"/notas-credito-venta/en-preparacion/{IdNota}\"", preparacion);
        Assert.Contains(">59.00</td>", preparacion);
    }

    /// <summary>Los regresos de la página del documento ("Guardar para después" / "Descartar", R6/R7) y del descarte en la lista.</summary>
    [Theory]
    [InlineData("/facturas-venta?estado=preparacion&ok=guardada", "La factura quedó guardada en preparación.")]
    [InlineData("/facturas-venta?estado=preparacion&ok=descartada", "Factura descartada.")]
    [InlineData("/notas-credito-venta?estado=preparacion&ok=guardada", "La nota de crédito quedó guardada en preparación.")]
    [InlineData("/notas-credito-venta?estado=preparacion&ok=descartada", "Nota de crédito descartada.")]
    public async Task EnPreparacion_MensajesDeRegreso(string url, string mensaje)
    {
        using var app = Configurar(new BlazorSsrFactory());

        var html = HtmlSsr.Decodificar(await HtmlSsr.HtmlAsync(app.Cliente(), url));

        Assert.Contains(mensaje, html);
    }

    /// <summary>PF6: el formulario de descarte está también en la pestaña "Registradas"; un POST allí recibe el mensaje real de la API.</summary>
    [Fact]
    public async Task Registradas_FormularioDeDescarteEnElArbol_YMensajeDeLaApi()
    {
        using var app = Configurar(new BlazorSsrFactory());
        app.Simular<IFacturaVentaApiClient>()
            .Setup(c => c.DeleteBorradorAsync(IdLiberada, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new VentaOperationResult<bool>(false, "La factura está liberada: reábrala antes de descartarla."));

        var facturas = await HtmlSsr.HtmlAsync(app.Cliente(), "/facturas-venta");
        var notas = await HtmlSsr.HtmlAsync(app.Cliente(), "/notas-credito-venta");
        var respuesta = await FormulariosSsr.EnviarAsync(app.Cliente(), "/facturas-venta", "delete-borrador",
            new Dictionary<string, string> { ["DeleteInput.Id"] = IdLiberada.ToString() });
        var html = HtmlSsr.Decodificar(await respuesta.Content.ReadAsStringAsync());

        Assert.Contains("value=\"delete-borrador\"", facturas);
        Assert.Contains("value=\"delete-borrador-nota\"", notas);
        Assert.Contains("La factura está liberada: reábrala antes de descartarla.", html);
    }

    /// <summary>El total de la nota en preparación no lleva los topes de lo que queda por acreditar: se rotula como estimado.</summary>
    [Fact]
    public async Task Notas_EnPreparacion_TotalEstimado_FacturaDeOrigen_YDescartar()
    {
        using var app = Configurar(new BlazorSsrFactory());

        var html = HtmlSsr.Decodificar(await HtmlSsr.HtmlAsync(app.Cliente(), "/notas-credito-venta?estado=preparacion"));
        var dialogo = HtmlSsr.Decodificar(await HtmlSsr.HtmlAsync(app.Cliente(), $"/notas-credito-venta?estado=preparacion&deleteId={IdNota}"));

        Assert.Contains(">Total estimado</th>", html);
        Assert.Contains("data-testid=\"aviso-total-estimado\"", html);
        Assert.Contains(">Factura de origen</th>", html);
        Assert.Contains("href=\"/facturas-venta/FV-000123\"", html);
        Assert.Contains($"href=\"/notas-credito-venta?estado=preparacion&deleteId={IdNota}\"", html);
        Assert.Contains(">Descartar nota de crédito</h2>", dialogo);
        Assert.DoesNotContain("data-testid=\"tabla-notas\"", html);
    }

    [Theory]
    [InlineData("/facturas-venta/borradores", "/facturas-venta?estado=preparacion")]
    [InlineData("/facturas-venta/borradores?numero=0001&nombre=Com", "/facturas-venta?estado=preparacion&numero=0001&nombre=Com")]
    [InlineData("/facturas-venta/borradores?estado=3", "/facturas-venta")]
    [InlineData("/facturas-venta/borradores?editId=7d0c9a51-0000-0000-0000-0000000008a1", "/facturas-venta/en-preparacion/7d0c9a51-0000-0000-0000-0000000008a1")]
    [InlineData("/notas-credito-venta/borradores", "/notas-credito-venta?estado=preparacion")]
    [InlineData("/notas-credito-venta/borradores?editId=7d0c9a51-0000-0000-0000-0000000008a3", "/notas-credito-venta/en-preparacion/7d0c9a51-0000-0000-0000-0000000008a3")]
    public async Task ListasAntiguas_Redirigen(string origen, string destino)
    {
        using var app = Configurar(new BlazorSsrFactory());

        var respuesta = await app.Cliente().GetAsync(origen);

        Assert.Equal(HttpStatusCode.Redirect, respuesta.StatusCode);
        Assert.Equal(destino, FormulariosSsr.Destino(respuesta));
    }

    private static BlazorSsrFactory Configurar(BlazorSsrFactory app)
    {
        var facturas = app.Simular<IFacturaVentaApiClient>();
        facturas.Setup(c => c.ContarEnPreparacionAsync(It.IsAny<CancellationToken>())).ReturnsAsync(2L);
        facturas.Setup(c => c.ListFacturasAsync(It.IsAny<FacturaVentaSearchCriteria?>(), It.IsAny<PageRequest?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<FacturaVentaResponse>([], 1, 50, 0));
        facturas.Setup(c => c.ListBorradoresAsync(It.IsAny<FacturaVentaBorradorFiltro?>(), It.IsAny<PageRequest?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<FacturaVentaBorradorResponse>(
            [
                new FacturaVentaBorradorResponse
                {
                    Id = IdAbierta, Numero = "00000041", Estado = EstadoFacturaBorrador.Abierta, SocioNegocioCodigo = "C-001", NombreFacturacion = "Comercial Uno",
                    NumeroLineas = 2, ImporteTotal = 1234.50m, CreatedAtUtc = new DateTime(2026, 10, 1, 12, 0, 0, DateTimeKind.Utc),
                },
                new FacturaVentaBorradorResponse
                {
                    Id = IdLiberada, Numero = "00000040", Estado = EstadoFacturaBorrador.Liberada, SocioNegocioCodigo = "C-002", NombreFacturacion = "Comercial Dos",
                    NumeroLineas = 1, ImporteTotal = 10m, CreatedAtUtc = new DateTime(2026, 9, 30, 12, 0, 0, DateTimeKind.Utc),
                },
            ], 1, 50, 2));
        facturas.Setup(c => c.GetBorradorAsync(IdAbierta, It.IsAny<CancellationToken>()))
            .ReturnsAsync(new FacturaVentaBorradorResponse { Id = IdAbierta, NombreFacturacion = "Comercial Uno", NumeroLineas = 2 });

        var notas = app.Simular<INotaCreditoVentaApiClient>();
        notas.Setup(c => c.ContarEnPreparacionAsync(It.IsAny<CancellationToken>())).ReturnsAsync(1L);
        notas.Setup(c => c.ListNotasAsync(It.IsAny<NotaCreditoVentaSearchCriteria?>(), It.IsAny<PageRequest?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<NotaCreditoVentaResponse>([], 1, 50, 0));
        notas.Setup(c => c.ListBorradoresAsync(It.IsAny<NotaCreditoVentaBorradorFiltro?>(), It.IsAny<PageRequest?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<NotaCreditoVentaBorradorResponse>(
            [
                new NotaCreditoVentaBorradorResponse
                {
                    Id = IdNota, Numero = "00000003", FacturaVentaNumero = "FV-000123", Estado = EstadoNotaCreditoBorrador.Abierta, SocioNegocioCodigo = "C-001",
                    NombreFacturacion = "Comercial Uno", NumeroLineas = 1, ImporteTotal = 59m, CreatedAtUtc = new DateTime(2026, 10, 2, 12, 0, 0, DateTimeKind.Utc),
                },
            ], 1, 50, 1));
        return app;
    }
}
