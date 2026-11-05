using OpenSource1.Application.Features.Reportes;
using OpenSource1.Core.Common;
using Moq;
using OpenSource1.Application.Data;
using OpenSource1.Application.Features.MovimientosCliente;
using OpenSource1.Application.Features.MovimientosCliente.Dtos;
using OpenSource1.Infrastructure.Data.Queries;

namespace OpenSource1.SmokeTests.Features.Reportes;

public class ReportesValidacionTests
{
    [Fact]
    public void RangoInvertido_DevuelveErrorDeDesde() =>
        Assert.Contains(ReportesValidacion.Validar(new(Desde: new(2026, 2, 2), Hasta: new(2026, 2, 1)), "ventas"), e => e.Campo == "Desde");

    [Theory]
    [InlineData("ventas", "borrador")]
    [InlineData("inventario", "ventas")]
    [InlineData("cuentas-por-cobrar", "sin-existencia")]
    public void EstadoAjenoAlReporte_DevuelveError(string reporte, string estado) =>
        Assert.Contains(ReportesValidacion.Validar(new(Estado: estado), reporte), e => e.Campo == "Estado");

    [Theory]
    [InlineData("inventario")]
    [InlineData("cuentas-por-cobrar")]
    public void VistaAcumulada_RechazaDesde(string reporte) =>
        Assert.Contains(ReportesValidacion.Validar(new(Desde: new(2026, 1, 1)), reporte), e => e.Campo == "Desde");

    [Fact]
    public void TamanoFueraDelContrato_DevuelveError() =>
        Assert.Contains(ReportesValidacion.Validar(new(TamanoPagina: 201), "ventas"), e => e.Campo == "TamanoPagina");

    [Theory]
    [InlineData("usd")]
    [InlineData("USDD")]
    public void MonedaInvalida_ImpideMezclarTotales(string moneda) =>
        Assert.Contains(ReportesValidacion.Validar(new(Moneda: moneda), "ventas"), e => e.Campo == "Moneda");

    [Fact]
    public async Task Handler_InvalidoNoConsultaRepositorio()
    {
        var repo = new Mock<IReportesReadRepository>(MockBehavior.Strict);
        var resultado = await new VentasReporteHandler(repo.Object).Handle(new(new(Estado: "borrador")), default);
        Assert.True(resultado.EsFallo);
        Assert.Contains(resultado.Errores, e => e.Campo == "Estado");
    }

    [Fact]
    public async Task Handler_DesdeFuturoSinHasta_RechazaRangoTrasResolverCorte()
    {
        var repo = new Mock<IReportesReadRepository>();
        repo.Setup(r => r.VentasAsync(It.IsAny<ReporteCriterios>(), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new ReporteResponse<VentaReporteFila>(new([], 1, 200, 0), new(), DateOnly.FromDateTime(DateTime.UtcNow)));
        var futuro = DateOnly.FromDateTime(DateTime.UtcNow).AddDays(1);
        var resultado = await new VentasReporteHandler(repo.Object).Handle(new(new(Desde: futuro)), default);
        Assert.True(resultado.EsFallo);
        Assert.Contains(resultado.Errores, e => e.Campo == "Desde");
    }

    [Fact]
    public async Task Handler_ResuelveCorteUtcYConservaPaginacion()
    {
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
        var esperado = new ReporteResponse<VentaReporteFila>(new([], 2, 10, 21), new(Ventas: 100, Neto: 100), hoy);
        var repo = new Mock<IReportesReadRepository>();
        repo.Setup(r => r.VentasAsync(It.Is<ReporteCriterios>(c => c.Hasta == hoy && c.Pagina == 2 && c.TamanoPagina == 10), It.IsAny<CancellationToken>())).ReturnsAsync(esperado);
        var resultado = await new VentasReporteHandler(repo.Object).Handle(new(new(Pagina: 2, TamanoPagina: 10)), default);
        Assert.True(resultado.EsExito);
        Assert.Equal(21, resultado.Valor.Pagina.Total);
        Assert.Equal(100, resultado.Valor.Totales.Neto);
    }

    [Fact]
    public async Task CuentasPorCobrar_ConservaCorteClienteYSaldosHistoricos()
    {
        var corte = new DateOnly(2026, 1, 31); var cliente = Guid.NewGuid();
        var fila = new EstadoCuentaClienteResponse { SocioNegocioId = cliente, Total = 80, Dias1a30 = 80 };
        var clientes = new Mock<IMovimientoClienteReadRepository>();
        clientes.Setup(r => r.GetEstadoCuentaAsync(new EstadoCuentaCriterios(corte, cliente, null, true),
                It.Is<PageRequest>(p => p.Pagina == 2 && p.TamanoPagina == 10), It.IsAny<CancellationToken>()))
            .ReturnsAsync(new EstadoCuentaResponse(new([fila], 2, 10, 12), corte, new EstadoCuentaTramos { Total = 500 }));
        var repo = new DapperReportesReadRepository(Mock.Of<IDbSession>(), clientes.Object);
        var resultado = await repo.CuentasPorCobrarAsync(new(Hasta: corte, ClienteId: cliente, Estado: "con-saldo", Pagina: 2, TamanoPagina: 10), default);
        Assert.Equal(corte, resultado.FechaCorte);
        Assert.Equal(500, resultado.Totales.CuentasPorCobrar);
        Assert.Equal(80, Assert.Single(resultado.Pagina.Items).Total);
        Assert.Equal(12, resultado.Pagina.Total);
    }
}
