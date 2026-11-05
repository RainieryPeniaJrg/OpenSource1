using OpenSource1.Application.Services.Clientes;
using OpenSource1.Application.Services.Contabilidad;
using OpenSource1.Core.Enums;

namespace OpenSource1.Application.Features.Cobros;

/// <summary>
/// Reclassifies the applied amount when invoice and payment froze different receivable accounts.
/// The payment account is debited and the invoice account credited; original entries remain immutable.
/// The caller owns the transaction and holds both customer movements locked.
/// </summary>
public static class ReclasificacionCxC
{
    public static AsientoContable? Construir(
        MovimientoClienteBloqueado factura, MovimientoClienteBloqueado pago, decimal importe, DateOnly fecha)
    {
        ArgumentNullException.ThrowIfNull(factura);
        ArgumentNullException.ThrowIfNull(pago);
        if (!CobroReglas.ImporteValido(importe))
        {
            throw new ArgumentOutOfRangeException(nameof(importe));
        }

        if (factura.SocioNegocioId != pago.SocioNegocioId)
        {
            throw new ArgumentException("La reclasificación requiere movimientos del mismo cliente.", nameof(pago));
        }

        if (factura.CuentaCxCId == pago.CuentaCxCId)
        {
            return null;
        }

        var descripcion = $"Reclasificación CxC {pago.NumeroDocumento} / {factura.NumeroDocumento}";
        return new AsientoContable(fecha, fecha, TipoDocumentoContable.Cobro, pago.NumeroDocumento,
            descripcion, TipoOrigenMovimiento.Cobro, $"{factura.Id}:{pago.Id}",
            [
                new LineaAsiento(pago.CuentaCxCId, importe, descripcion, pago.SocioNegocioId),
                new LineaAsiento(factura.CuentaCxCId, -importe, descripcion, factura.SocioNegocioId),
            ]);
    }
}
