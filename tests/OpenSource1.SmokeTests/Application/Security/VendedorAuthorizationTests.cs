extern alias BlazorApp;

using System.Security.Claims;
using System.Reflection;
using Microsoft.AspNetCore.Authorization;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenSource1.Application.Security;
using OpenSource1.Api.Controllers;
using OpenSource1.Infrastructure.Identity;

namespace OpenSource1.SmokeTests.Application.Security;

public sealed class VendedorAuthorizationTests
{
    [Fact]
    public void Vendedor_PuedeCapturarYEmitirFactura_SinEscriturasGlobales()
    {
        var permisos = PermisosPorRol.ParaRol("Vendedor");

        Assert.Contains("facturacion.factura.capturar", permisos);
        Assert.Contains("facturacion.factura.emitir", permisos);
        Assert.DoesNotContain(permisos, p => p.EndsWith(".eliminar", StringComparison.Ordinal));
        Assert.DoesNotContain("contabilidad.setup.modificar", permisos);
        Assert.DoesNotContain("contabilidad.cuenta.modificar", permisos);
        Assert.DoesNotContain("inventario.diario.postear", permisos);
        Assert.Contains("Ejecutor", ApplicationRoles.All);
        Assert.Contains("Vendedor", ApplicationRoles.All);
    }

    [Theory]
    [InlineData("CanCapturarFactura", true)]
    [InlineData("CanEmitirFactura", true)]
    [InlineData("CanConsult", true)]
    [InlineData("CanAdd", false)]
    [InlineData("CanModify", false)]
    [InlineData("CanDelete", false)]
    [InlineData("CanAdministrar", false)]
    public async Task Api_Vendedor_PermiteSoloOperacionesDeVenta(string policy, bool expected)
    {
        using var services = ApiServices();
        var authorization = services.GetRequiredService<IAuthorizationService>();
        var result = await authorization.AuthorizeAsync(Vendedor(), null, policy);

        Assert.Equal(expected, result.Succeeded);
    }

    [Theory]
    [InlineData("Administrador", "CanCapturarFactura", true)]
    [InlineData("Administrador", "CanEmitirFactura", true)]
    [InlineData("Ejecutor", "CanCapturarFactura", true)]
    [InlineData("Ejecutor", "CanEmitirFactura", false)]
    [InlineData("Supervisor", "CanCapturarFactura", false)]
    [InlineData("Supervisor", "CanEmitirFactura", true)]
    public async Task Api_PoliticasFactura_ConservanRolesAnteriores(string role, string policy, bool expected)
    {
        using var services = ApiServices();
        var principal = new ClaimsPrincipal(new ClaimsIdentity([new Claim(ClaimTypes.Role, role)], "test"));

        Assert.Equal(expected, (await services.GetRequiredService<IAuthorizationService>().AuthorizeAsync(principal, null, policy)).Succeeded);
    }

    [Theory]
    [InlineData("CanCapturarFactura", true)]
    [InlineData("CanEmitirFactura", true)]
    [InlineData("CanModify", false)]
    [InlineData("CanAdministrar", false)]
    public async Task Ssr_Vendedor_UsaPermisosEspecificosSinModificarGlobal(string policy, bool expected)
    {
        var collection = new ServiceCollection().AddLogging();
        collection.AddAuthorization(BlazorApp::OpenSource1.Blazor.Security.PoliticasBlazor.Configurar);
        using var services = collection.BuildServiceProvider();

        Assert.Equal(expected, (await services.GetRequiredService<IAuthorizationService>().AuthorizeAsync(Vendedor(), null, policy)).Succeeded);
    }

    [Theory]
    [InlineData(nameof(FacturasVentaController.CreateBorrador), true)]
    [InlineData(nameof(FacturasVentaController.CopiarABorrador), true)]
    [InlineData(nameof(FacturasVentaController.CreateLinea), true)]
    [InlineData(nameof(FacturasVentaController.UpdateBorrador), true)]
    [InlineData(nameof(FacturasVentaController.UpdateLinea), true)]
    [InlineData(nameof(FacturasVentaController.LiberarBorrador), true)]
    [InlineData(nameof(FacturasVentaController.ReabrirBorrador), true)]
    [InlineData(nameof(FacturasVentaController.PostearBorrador), true)]
    [InlineData(nameof(FacturasVentaController.DeleteBorrador), false)]
    [InlineData(nameof(FacturasVentaController.DeleteLinea), false)]
    public async Task Api_AccionesFactura_RespetanElAlcanceVendedor(string action, bool expected)
    {
        using var services = ApiServices();
        var authorization = services.GetRequiredService<IAuthorizationService>();
        var policy = Assert.Single(typeof(FacturasVentaController).GetMethod(action)!
            .GetCustomAttributes<AuthorizeAttribute>()).Policy!;

        Assert.Equal(expected, (await authorization.AuthorizeAsync(Vendedor(), null, policy)).Succeeded);
    }

    private static ClaimsPrincipal Vendedor() => new(new ClaimsIdentity(
    [
        new Claim(ClaimTypes.Role, "Vendedor"),
        new Claim("permission", "CanConsult"),
        new Claim("permission", "facturacion.factura.capturar"),
        new Claim("permission", "facturacion.factura.emitir"),
    ], "test"));

    private static ServiceProvider ApiServices()
    {
        var services = new ServiceCollection().AddLogging();
        services.AddApplicationIdentity(new ConfigurationBuilder().Build());
        return services.BuildServiceProvider();
    }
}
