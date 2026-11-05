using OpenSource1.Application.Features.Cobros;
using OpenSource1.Application.Services.Clientes;
using OpenSource1.Core.Enums;

namespace OpenSource1.SmokeTests.Features.Cobros;

public sealed class ReclasificacionCxCTests
{
    private static readonly Guid Socio = Guid.NewGuid();
    private static readonly Guid CuentaFactura = Guid.NewGuid();
    private static readonly Guid CuentaPago = Guid.NewGuid();
    private static readonly DateOnly Fecha = new(2026, 9, 15);

    [Fact]
    public void CuentasDistintas_RevierteCreditoDePagoYAcreditaCuentaFactura()
    {
        var factura = Movimiento(1, CuentaFactura, 118m);
        var pago = Movimiento(2, CuentaPago, -50m);
        var asiento = ReclasificacionCxC.Construir(factura, pago, 50m, Fecha);

        Assert.NotNull(asiento);
        Assert.Equal(Fecha, asiento.FechaRegistro);
        Assert.Equal(0m, asiento.Lineas.Sum(l => l.Importe));
        Assert.Collection(asiento.Lineas,
            l => { Assert.Equal(CuentaPago, l.CuentaContableId); Assert.Equal(50m, l.Importe); Assert.Equal(Socio, l.SocioNegocioId); },
            l => { Assert.Equal(CuentaFactura, l.CuentaContableId); Assert.Equal(-50m, l.Importe); Assert.Equal(Socio, l.SocioNegocioId); });
        Assert.Equal("1:2", asiento.ClaveOrigen);
    }

    [Fact]
    public void MismaCuenta_NoGeneraAsiento() =>
        Assert.Null(ReclasificacionCxC.Construir(Movimiento(1, CuentaFactura, 118m), Movimiento(2, CuentaFactura, -50m), 50m, Fecha));

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(50.001)]
    public void ImporteInvalido_NoConstruyeAsiento(decimal importe) =>
        Assert.Throws<ArgumentOutOfRangeException>(() => ReclasificacionCxC.Construir(
            Movimiento(1, CuentaFactura, 118m), Movimiento(2, CuentaPago, -50m), importe, Fecha));

    private static MovimientoClienteBloqueado Movimiento(long id, Guid cuenta, decimal restante) =>
        new(id, Socio, restante > 0 ? TipoDocumentoCliente.Factura : TipoDocumentoCliente.Pago,
            $"DOC-{id}", restante, new DateOnly(2026, 9, 10), cuenta);
}
