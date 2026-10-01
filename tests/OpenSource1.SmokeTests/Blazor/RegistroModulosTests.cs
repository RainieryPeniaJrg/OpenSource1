extern alias BlazorApp;

using System.Reflection;
using BlazorApp::OpenSource1.Blazor.Navigation;
using BlazorApp::OpenSource1.Blazor.Security;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Components;
using Microsoft.Extensions.DependencyInjection;
using OpenSource1.SmokeTests.TestInfrastructure;

namespace OpenSource1.SmokeTests.Blazor;

/// <summary>
/// Task A1: el catálogo de módulos es la fuente única de navegación. Cada listado (@page sin parámetros que no sea de
/// cuenta, error, inicio, búsqueda ni alta) aparece en el registro, y cada ruta del registro existe como @page.
/// Visibilidad evaluada con las políticas reales del host (claim "permission") y los roles.
/// </summary>
public sealed class RegistroModulosTests
{
    private static readonly string[] RutasFueraDelRegistro =
    [
        "/", "/buscar", "/Error", "/not-found", "/access-denied",
        // Listas antiguas de borradores: redirigen a la pestaña En preparación (spec alta rápida).
        "/facturas-venta/borradores", "/notas-credito-venta/borradores",
    ];

    [Fact]
    public void CadaListadoTieneModulo_YCadaRutaDelRegistroExiste()
    {
        var plantillas = typeof(BlazorApp::OpenSource1.Blazor.Components.App).Assembly.GetTypes()
            .SelectMany(t => t.GetCustomAttributes<RouteAttribute>())
            .Select(a => "/" + a.Template.Trim('/'))
            .ToHashSet(StringComparer.OrdinalIgnoreCase);

        var listados = plantillas
            .Where(p => !p.Contains('{'))
            .Where(p => !RutasFueraDelRegistro.Contains(p, StringComparer.OrdinalIgnoreCase))
            .Where(p => !p.StartsWith("/account/", StringComparison.OrdinalIgnoreCase))
            .Where(p => !p.EndsWith("/new", StringComparison.OrdinalIgnoreCase)
                        && !p.EndsWith("/nuevo", StringComparison.OrdinalIgnoreCase)
                        && !p.EndsWith("/nueva", StringComparison.OrdinalIgnoreCase))
            .ToList();

        var rutasRegistro = CatalogoModulos.Modulos.Select(m => m.Ruta).ToHashSet(StringComparer.OrdinalIgnoreCase);

        Assert.All(listados, ruta => Assert.True(rutasRegistro.Contains(ruta), $"El listado {ruta} no está en CatalogoModulos."));
        Assert.All(rutasRegistro, ruta => Assert.True(plantillas.Contains(ruta), $"La ruta {ruta} del registro no existe como @page."));
    }

    [Fact]
    public void Catalogo_ClavesUnicas_YGruposExistentes()
    {
        Assert.Equal(CatalogoModulos.Modulos.Count, CatalogoModulos.Modulos.Select(m => m.Clave).Distinct().Count());
        var grupos = CatalogoModulos.Grupos.Select(g => g.Clave).ToHashSet();
        Assert.All(CatalogoModulos.Modulos, m => Assert.Contains(m.Grupo, grupos));
        Assert.Equal(
            ["clientes", "productos", "inventario", "ventas", "facturacion", "contabilidad", "reportes", "configuracion", "administracion"],
            CatalogoModulos.Grupos.OrderBy(g => g.Orden).Select(g => g.Clave));
    }

    [Theory]
    [InlineData("Administrador", true, true, true)]
    [InlineData("Supervisor", false, false, true)]
    [InlineData("Ejecutor", false, false, false)]
    public async Task Visibles_SegunRolYPolitica(string rol, bool usuarios, bool fechas, bool bitacora)
    {
        var registro = Registro();

        var visibles = (await registro.VisiblesAsync(PermisosTestAuthHandler.Principal(rol))).Select(m => m.Clave).ToList();

        Assert.Contains("clientes", visibles);
        Assert.Equal(usuarios, visibles.Contains("usuarios"));
        Assert.Equal(fechas, visibles.Contains("fechas-registro"));
        Assert.Equal(fechas, visibles.Contains("series-numeracion"));
        Assert.Equal(fechas, visibles.Contains("configuracion-numeracion"));
        Assert.Equal(bitacora, visibles.Contains("bitacora"));
    }

    [Fact]
    public async Task Anonimo_NoVeNada_YSinCanConsultSoloVeLoQueNoLaExige()
    {
        var registro = Registro();

        Assert.Empty(await registro.VisiblesAsync(new System.Security.Claims.ClaimsPrincipal()));

        // Supervisor sin CanConsult: solo la Bitácora (rol) sigue visible.
        var sinConsulta = await registro.VisiblesAsync(PermisosTestAuthHandler.Principal("Supervisor", permisos: ""));
        Assert.Equal(["bitacora"], sinConsulta.Select(m => m.Clave));
    }

    [Fact]
    public async Task GruposVisibles_SoloLosQueTienenModulosVisibles()
    {
        var registro = Registro();

        var ejecutor = (await registro.GruposVisiblesAsync(PermisosTestAuthHandler.Principal("Ejecutor"))).Select(g => g.Clave).ToList();
        var admin = (await registro.GruposVisiblesAsync(PermisosTestAuthHandler.Principal("Administrador"))).Select(g => g.Clave).ToList();

        Assert.DoesNotContain("administracion", ejecutor);
        Assert.Contains("configuracion", ejecutor);
        Assert.Equal(9, admin.Count);
    }

    [Theory]
    [InlineData("facturas-venta/borradores/3f2b8c1e-0000-0000-0000-000000000001", "facturas")]
    [InlineData("/facturas-venta/FV000001", "facturas")]
    [InlineData("/contabilidad/movimientos", "movimientos-contables")]
    [InlineData("/contabilidad", "costo-inventario")]
    [InlineData("/clientes/3f2b8c1e-0000-0000-0000-000000000001/editar", "clientes")]
    [InlineData("/unidades-medida?pagina=2", "unidades-medida")]
    public void ModuloDeRuta_ElPrefijoMasLargoGana(string ruta, string clave)
    {
        Assert.Equal(clave, Registro().ModuloDeRuta(ruta)?.Clave);
    }

    /// <summary>
    /// Puerta C: las páginas de alta y edición marcan el módulo de su listado; las altas y las páginas en preparación marcan el
    /// módulo de su documento (spec alta rápida: Facturación tiene dos entradas, Facturas y Notas de crédito).
    /// </summary>
    [Theory]
    [InlineData("/notas-credito-venta/nueva", "notas-credito")]
    [InlineData("/notas-credito-venta/nueva?socioId=3f2b8c1e-0000-0000-0000-000000000001", "notas-credito")]
    [InlineData("/facturas-venta/nueva", "facturas")]
    [InlineData("/facturas-venta/nueva/?socioId=3f2b8c1e-0000-0000-0000-000000000001", "facturas")]
    [InlineData("/facturas-venta/en-preparacion/3f2b8c1e-0000-0000-0000-000000000001", "facturas")]
    [InlineData("/notas-credito-venta/en-preparacion/3f2b8c1e-0000-0000-0000-000000000001", "notas-credito")]
    [InlineData("/notas-credito-venta/NC000001", "notas-credito")]
    [InlineData("/facturas-venta/nuevax", "facturas")]
    [InlineData("/cobros/nuevo?socioId=3f2b8c1e-0000-0000-0000-000000000001", "cobros")]
    [InlineData("/clientes/nuevo", "clientes")]
    [InlineData("/productos/3f2b8c1e-0000-0000-0000-000000000001/editar", "productos")]
    [InlineData("/diarios-inventario/nuevo?productoId=3f2b8c1e-0000-0000-0000-000000000001", "diarios-inventario")]
    [InlineData("/diarios-inventario/3f2b8c1e-0000-0000-0000-000000000001/editar", "diarios-inventario")]
    [InlineData("/unidades-medida/nuevo", "unidades-medida")]
    [InlineData("/terminos-pago/3f2b8c1e-0000-0000-0000-000000000001/editar", "terminos-pago")]
    [InlineData("/categorias-producto/nuevo", "categorias-producto")]
    [InlineData("/almacenes/3f2b8c1e-0000-0000-0000-000000000001/editar", "almacenes")]
    [InlineData("/cuentas-contables/nuevo", "plan-cuentas")]
    [InlineData("/grupos-contables/nuevo?tipo=iva", "grupos-contables")]
    [InlineData("/grupos-cliente-contable/3f2b8c1e-0000-0000-0000-000000000001/editar", "grupos-cliente-contable")]
    [InlineData("/setups-contables/nuevo", "setups-contables")]
    [InlineData("/admin/users/nuevo", "usuarios")]
    [InlineData("/admin/users/abc/editar", "usuarios")]
    [InlineData("/facturas-venta/borradores/3f2b8c1e-0000-0000-0000-000000000001/editar", "facturas")]
    [InlineData("/notas-credito-venta/borradores/3f2b8c1e-0000-0000-0000-000000000001/editar", "notas-credito")]
    [InlineData("/series/nueva", "series-numeracion")]
    [InlineData("/series/3f2b8c1e-0000-0000-0000-000000000001", "series-numeracion")]
    [InlineData("/configuracion/numeracion", "configuracion-numeracion")]
    public void ModuloDeRuta_AltasYEdiciones_MarcanSuModulo(string ruta, string clave)
    {
        Assert.Equal(clave, Registro().ModuloDeRuta(ruta)?.Clave);
    }

    [Fact]
    public void ModuloDeRuta_CadaAltaYEdicionTieneModulo()
    {
        var plantillas = typeof(BlazorApp::OpenSource1.Blazor.Components.App).Assembly.GetTypes()
            .SelectMany(t => t.GetCustomAttributes<RouteAttribute>())
            .Select(a => "/" + a.Template.Trim('/'))
            .Where(p => p.EndsWith("/nuevo", StringComparison.OrdinalIgnoreCase)
                        || p.EndsWith("/nueva", StringComparison.OrdinalIgnoreCase)
                        || p.EndsWith("/editar", StringComparison.OrdinalIgnoreCase))
            .ToList();

        Assert.NotEmpty(plantillas);
        foreach (var plantilla in plantillas)
        {
            var ruta = System.Text.RegularExpressions.Regex.Replace(plantilla, "\\{[^}]+\\}", "3f2b8c1e-0000-0000-0000-000000000001");
            Assert.True(Registro().ModuloDeRuta(ruta) is not null, $"La ruta {plantilla} no marca ningún módulo.");
        }
    }

    [Theory]
    [InlineData("")]
    [InlineData("/")]
    [InlineData("/clientesx")]
    [InlineData(null)]
    public void ModuloDeRuta_SinCoincidencia_EsNull(string? ruta)
    {
        Assert.Null(Registro().ModuloDeRuta(ruta));
    }

    [Fact]
    public void Facturacion_DosEntradas_YLasPalabrasClaveDeLaPreparacion()
    {
        var facturacion = CatalogoModulos.Modulos.Where(m => m.Grupo == "facturacion").Select(m => m.Clave).ToList();

        Assert.Equal(["facturas", "notas-credito"], facturacion);
        Assert.Equal(["facturas", "notas-credito"], TextoBusqueda.FiltrarModulos(CatalogoModulos.Modulos, "borrador").Select(m => m.Clave).Order());
        Assert.Equal(["facturas", "notas-credito"], TextoBusqueda.FiltrarModulos(CatalogoModulos.Modulos, "en preparación").Select(m => m.Clave).Order());
        Assert.Empty(CatalogoModulos.RutasAsociadas);
    }

    [Fact]
    public void Normalizar_QuitaAcentosYMayusculas()
    {
        Assert.Equal("categorias de producto", TextoBusqueda.Normalizar("  Categorías de PRODUCTO "));
        Assert.Equal(string.Empty, TextoBusqueda.Normalizar(null));
    }

    [Fact]
    public void FiltrarModulos_PorTituloPalabraClaveYGrupo_SinAcentos()
    {
        Assert.Contains(TextoBusqueda.FiltrarModulos(CatalogoModulos.Modulos, "categoria"), m => m.Clave == "categorias-producto");
        Assert.Equal(
            ["movimientos-cliente", "estado-cuenta", "grupos-cliente-contable"],
            TextoBusqueda.FiltrarModulos(CatalogoModulos.Modulos, "CXC").Select(m => m.Clave));
        Assert.Contains(TextoBusqueda.FiltrarModulos(CatalogoModulos.Modulos, "facturación"), m => m.Clave == "notas-credito");
        Assert.Empty(TextoBusqueda.FiltrarModulos(CatalogoModulos.Modulos, "  "));
    }

    private static RegistroModulos Registro()
    {
        var servicios = new ServiceCollection();
        servicios.AddLogging();
        servicios.AddAuthorizationCore(PoliticasBlazor.Configurar);
        var autorizacion = servicios.BuildServiceProvider().GetRequiredService<IAuthorizationService>();
        return new RegistroModulos(autorizacion);
    }
}
