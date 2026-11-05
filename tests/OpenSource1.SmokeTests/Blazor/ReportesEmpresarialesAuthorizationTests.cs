using System.Net;
using OpenSource1.SmokeTests.TestInfrastructure;

namespace OpenSource1.SmokeTests.Blazor;

public sealed class ReportesEmpresarialesAuthorizationTests
{
    [Theory]
    [InlineData("/reports/empresarial/ventas.pdf")]
    [InlineData("/reports/empresarial/inventario.xlsx")]
    [InlineData("/reports/empresarial/top-productos.pdf")]
    [InlineData("/reports/empresarial/cuentas-por-cobrar.xlsx")]
    public async Task DescargaDirecta_SinSesion_NoEntregaArchivo(string url)
    {
        await using var app = new BlazorSsrFactory();
        using var response = await app.Cliente(anonimo: true).GetAsync(url);
        Assert.Contains(response.StatusCode, new[] { HttpStatusCode.Unauthorized, HttpStatusCode.Redirect });
        Assert.NotEqual("application/pdf", response.Content.Headers.ContentType?.MediaType);
        Assert.NotEqual("application/vnd.openxmlformats-officedocument.spreadsheetml.sheet", response.Content.Headers.ContentType?.MediaType);
    }

    [Theory]
    [InlineData("/reporteria/empresarial")]
    [InlineData("/reports/empresarial/ventas.pdf")]
    public async Task UsuarioSinPermisos_NoConsultaNiExporta(string url)
    {
        await using var app = new BlazorSsrFactory();
        using var response = await app.Cliente(permisos: "").GetAsync(url);
        Assert.Contains(response.StatusCode, new[] { HttpStatusCode.Forbidden, HttpStatusCode.Redirect });
    }
}
