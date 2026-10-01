extern alias BlazorApp;

using System.Net;
using BlazorApp::OpenSource1.Blazor.Navigation;
using BlazorApp::OpenSource1.Blazor.Services;
using Microsoft.Extensions.DependencyInjection;
using Moq;
using OpenSource1.Application.Features.FacturasVenta.Borradores.Dtos;
using OpenSource1.Application.Features.FacturasVenta.Posteadas;
using OpenSource1.Application.Features.FacturasVenta.Posteadas.Dtos;
using OpenSource1.Application.Features.NotasCreditoVenta.Borradores.Dtos;
using OpenSource1.Application.Features.Productos.Dtos;
using OpenSource1.Application.Features.SociosNegocio.Dtos;
using OpenSource1.Core.Common;
using OpenSource1.SmokeTests.TestInfrastructure;
using static OpenSource1.SmokeTests.TestInfrastructure.HtmlSsr;

namespace OpenSource1.SmokeTests.Blazor;

/// <summary>Fix-Features A5/A6: inicio compacto sin hero azul y páginas de grupo con indicadores tolerantes a fallos.</summary>
public sealed class InicioYGruposTests
{
    private static readonly System.Text.RegularExpressions.Regex AnimacionEntrada =
        new("animate-(fade-in|slide-up|slide-up-slow|slide-in-left|pop|pulse-glow|float)\\b");

    [Fact]
    public async Task Inicio_SinHeroNiAnimaciones_ConKpisYTarjetasDeGrupo()
    {
        using var app = new BlazorSsrFactory();
        var (socios, productos) = SimularTotales(app, clientes: 12, productos: 30, sinExistencia: 4);

        var html = await HtmlAsync(app.Cliente("Administrador"), "/");

        Assert.DoesNotContain("bg-gradient-to-br from-brand-700", html);
        Assert.DoesNotMatch("class=\"[^\"]*animate-(fade-in|slide-up|slide-in-left|pop|pulse-glow|float)", html.Split("<main")[1]);
        Assert.Contains("data-testid=\"kpis-inicio\"", html);
        Assert.Contains("href=\"/modulos/facturacion\"", html);
        Assert.Contains("href=\"/modulos/administracion\"", html);
    }

    [Fact]
    public async Task Inicio_Kpis_UnaPaginaDeUnElementoPorIndicador_NuncaListasCompletas()
    {
        using var app = new BlazorSsrFactory();
        var (socios, productos) = SimularTotales(app, clientes: 12, productos: 30, sinExistencia: 4);

        var html = await HtmlAsync(app.Cliente("Administrador"), "/");
        var kpis = html[html.IndexOf("data-testid=\"kpis-inicio\"", StringComparison.Ordinal)..html.IndexOf("data-testid=\"grupos-inicio\"", StringComparison.Ordinal)];

        Assert.Matches("href=\"/clientes\"[^>]*>\\s*<p[^>]*>Clientes</p>\\s*<p[^>]*>12</p>", kpis);
        Assert.Matches("href=\"/productos\"[^>]*>\\s*<p[^>]*>Productos</p>\\s*<p[^>]*>30</p>", kpis);
        Assert.Matches("href=\"/productos\\?estado=without\"[^>]*>\\s*<p[^>]*>Sin existencia</p>\\s*<p[^>]*>4</p>", kpis);
        Assert.DoesNotContain("Existencia baja", html);

        socios.Verify(c => c.ListAsync(It.IsAny<SocioNegocioSearchFilter?>(), It.Is<PageRequest?>(p => p != null && p.Pagina == 1 && p.TamanoPagina == 1), It.IsAny<CancellationToken>()), Times.Once);
        productos.Verify(c => c.ListAsync(It.Is<ProductoSearchFilter?>(f => f == null || f.StockState == null), It.Is<PageRequest?>(p => p != null && p.Pagina == 1 && p.TamanoPagina == 1), It.IsAny<CancellationToken>()), Times.Once);
        productos.Verify(c => c.ListAsync(It.Is<ProductoSearchFilter?>(f => f != null && f.StockState == "without"), It.Is<PageRequest?>(p => p != null && p.Pagina == 1 && p.TamanoPagina == 1), It.IsAny<CancellationToken>()), Times.Once);
        socios.Verify(c => c.ListAllAsync(It.IsAny<SocioNegocioSearchFilter?>(), It.IsAny<CancellationToken>()), Times.Never);
        productos.Verify(c => c.ListAllAsync(It.IsAny<ProductoSearchFilter?>(), It.IsAny<CancellationToken>()), Times.Never);
        Assert.Single(socios.Invocations);
        Assert.Equal(2, productos.Invocations.Count);
    }

    [Theory]
    [InlineData("without", "without")]
    [InlineData("with", "with")]
    [InlineData("raro", null)]
    public async Task Productos_ParametroEstado_FiltraPorExistencia_YSeConservaEnLosEnlaces(string estado, string? esperado)
    {
        using var app = new BlazorSsrFactory();
        var (_, productos) = SimularTotales(app, clientes: 0, productos: 0, sinExistencia: 0);

        var html = await HtmlAsync(app.Cliente("Administrador"), $"/productos?estado={estado}");

        productos.Verify(c => c.ListAsync(It.Is<ProductoSearchFilter?>(f => f != null && f.StockState == esperado), It.IsAny<PageRequest?>(), It.IsAny<CancellationToken>()), Times.Once);
        if (esperado is null)
        {
            Assert.DoesNotContain("data-testid=\"filtro-existencia\"", html);
        }
        else
        {
            Assert.Contains("data-testid=\"filtro-existencia\"", html);
            // Los enlaces de la lista (vista, filtros, paginación) conservan el filtro.
            Assert.Matches($"href=\"/productos\\?[^\"]*estado={esperado}", html);
        }
    }

    [Fact]
    public async Task Productos_SinParametroPagina_PideLaPrimeraPagina()
    {
        // Runtime de la puerta A: /productos?estado=without mostraba "página 0 de 1" (el KPI enlaza sin ?pagina=).
        using var app = new BlazorSsrFactory();
        var (_, productos) = SimularTotales(app, clientes: 0, productos: 0, sinExistencia: 0);

        var html = await HtmlAsync(app.Cliente("Administrador"), "/productos?estado=without");

        productos.Verify(c => c.ListAsync(It.IsAny<ProductoSearchFilter?>(), It.Is<PageRequest?>(p => p != null && p.Pagina == 1), It.IsAny<CancellationToken>()), Times.Once);
        Assert.Contains("página 1 de 1", html);
    }

    private static (Mock<ISocioNegocioApiClient> Socios, Mock<IProductoApiClient> Productos) SimularTotales(
        BlazorSsrFactory app, long clientes, long productos, long sinExistencia)
    {
        var socios = app.Simular<ISocioNegocioApiClient>();
        socios.Setup(c => c.ListAsync(It.IsAny<SocioNegocioSearchFilter?>(), It.IsAny<PageRequest?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<SocioNegocioResponse>([], 1, 1, clientes));
        var mockProductos = app.Simular<IProductoApiClient>();
        mockProductos.Setup(c => c.ListAsync(It.IsAny<ProductoSearchFilter?>(), It.IsAny<PageRequest?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<ProductoResponse>([], 1, 1, productos));
        mockProductos.Setup(c => c.ListAsync(It.Is<ProductoSearchFilter?>(f => f != null && f.StockState == "without"), It.IsAny<PageRequest?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<ProductoResponse>([], 1, 1, sinExistencia));
        return (socios, mockProductos);
    }

    [Fact]
    public void HomeRazor_SinHeroNiClasesDeAnimacion()
    {
        // SinFlashTests excluye Home.razor (A6): la cobertura del fuente del inicio vive aquí.
        var fuente = File.ReadAllText(Path.Combine(BlazorSsrFactory.RaizRepositorio(), "src", "OpenSource1.Blazor", "Components", "Pages", "Home.razor"));

        Assert.DoesNotContain("bg-gradient", fuente);
        Assert.DoesNotMatch(AnimacionEntrada, fuente);
        Assert.DoesNotContain("animate-", fuente);
    }

    [Fact]
    public async Task Inicio_Ejecutor_SoloGruposConModulosVisibles()
    {
        using var app = new BlazorSsrFactory();
        SimularTotales(app, clientes: 0, productos: 0, sinExistencia: 0);

        var html = await HtmlAsync(app.Cliente("Ejecutor"), "/");

        Assert.Contains("href=\"/modulos/clientes\"", html);
        Assert.DoesNotContain("href=\"/modulos/administracion\"", html);
    }

    [Theory]
    [InlineData("Administrador", "CanModify")]
    [InlineData("Ejecutor", "")]
    public async Task Inicio_SinCanConsult_AvisoRestringido_SinKpisNiLlamadasALaApi(string rol, string permisos)
    {
        using var app = new BlazorSsrFactory();
        var socios = app.Simular<ISocioNegocioApiClient>();
        var productos = app.Simular<IProductoApiClient>();

        var html = await HtmlAsync(app.Cliente(rol, permisos: permisos), "/");

        Assert.DoesNotContain("data-testid=\"kpis-inicio\"", html);
        Assert.Contains("Acceso operativo restringido", html);
        if (permisos.Length == 0)
        {
            // Ejecutor sin ningún permiso no ve módulos: ni tarjetas ni un encabezado "Módulos" vacío. (Administrador
            // conserva los módulos restringidos solo por rol, p. ej. Administración.)
            Assert.DoesNotContain("data-testid=\"grupos-inicio\"", html);
            Assert.DoesNotContain(">Módulos</h2>", html);
        }
        Assert.Empty(socios.Invocations);
        Assert.Empty(productos.Invocations);
    }

    [Fact]
    public async Task Grupo_MuestraSusModulos_AbreSuGrupoEnElMenu_YGrupoInexistente404()
    {
        using var app = new BlazorSsrFactory();

        var html = await HtmlAsync(app.Cliente("Administrador"), "/modulos/configuracion");
        var inexistente = await app.Cliente("Administrador").GetAsync("/modulos/noexiste");

        Assert.Contains("href=\"/terminos-pago\"", html);
        Assert.Contains("href=\"/admin/fechas-registro\"", html);
        Assert.Matches("<details data-grupo=\"configuracion\" open", html);
        Assert.DoesNotMatch("<details data-grupo=\"clientes\" open", html);
        Assert.Equal(HttpStatusCode.NotFound, inexistente.StatusCode);
    }

    [Fact]
    public async Task Grupo_IndicadorQueFalla_SeOmite()
    {
        using var app = new BlazorSsrFactory();
        var facturas = app.Simular<IFacturaVentaApiClient>();
        facturas.Setup(c => c.ListBorradoresAsync(It.IsAny<FacturaVentaBorradorFiltro?>(), It.IsAny<PageRequest?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<FacturaVentaBorradorResponse>([], 1, 1, 7));
        facturas.Setup(c => c.ListFacturasAsync(It.IsAny<FacturaVentaSearchCriteria?>(), It.IsAny<PageRequest?>(), It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("API caída"));
        app.Simular<INotaCreditoVentaApiClient>()
            .Setup(c => c.ListBorradoresAsync(It.IsAny<NotaCreditoVentaBorradorFiltro?>(), It.IsAny<PageRequest?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<NotaCreditoVentaBorradorResponse>([], 1, 1, 2));

        var html = await HtmlAsync(app.Cliente("Administrador"), "/modulos/facturacion");

        Assert.Contains("data-testid=\"indicadores-grupo\"", html);
        Assert.Contains("Facturas en preparación", html);
        Assert.Contains(">7<", html);
        Assert.Contains(">2<", html);
        Assert.Contains("Notas de crédito en preparación", html);
        Assert.DoesNotContain("Facturas registradas", html);
        Assert.Contains("href=\"/facturas-venta\"", html);
        Assert.Contains("href=\"/facturas-venta?estado=preparacion\"", html);
        // En preparación = Abierta + Liberada: sin filtro de estado.
        facturas.Verify(c => c.ListBorradoresAsync(It.Is<FacturaVentaBorradorFiltro?>(f => f == null), It.IsAny<PageRequest?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task Indicadores_ElQueSuperaElTiempoMaximo_SeOmite_YRecibeUnTokenCancelable()
    {
        using var app = new BlazorSsrFactory();
        var facturas = app.Simular<IFacturaVentaApiClient>();
        var tokens = new List<CancellationToken>();
        // Un indicador que nunca termina (ni siquiera atiende el token) no debe colgar la página.
        facturas.Setup(c => c.ListBorradoresAsync(It.IsAny<FacturaVentaBorradorFiltro?>(), It.IsAny<PageRequest?>(), It.IsAny<CancellationToken>()))
            .Callback<FacturaVentaBorradorFiltro?, PageRequest?, CancellationToken>((_, _, ct) => tokens.Add(ct))
            .Returns(new TaskCompletionSource<PagedResult<FacturaVentaBorradorResponse>>().Task);
        facturas.Setup(c => c.ListFacturasAsync(It.IsAny<FacturaVentaSearchCriteria?>(), It.IsAny<PageRequest?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<FacturaVentaResponse>([], 1, 1, 5));
        app.Simular<INotaCreditoVentaApiClient>()
            .Setup(c => c.ListBorradoresAsync(It.IsAny<NotaCreditoVentaBorradorFiltro?>(), It.IsAny<PageRequest?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<NotaCreditoVentaBorradorResponse>([], 1, 1, 2));
        _ = app.Cliente();

        using var scope = app.Services.CreateScope();
        var indicadores = (IndicadoresModulos)scope.ServiceProvider.GetRequiredService<IIndicadoresModulos>();
        indicadores.TiempoMaximoPorIndicador = TimeSpan.FromMilliseconds(100);
        var reloj = System.Diagnostics.Stopwatch.StartNew();
        var resultado = await indicadores.ObtenerAsync("facturacion", PermisosTestAuthHandler.Principal("Administrador"));

        Assert.True(reloj.Elapsed < TimeSpan.FromSeconds(5), $"Tardó {reloj.Elapsed}.");
        Assert.Equal(["Facturas registradas", "Notas de crédito en preparación"], resultado.Select(i => i.Titulo));
        Assert.True(Assert.Single(tokens).IsCancellationRequested);
    }

    [Fact]
    public async Task Indicadores_SinCanConsult_NingunoYSinLlamadas()
    {
        using var app = new BlazorSsrFactory();
        var facturas = app.Simular<IFacturaVentaApiClient>();
        _ = app.Cliente();

        using var scope = app.Services.CreateScope();
        var indicadores = scope.ServiceProvider.GetRequiredService<IIndicadoresModulos>();
        var resultado = await indicadores.ObtenerAsync("facturacion", PermisosTestAuthHandler.Principal("Administrador", "CanModify"));
        var ninguno = await indicadores.ObtenerAsync("facturacion", PermisosTestAuthHandler.Principal("Administrador", ""));

        Assert.Empty(resultado);
        Assert.Empty(ninguno);
        Assert.Empty(facturas.Invocations);
    }

    [Fact]
    public async Task Grupo_SinModulosVisibles_MensajeSinEnlaces()
    {
        using var app = new BlazorSsrFactory();

        var html = await HtmlAsync(app.Cliente("Ejecutor"), "/modulos/administracion");

        Assert.Contains("No tiene módulos disponibles en este grupo.", html);
        Assert.DoesNotContain("data-testid=\"tarjeta-modulo\"", html);
    }
}
