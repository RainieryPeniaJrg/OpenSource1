using System.Net;
using System.Text.Json;
using OpenSource1.SmokeTests.TestInfrastructure;

namespace OpenSource1.SmokeTests.Features.Reportes;

[Collection(PostgresCollection.Name)]
public sealed class ReportesExportacionApiTests(PostgresTestFixture fixture) : IClassFixture<PostgresTestFixture>
{
    [Fact]
    public async Task Exportaciones_CompletasProtegidasYConFiltrosValidados()
    {
        await using var factory = fixture.CreateFactory();
        using var client = factory.CreateClient(new Microsoft.AspNetCore.Mvc.Testing.WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        foreach (var tipo in new[] { "ventas", "inventario", "top-productos", "cuentas-por-cobrar" })
        {
            var ruta = $"/api/reportes/{tipo}/exportacion";
            using var respuesta = await client.GetAsync(ruta);
            Assert.Equal(HttpStatusCode.OK, respuesta.StatusCode);
            using var contenido = JsonDocument.Parse(await respuesta.Content.ReadAsStringAsync());
            var pagina = contenido.RootElement.GetProperty("pagina");
            Assert.Equal(pagina.GetProperty("total").GetInt64(), pagina.GetProperty("items").GetArrayLength());

            using var anonimo = new HttpRequestMessage(HttpMethod.Get, ruta);
            anonimo.Headers.Add("X-Test-Anonymous", "true");
            Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(anonimo)).StatusCode);
            using var prohibido = new HttpRequestMessage(HttpMethod.Get, ruta);
            prohibido.Headers.Add("X-Test-Roles", "Desconocido");
            Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(prohibido)).StatusCode);
            Assert.Equal(HttpStatusCode.BadRequest, (await client.GetAsync(ruta + "?estado=invalido")).StatusCode);
        }
    }
}
