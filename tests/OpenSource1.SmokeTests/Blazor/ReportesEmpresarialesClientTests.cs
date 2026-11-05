extern alias BlazorApp;
using System.Net;
using System.Text;
using BlazorApp::OpenSource1.Blazor.Services;
using OpenSource1.Application.Features.Reportes;

namespace OpenSource1.SmokeTests.Blazor;

public sealed class ReportesEmpresarialesClientTests
{
    [Fact]
    public async Task Exportacion_UsaSnapshotServidorYPreservaFiltros()
    {
        var handler = new Respuestas();
        var client = new ReportesEmpresarialesApiClient(new HttpClient(handler) { BaseAddress = new("http://localhost/") });
        var tabla = await client.TablaAsync("ventas", new ReporteCriterios(Desde: new(2026, 1, 1)), true);
        Assert.Equal(2, tabla.Filas.Count);
        Assert.Single(handler.Uris);
        Assert.All(handler.Uris, uri => Assert.Contains("desde=2026-01-01", uri));
        Assert.Contains("/exportacion?", handler.Uris[0]);
        Assert.Equal(2m, tabla.Filas[1][7]);
    }

    [Fact]
    public async Task ErrorApi_NoSePresentaComoReporteVacio()
    {
        var client = new ReportesEmpresarialesApiClient(new HttpClient(new Respuestas(HttpStatusCode.Forbidden)) { BaseAddress = new("http://localhost/") });
        var error = await Assert.ThrowsAsync<HttpRequestException>(() => client.TablaAsync("ventas", new(), false));
        Assert.Equal(HttpStatusCode.Forbidden, error.StatusCode);
    }

    [Fact]
    public async Task Exportacion_SnapshotIncompleto_RechazaArchivoTruncado()
    {
        var client = new ReportesEmpresarialesApiClient(new HttpClient(new Respuestas(cambiarTotales: true)) { BaseAddress = new("http://localhost/") });
        await Assert.ThrowsAsync<HttpRequestException>(() => client.TablaAsync("ventas", new(), true));
    }

    [Fact]
    public async Task Exportacion_FijaCorteEIdentificaMoneda()
    {
        var handler = new Respuestas();
        var client = new ReportesEmpresarialesApiClient(new HttpClient(handler) { BaseAddress = new("http://localhost/") });
        var tabla = await client.TablaAsync("ventas", new(Moneda: "USD"), true);
        Assert.Contains("USD", tabla.Filtros);
        Assert.All(handler.Uris, uri => Assert.Contains("hasta=", uri));
        Assert.All(handler.Uris, uri => Assert.Contains("moneda=USD", uri));
    }

    private sealed class Respuestas(HttpStatusCode status = HttpStatusCode.OK, bool cambiarTotales = false) : HttpMessageHandler
    {
        public List<string> Uris { get; } = [];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Uris.Add(request.RequestUri!.PathAndQuery);
            var numero = Uris.Count;
            var extra = cambiarTotales ? "" : "," + """{"numero":"FV2","tipoDocumento":"Factura","fecha":"2026-01-01","clienteNombre":"Cliente","moneda":"DOP","venta":2,"devolucion":0,"neto":2}""";
            var json = $$"""{"pagina":{"items":[{"numero":"FV{{numero}}","tipoDocumento":"Factura","fecha":"2026-01-01","clienteNombre":"Cliente","moneda":"DOP","venta":{{numero}},"devolucion":0,"neto":{{numero}}}{{extra}}],"pagina":{{numero}},"tamanoPagina":2,"total":2},"totales":{"ventas":3,"devoluciones":0,"neto":3},"fechaCorte":"2026-01-01"}""";
            return Task.FromResult(new HttpResponseMessage(status) { Content = new StringContent(json, Encoding.UTF8, "application/json") });
        }
    }
}
