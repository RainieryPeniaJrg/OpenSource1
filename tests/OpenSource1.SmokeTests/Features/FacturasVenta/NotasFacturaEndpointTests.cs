using System.Net;
using Microsoft.AspNetCore.Mvc.Testing;
using OpenSource1.SmokeTests.TestInfrastructure;

namespace OpenSource1.SmokeTests.Features.FacturasVenta;

[Collection(PostgresCollection.Name)]
public sealed class NotasFacturaEndpointTests(PostgresTestFixture fixture) : IClassFixture<PostgresTestFixture>
{
    [Fact]
    public async Task NotasFactura_ExigeConsulta_YDevuelve404SiLaFacturaNoExiste()
    {
        await using var factory = fixture.CreateFactory();
        using var client = factory.CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
        const string route = "/api/facturas-venta/FV-inexistente/notas-credito";
        using var anonymous = new HttpRequestMessage(HttpMethod.Get, route);
        anonymous.Headers.Add("X-Test-Anonymous", "true");
        Assert.Equal(HttpStatusCode.Unauthorized, (await client.SendAsync(anonymous)).StatusCode);
        using var forbidden = new HttpRequestMessage(HttpMethod.Get, route);
        forbidden.Headers.Add("X-Test-Roles", "Desconocido");
        Assert.Equal(HttpStatusCode.Forbidden, (await client.SendAsync(forbidden)).StatusCode);
        Assert.Equal(HttpStatusCode.NotFound, (await client.GetAsync(route)).StatusCode);
    }
}
