using MediatR;
using OpenSource1.Application.Data.UnitOfWork;
using OpenSource1.Application.Services.Clientes;
using OpenSource1.Application.Services.Contabilidad;
using OpenSource1.Application.Services.Registro;
using OpenSource1.Core.Common;
using OpenSource1.Core.Enums;

namespace OpenSource1.Application.Features.Cobros;

/// <summary>
/// Aplicación de un pago a una factura, en UNA transacción propia. Si las CxC congeladas difieren,
/// agrega un asiento de reclasificación por el importe aplicado sin modificar los asientos originales.
/// <list type="number">
/// <item>Entrada: Ids positivos y distintos, importe &gt; 0 con 2 decimales como máximo.</item>
/// <item>Bloqueo <c>FOR UPDATE</c> de los dos movimientos en orden de Id (<see cref="IRegistroMovimientosCliente.BloquearMovimientosAsync"/>):
/// inexistentes → 400 (<c>cobro.movimiento_invalido</c>, referencia del cuerpo, no 404).</item>
/// <item>Mismo socio en ambos movimientos (<c>clientes.socios_distintos</c>) ANTES de bloquear el socio.</item>
/// <item>Socio (<c>FOR SHARE</c>): un socio bloqueado <c>Todo</c> no admite aplicaciones (como no admite pagos); bloqueado solo para
/// <c>Facturacion</c>, sí.</item>
/// <item>Fecha de la aplicación (la indicada o la de hoy) dentro de las fechas de registro permitidas y no anterior
/// a ninguno de los movimientos. Las cuentas CxC distintas generan una reclasificación por el importe aplicado.</item>
/// <item><see cref="IRegistroMovimientosCliente.AplicarAsync"/> valida con los restantes posteriores al bloqueo (mismo socio, signos,
/// importe ≤ mínimo de los restantes) antes de insertar las dos filas Aplicación.</item>
/// </list>
/// <para>
/// Orden de locks: movimientos de cliente (<c>FOR UPDATE</c>, orden de Id: ocupan el lugar del "documento", el primero del orden
/// global) → socios (<c>FOR SHARE</c>) → cuentas → libro contable → serie de asientos (solo si se reclasifica).
/// El posteo de facturas y el pago bloquean socios <c>FOR SHARE</c> pero nunca esperan por un
/// movimiento de cliente existente (solo insertan movimientos nuevos), así que no se forma ningún ciclo.
/// </para>
/// </summary>
public sealed class AplicarPagoCommandHandler(
    IUnitOfWork unitOfWork,
    IRegistroMovimientosCliente registroClientes,
    ICobroDatos datos,
    // Original: IValidadorFechaRegistro validadorFecha)
    IValidadorFechaRegistro validadorFecha,
    IRegistroContable registroContable)
    : IRequestHandler<AplicarPagoCommand, Result<ResultadoAplicacionPago>>
{
    public async Task<Result<ResultadoAplicacionPago>> Handle(AplicarPagoCommand request, CancellationToken cancellationToken)
    {
        if (unitOfWork.HayTransaccionActiva)
        {
            throw new InvalidOperationException(
                "AplicarPago debe ejecutarse fuera de otra transacción: garantiza su propia atomicidad.");
        }

        var errores = new List<Error>();
        if (request.MovimientoFacturaId <= 0)
        {
            errores.Add(MovimientoInvalido("MovimientoFacturaId"));
        }

        if (request.MovimientoPagoId <= 0)
        {
            errores.Add(MovimientoInvalido("MovimientoPagoId"));
        }
        else if (request.MovimientoPagoId == request.MovimientoFacturaId)
        {
            errores.Add(new Error(
                "cobro.aplicacion_invalida", "El pago y el documento a pagar deben ser movimientos distintos.", "MovimientoPagoId"));
        }

        if (!CobroReglas.ImporteValido(request.Importe))
        {
            errores.Add(CobroReglas.ImporteInvalido("El importe a aplicar"));
        }

        if (errores.Count > 0)
        {
            return Fallo([.. errores]);
        }

        await using var transaccion = await unitOfWork.BeginTransactionAsync(cancellationToken);

        var bloqueados = await registroClientes.BloquearMovimientosAsync(
            [request.MovimientoFacturaId, request.MovimientoPagoId], cancellationToken);
        var factura = bloqueados.FirstOrDefault(m => m.Id == request.MovimientoFacturaId);
        var pago = bloqueados.FirstOrDefault(m => m.Id == request.MovimientoPagoId);
        if (factura is null)
        {
            errores.Add(MovimientoInvalido("MovimientoFacturaId"));
        }

        if (pago is null)
        {
            errores.Add(MovimientoInvalido("MovimientoPagoId"));
        }

        if (factura is null || pago is null)
        {
            return Fallo([.. errores]);
        }

        // Mismo socio ANTES de bloquear los socios: el error correcto (clientes.socios_distintos) aunque alguno esté bloqueado
        // Todo, y sin tomar el FOR SHARE de un socio ajeno. AplicarAsync conserva su propia guarda (defensa en profundidad).
        if (factura.SocioNegocioId != pago.SocioNegocioId)
        {
            return Fallo(new Error(
                "clientes.socios_distintos",
                $"El pago {pago.NumeroDocumento} y el documento {factura.NumeroDocumento} son de clientes distintos: solo se aplica entre " +
                "movimientos del mismo cliente.",
                "MovimientoPagoId"));
        }

        var sociosIds = new[] { factura.SocioNegocioId };
        var socios = await datos.BloquearSociosAsync(sociosIds, cancellationToken);
        if (socios.Count != sociosIds.Length || socios.Any(s => s.Bloqueado == BloqueoSocioNegocio.Todo))
        {
            return Fallo(new Error(
                "cobro.socio_bloqueado",
                "El cliente de los movimientos no existe o está bloqueado para todo: no se le pueden aplicar cobros.",
                "MovimientoFacturaId"));
        }

        // Fechas de registro permitidas (Task 8.5): la fecha EFECTIVA de la aplicación (la indicada o la de hoy).
        var fechaRegistro = request.FechaRegistro ?? DateOnly.FromDateTime(DateTime.UtcNow);
        if (fechaRegistro < factura.FechaRegistro || fechaRegistro < pago.FechaRegistro)
        {
            return Fallo(new Error(
                "cobro.fecha_anterior_documentos",
                "La fecha de aplicación no puede ser anterior a la factura ni al pago.", "FechaRegistro"));
        }

        var fecha = await validadorFecha.ValidarAsync(fechaRegistro, cancellationToken);
        if (fecha.EsFallo)
        {
            return Fallo([.. fecha.Errores]);
        }

        // Both rows remain locked. For valid remaining amounts, validate and post the reclassification
        // before writing customer details; a blocked account therefore leaves both ledgers untouched.
        // Invalid signs/remaining amounts are delegated to the existing writer's detailed errors.
        if (factura.ImporteRestante > 0m && pago.ImporteRestante < 0m
            && request.Importe <= Math.Min(factura.ImporteRestante, -pago.ImporteRestante))
        {
            var reclasificacion = ReclasificacionCxC.Construir(factura, pago, request.Importe, fechaRegistro);
            if (reclasificacion is not null)
            {
                var asiento = await registroContable.RegistrarAsync(reclasificacion, cancellationToken);
                if (asiento.EsFallo)
                {
                    return Result<ResultadoAplicacionPago>.Fallo(asiento);
                }
            }
        }

        var aplicacion = await registroClientes.AplicarAsync(new AplicacionClienteSolicitud(
            factura.Id,
            pago.Id,
            request.Importe,
            fechaRegistro,
            TipoOrigenMovimiento.Cobro,
            pago.NumeroDocumento), cancellationToken);
        if (!aplicacion.TryObtenerValor(out var registrada))
        {
            return Result<ResultadoAplicacionPago>.Fallo(aplicacion);
        }

        await unitOfWork.CommitAsync(cancellationToken);

        return Result<ResultadoAplicacionPago>.Exito(new ResultadoAplicacionPago(
            factura.Id, pago.Id, request.Importe, registrada.RestanteFactura, registrada.RestantePago));
    }

    private static Error MovimientoInvalido(string campo) =>
        new("cobro.movimiento_invalido", "El movimiento de cliente indicado no existe.", campo);

    private static Result<ResultadoAplicacionPago> Fallo(params Error[] errores) => Result<ResultadoAplicacionPago>.Fallo(errores);
}
