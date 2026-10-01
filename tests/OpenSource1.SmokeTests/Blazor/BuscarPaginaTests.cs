extern alias BlazorApp;

using BlazorApp::OpenSource1.Blazor.Components.Pages;
using BlazorApp::OpenSource1.Blazor.Services;
using Moq;
using OpenSource1.Application.Features.Busqueda.Dtos;
using OpenSource1.Application.Features.Productos.Dtos;
using OpenSource1.Application.Features.SociosNegocio.Dtos;
using OpenSource1.Core.Common;
using OpenSource1.SmokeTests.TestInfrastructure;
using static OpenSource1.SmokeTests.TestInfrastructure.HtmlSsr;

namespace OpenSource1.SmokeTests.Blazor;

/// <summary>Fix-Features A3: página /buscar (módulos del registro + un bloque por tipo) y barra superior de búsqueda.</summary>
public sealed class BuscarPaginaTests
{
    [Fact]
    public async Task Buscar_MuestraRegistrosConEnlace_YVerTodos()
    {
        using var app = new BlazorSsrFactory();
        var productoId = Guid.NewGuid();
        app.Simular<IBusquedaApiClient>()
            .Setup(c => c.BuscarAsync("tornillo", 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Respuesta(new ResultadoBusqueda(TiposResultadoBusqueda.Productos, productoId.ToString(), "Tornillo 3/8", "TOR-38", $"/productos/{productoId}")));

        var html = await HtmlAsync(app.Cliente(), "/buscar?q=tornillo");

        Assert.Contains($"href=\"/productos/{productoId}\"", html);
        Assert.Contains("Tornillo 3/8", html);
        Assert.Contains("href=\"/productos?nombre=tornillo\"", html);
        Assert.Contains("Ningún módulo coincide", html);
        Assert.DoesNotContain("data-testid=\"resultados-clientes\"", html);
    }

    [Fact]
    public async Task Buscar_ModulosSinAcentos_SegunPermisos()
    {
        using var app = new BlazorSsrFactory();
        app.Simular<IBusquedaApiClient>()
            .Setup(c => c.BuscarAsync(It.IsAny<string>(), 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Respuesta());

        var admin = await HtmlAsync(app.Cliente("Administrador"), "/buscar?q=categoria");
        var ejecutor = await HtmlAsync(app.Cliente("Ejecutor"), "/buscar?q=usuarios");
        var adminUsuarios = await HtmlAsync(app.Cliente("Administrador"), "/buscar?q=usuarios");

        Assert.Contains("href=\"/categorias-producto\"", admin);
        // Solo el bloque de resultados (el menú lateral ya se probó en A2).
        Assert.DoesNotContain("href=\"/admin/users\"", BloqueModulos(ejecutor));
        Assert.Contains("href=\"/admin/users\"", BloqueModulos(adminUsuarios));
    }

    [Fact]
    public async Task Buscar_ConApiCaida_MuestraModulosYAviso()
    {
        using var app = new BlazorSsrFactory();
        app.Simular<IBusquedaApiClient>()
            .Setup(c => c.BuscarAsync(It.IsAny<string>(), 5, It.IsAny<CancellationToken>()))
            .ThrowsAsync(new HttpRequestException("API caída"));

        var html = await HtmlAsync(app.Cliente(), "/buscar?q=clientes");

        Assert.Contains("No fue posible buscar registros en este momento", html);
        Assert.Contains("href=\"/clientes\"", BloqueModulos(html));
    }

    [Fact]
    public async Task Buscar_ConsultaCorta_NoLlamaALaApi()
    {
        using var app = new BlazorSsrFactory();
        var api = app.Simular<IBusquedaApiClient>();

        var html = await HtmlAsync(app.Cliente(), "/buscar?q=%20a%20");

        Assert.Contains("Escriba al menos 2 caracteres", html);
        api.Verify(c => c.BuscarAsync(It.IsAny<string>(), It.IsAny<int>(), It.IsAny<CancellationToken>()), Times.Never);
    }

    [Fact]
    public async Task BarraSuperior_EnPaginasAutenticadas_YConservaLaConsulta()
    {
        using var app = new BlazorSsrFactory();
        app.Simular<IBusquedaApiClient>()
            .Setup(c => c.BuscarAsync(It.IsAny<string>(), 5, It.IsAny<CancellationToken>()))
            .ReturnsAsync(Respuesta());

        var reporteria = await HtmlAsync(app.Cliente(), "/reporteria");
        var buscar = await HtmlAsync(app.Cliente(), "/buscar?q=abc");
        var login = await app.Cliente(anonimo: true).GetStringAsync("/account/login");

        Assert.Contains("data-testid=\"busqueda-global-form\"", reporteria);
        Assert.Contains("action=\"/buscar\"", reporteria);
        Assert.Contains("id=\"busqueda-global\" name=\"q\"", reporteria);
        Assert.Contains("value=\"abc\"", buscar);
        Assert.DoesNotContain("data-testid=\"busqueda-global-form\"", login);
    }

    [Theory]
    // Clientes: código (serie SOCIOS, 6 dígitos) / RNC o cédula (7+ dígitos, guiones admitidos) / nombre (con o sin dígitos).
    [InlineData(TiposResultadoBusqueda.Clientes, "ana", "/clientes?nombre=ana")]
    [InlineData(TiposResultadoBusqueda.Clientes, "000012", "/clientes?codigo=000012&filters=codigo")]
    [InlineData(TiposResultadoBusqueda.Clientes, "12", "/clientes?codigo=12&filters=codigo")]
    [InlineData(TiposResultadoBusqueda.Clientes, "131246789", "/clientes?documento=131246789&filters=documento")]
    [InlineData(TiposResultadoBusqueda.Clientes, "001-1234567-8", "/clientes?documento=001-1234567-8&filters=documento")]
    [InlineData(TiposResultadoBusqueda.Clientes, "Tienda 24", "/clientes?nombre=Tienda%2024")]
    // Productos: una sola palabra con dígitos = código; si no, nombre.
    [InlineData(TiposResultadoBusqueda.Productos, "tor", "/productos?nombre=tor")]
    [InlineData(TiposResultadoBusqueda.Productos, "P-001", "/productos?codigo=P-001&filters=codigo")]
    [InlineData(TiposResultadoBusqueda.Productos, "tornillo 3/4", "/productos?nombre=tornillo%203%2F4")]
    // Facturas y borradores: número solo si la consulta son dígitos; si no, nombre de facturación.
    [InlineData(TiposResultadoBusqueda.Facturas, "00000012", "/facturas-venta?numero=00000012")]
    [InlineData(TiposResultadoBusqueda.Facturas, "FV01", "/facturas-venta?nombre=FV01")]
    [InlineData(TiposResultadoBusqueda.Facturas, "Tienda 24", "/facturas-venta?nombre=Tienda%2024")]
    [InlineData(TiposResultadoBusqueda.Facturas, "comercial", "/facturas-venta?nombre=comercial")]
    [InlineData(TiposResultadoBusqueda.BorradoresFactura, "0001", "/facturas-venta?estado=preparacion&numero=0001")]
    [InlineData(TiposResultadoBusqueda.BorradoresFactura, "B 1", "/facturas-venta?estado=preparacion&nombre=B%201")]
    [InlineData(TiposResultadoBusqueda.NotasCredito, "NC", "/notas-credito-venta?numero=NC")]
    [InlineData(TiposResultadoBusqueda.BorradoresNotaCredito, "NC", "/notas-credito-venta?estado=preparacion&numero=NC")]
    public void VerTodosUrl_ApuntaAlListadoFiltrado(string tipo, string q, string esperado)
    {
        Assert.Equal(esperado, Buscar.VerTodosUrl(tipo, q));
    }

    [Theory]
    [InlineData("000012", "codigo")]
    [InlineData("131246789", "documento")]
    public async Task VerTodosUrl_Clientes_ElListadoEnviaElFiltroALaApi(string q, string campo)
    {
        using var app = new BlazorSsrFactory();
        var socios = app.Simular<ISocioNegocioApiClient>();
        socios.Setup(c => c.ListAsync(It.IsAny<SocioNegocioSearchFilter?>(), It.IsAny<PageRequest?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<SocioNegocioResponse>([], 1, PageRequest.TamanoPorDefecto, 0));

        await HtmlAsync(app.Cliente(), Buscar.VerTodosUrl(TiposResultadoBusqueda.Clientes, q));

        socios.Verify(c => c.ListAsync(
            It.Is<SocioNegocioSearchFilter?>(f => f != null && f.NombreComercial == null
                && (campo == "codigo" ? f.Codigo == q && f.NumeroDocumentoFiscal == null : f.NumeroDocumentoFiscal == q && f.Codigo == null)),
            It.IsAny<PageRequest?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    [Fact]
    public async Task VerTodosUrl_Productos_PorCodigo_ElListadoEnviaElFiltroALaApi()
    {
        using var app = new BlazorSsrFactory();
        var productos = app.Simular<IProductoApiClient>();
        productos.Setup(c => c.ListAsync(It.IsAny<ProductoSearchFilter?>(), It.IsAny<PageRequest?>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new PagedResult<ProductoResponse>([], 1, PageRequest.TamanoPorDefecto, 0));

        await HtmlAsync(app.Cliente(), Buscar.VerTodosUrl(TiposResultadoBusqueda.Productos, "TOR-38"));

        productos.Verify(c => c.ListAsync(It.Is<ProductoSearchFilter?>(f => f != null && f.Codigo == "TOR-38" && f.Nombre == null), It.IsAny<PageRequest?>(), It.IsAny<CancellationToken>()), Times.Once);
    }

    /// <summary>Desde la sección de módulos hasta el final (excluye el menú lateral, que va antes en el layout).</summary>
    private static string BloqueModulos(string html)
    {
        var inicio = html.IndexOf("data-testid=\"resultados-modulos\"", StringComparison.Ordinal);
        Assert.True(inicio >= 0, "Falta el bloque data-testid=\"resultados-modulos\".");
        return html[inicio..];
    }

    private static ConsultaResultado<BusquedaGlobalResponse> Respuesta(params ResultadoBusqueda[] items) =>
        new(true, string.Empty, new BusquedaGlobalResponse(
            items.GroupBy(i => i.Tipo).Select(g => new GrupoResultadosBusqueda(g.Key, g.Key, g.ToList())).ToList()));
}
