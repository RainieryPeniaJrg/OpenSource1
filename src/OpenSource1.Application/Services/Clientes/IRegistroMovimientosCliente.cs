using OpenSource1.Core.Common;
using OpenSource1.Core.Enums;

namespace OpenSource1.Application.Services.Clientes;

/// <summary>
/// Petición de alta de un movimiento del libro de clientes (spec 6.4) con su detalle inicial.
/// </summary>
/// <param name="SocioNegocioId">El cliente del movimiento: en una factura, el facturar-a (desviaciones de la Fase 6).</param>
/// <param name="ImporteOriginal">Con signo: + factura (debe el cliente), − pago/nota de crédito. Distinto de 0, 4 decimales como máximo.</param>
/// <param name="CuentaCxCId">Cuenta por cobrar YA derivada (<c>IDerivadorCuentas.CuentaCxCAsync</c>) y congelada en el movimiento.</param>
public sealed record MovimientoClienteSolicitud(
    Guid SocioNegocioId,
    DateOnly FechaRegistro,
    DateOnly FechaDocumento,
    DateOnly FechaVencimiento,
    TipoDocumentoCliente TipoDocumento,
    string NumeroDocumento,
    string? Descripcion,
    decimal ImporteOriginal,
    Guid GrupoClienteContableId,
    Guid CuentaCxCId,
    TipoOrigenMovimiento TipoOrigen,
    string ClaveOrigen);

/// <summary>Ids escritos: el movimiento y su fila de detalle inicial.</summary>
public sealed record MovimientoClienteRegistrado(long MovimientoClienteId, long DetalleId);

/// <summary>
/// Movimiento de cliente bloqueado <c>FOR UPDATE</c> con su importe restante (<c>Σ detalle.Importe</c>) leído DESPUÉS de obtener
/// el bloqueo. La fecha y la cuenta CxC congelada permiten validar la fecha de aplicación y reclasificar
/// cuentas distintas sin depender del grupo vigente del cliente.
/// </summary>
public sealed record MovimientoClienteBloqueado(
    // Original: long Id, Guid SocioNegocioId, TipoDocumentoCliente TipoDocumento, string NumeroDocumento, decimal ImporteRestante);
    long Id, Guid SocioNegocioId, TipoDocumentoCliente TipoDocumento, string NumeroDocumento, decimal ImporteRestante,
    DateOnly FechaRegistro = default, Guid CuentaCxCId = default);

/// <summary>
/// Aplicación de un movimiento de restante negativo (pago o nota de crédito) a uno de restante positivo (factura), spec 6.4 y
/// 6.6: dos filas de detalle <see cref="TipoDetalleCliente.Aplicacion"/>, −<paramref name="Importe"/> en la factura y
/// +<paramref name="Importe"/> en el pago, cada una apuntando a la otra con <c>MovimientoClienteAplicadoId</c>.
/// </summary>
/// <param name="Importe">&gt; 0, 4 decimales como máximo y ≤ min(restante de la factura, −restante del pago).</param>
public sealed record AplicacionClienteSolicitud(
    long MovimientoFacturaId,
    long MovimientoPagoId,
    decimal Importe,
    DateOnly FechaRegistro,
    TipoOrigenMovimiento TipoOrigen,
    string ClaveOrigen);

/// <summary>Filas de detalle escritas y los restantes de los dos movimientos después de la aplicación.</summary>
public sealed record AplicacionClienteRegistrada(
    long DetalleFacturaId, long DetallePagoId, decimal RestanteFactura, decimal RestantePago);

/// <summary>
/// Escritor ÚNICO del libro de clientes (<c>MovimientosCliente</c> + <c>MovimientosClienteDetalle</c>), mismo papel que
/// <c>IRegistroMovimientosInventario</c> en inventario e <c>IRegistroContable</c> en contabilidad. Solo hace <c>INSERT</c> (el
/// libro es append-only). La facturación (Task 6.4) registra facturas y los cobros (Task 6.5) registran pagos (con
/// <see cref="RegistrarAsync"/>, detalle inicial <see cref="TipoDetalleCliente.Pago"/>) y aplicaciones (<see cref="AplicarAsync"/>).
/// <para>
/// Bloqueos: <see cref="BloquearMovimientosAsync"/> y <see cref="AplicarAsync"/> toman los movimientos <c>FOR UPDATE</c> en orden
/// de Id. En el orden global de locks ocupan el lugar del "documento" (el primero): una aplicación bloquea sus dos movimientos y
/// después, como mucho, los socios (<c>FOR SHARE</c>). Nadie más bloquea movimientos existentes: el posteo de facturas y el pago
/// solo INSERTAN movimientos nuevos (sin esperar a ninguno existente), así que no hay ciclo posible.
/// </para>
/// </summary>
public interface IRegistroMovimientosCliente
{
    /// <summary>
    /// Inserta el movimiento y su detalle inicial: <see cref="TipoDetalleCliente.Pago"/> si el documento es un pago y
    /// <see cref="TipoDetalleCliente.ImporteInicial"/> en los demás, con <c>Importe = ImporteOriginal</c>. Requiere transacción
    /// activa (<c>clientes.sin_transaccion</c> si no la hay) y no hace commit. Un resultado fallido no escribe ninguna fila (las
    /// validaciones van antes de cualquier INSERT).
    /// </summary>
    Task<Result<MovimientoClienteRegistrado>> RegistrarAsync(MovimientoClienteSolicitud solicitud, CancellationToken ct = default);

    /// <summary>
    /// Bloquea <c>FOR UPDATE</c>, en orden de Id y sin repetir, los movimientos que existan de <paramref name="ids"/> y devuelve
    /// cada uno con su restante, calculado en una sentencia POSTERIOR al bloqueo (en READ COMMITTED ve así el detalle confirmado
    /// por quien tuviera el bloqueo antes). Los Ids inexistentes no aparecen en el resultado. Requiere transacción activa
    /// (lanza <see cref="InvalidOperationException"/> si no la hay).
    /// </summary>
    Task<IReadOnlyList<MovimientoClienteBloqueado>> BloquearMovimientosAsync(IEnumerable<long> ids, CancellationToken ct = default);

    /// <summary>
    /// Aplica un pago a una factura (<see cref="AplicacionClienteSolicitud"/>). Bloquea los dos movimientos
    /// (<see cref="BloquearMovimientosAsync"/>) y valida, con los restantes posteriores al bloqueo y ANTES de cualquier INSERT:
    /// ambos existen (<c>clientes.movimiento_inexistente</c>), son del mismo socio (<c>clientes.socios_distintos</c>), la factura
    /// tiene restante &gt; 0 y el pago &lt; 0 (<c>clientes.movimiento_sin_restante</c>) y el importe no excede ninguno de los dos
    /// (<c>clientes.importe_excede_restante</c>). Así dos aplicaciones concurrentes sobre el mismo movimiento se serializan y la
    /// segunda ve lo que aplicó la primera: nunca se aplica más que el restante. Requiere transacción activa y no hace commit; no
    /// genera asiento (el importe ya está en el libro contable desde el pago y la factura).
    /// </summary>
    Task<Result<AplicacionClienteRegistrada>> AplicarAsync(AplicacionClienteSolicitud solicitud, CancellationToken ct = default);
}
