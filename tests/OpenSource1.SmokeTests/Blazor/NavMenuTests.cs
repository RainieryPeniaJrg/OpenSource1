using System.Text.RegularExpressions;
using OpenSource1.SmokeTests.TestInfrastructure;
using static OpenSource1.SmokeTests.TestInfrastructure.HtmlSsr;

namespace OpenSource1.SmokeTests.Blazor;

/// <summary>
/// Task A2: el menú lateral se genera desde el registro de módulos, agrupado con &lt;details&gt; nativos; el grupo de la ruta
/// actual llega abierto desde el servidor y el módulo actual marcado. /reporteria no llama a la API: sirve de página neutra.
/// </summary>
public sealed class NavMenuTests
{
    [Fact]
    public async Task Admin_GrupoDeLaRutaActualAbierto_YModuloMarcado()
    {
        using var app = new BlazorSsrFactory();
        var html = await HtmlAsync(app.Cliente("Administrador"), "/reporteria");

        Assert.Matches(new Regex("<details data-grupo=\"reportes\" open"), html);
        Assert.DoesNotMatch(new Regex("<details data-grupo=\"clientes\" open"), html);
        Assert.Matches(new Regex("<a href=\"/reporteria\"[^>]*aria-current=\"page\""), html);
        foreach (var grupo in new[] { "clientes", "productos", "inventario", "ventas", "facturacion", "contabilidad", "reportes", "configuracion", "administracion" })
        {
            Assert.Contains($"data-grupo=\"{grupo}\"", html);
        }

        Assert.Contains("href=\"/admin/users\"", html);
        Assert.Contains("href=\"/admin/fechas-registro\"", html);
    }

    [Fact]
    public async Task RutaConQuery_SeResuelveRelativaALaBase_YAbreSuGrupo()
    {
        using var app = new BlazorSsrFactory();
        var html = await HtmlAsync(app.Cliente("Administrador"), "/reporteria/?formato=pdf&x=%2Fclientes");

        Assert.Matches(new Regex("<details data-grupo=\"reportes\" open"), html);
        Assert.DoesNotMatch(new Regex("<details data-grupo=\"clientes\" open"), html);
        Assert.Matches(new Regex("<a href=\"/reporteria\"[^>]*aria-current=\"page\""), html);
        Assert.DoesNotMatch(new Regex("<a href=\"/\"[^>]*aria-current=\"page\""), html);
    }

    /// <summary>Puerta C: las altas de documentos marcan su documento (Facturas / Notas de crédito) y abren Facturación.</summary>
    [Theory]
    [InlineData("/facturas-venta/nueva", "/facturas-venta", "facturacion")]
    [InlineData("/notas-credito-venta/nueva", "/notas-credito-venta", "facturacion")]
    [InlineData("/cobros/nuevo", "/cobros", "ventas")]
    [InlineData("/unidades-medida/nuevo", "/unidades-medida", "configuracion")]
    public async Task AltaDeDocumento_MarcaSuModuloEnElMenu(string ruta, string marcado, string grupo)
    {
        using var app = new BlazorSsrFactory();
        var html = await HtmlAsync(app.Cliente("Administrador"), ruta);

        Assert.Matches(new Regex($"<details data-grupo=\"{grupo}\" open"), html);
        Assert.Matches(new Regex($"<a href=\"{Regex.Escape(marcado)}\"[^>]*aria-current=\"page\""), html);
        Assert.Single(Regex.Matches(html, "<a href=\"[^\"]*\" class=\"[^\"]*\" aria-current=\"page\""));
    }

    [Fact]
    public async Task Ejecutor_NoVeAdministracionFechasNiBitacora()
    {
        using var app = new BlazorSsrFactory();
        var html = await HtmlAsync(app.Cliente("Ejecutor"), "/reporteria");

        Assert.DoesNotContain("data-grupo=\"administracion\"", html);
        Assert.DoesNotContain("href=\"/admin/users\"", html);
        Assert.DoesNotContain("href=\"/admin/fechas-registro\"", html);
        Assert.DoesNotContain("href=\"/bitacora\"", html);
        Assert.Contains("href=\"/clientes\"", html);
    }

    [Fact]
    public async Task Supervisor_VeBitacora()
    {
        using var app = new BlazorSsrFactory();
        var html = await HtmlAsync(app.Cliente("Supervisor"), "/reporteria");

        Assert.Contains("href=\"/bitacora\"", html);
        Assert.DoesNotContain("href=\"/admin/users\"", html);
    }

    [Fact]
    public async Task ModoColapsado_TemaYSesion_SeConservan()
    {
        using var app = new BlazorSsrFactory();
        var cliente = app.Cliente("Administrador");
        cliente.DefaultRequestHeaders.Add("Cookie", "axionerp-sidebar=collapsed; axionerp-theme=dark");

        var html = await HtmlAsync(cliente, "/reporteria");

        Assert.Contains("title=\"Mostrar menú\"", html);
        Assert.Contains("<html lang=\"es\" class=\"dark\"", html);
        Assert.Contains("action=\"/account/logout\"", html);
    }
}
