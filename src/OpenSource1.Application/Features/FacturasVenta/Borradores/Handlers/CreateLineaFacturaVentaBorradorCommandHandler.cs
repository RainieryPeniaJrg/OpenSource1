using MediatR;
using OpenSource1.Application.Data.UnitOfWork;
using OpenSource1.Application.Features.FacturasVenta.Borradores.Commands;
using OpenSource1.Application.Features.FacturasVenta.Borradores.Dtos;
using OpenSource1.Application.Services.Contabilidad;
using OpenSource1.Application.Services.Inventario;
using OpenSource1.Core.Common;
using OpenSource1.Core.Entities.Ventas;
using OpenSource1.Core.Enums;

namespace OpenSource1.Application.Features.FacturasVenta.Borradores.Handlers;

/// <summary>
/// Alta de una línea. Mismo esquema que <c>CreateLineaDiarioCommandHandler</c>: transacción + <c>FOR UPDATE</c> del borrador
/// ANTES de calcular <c>NumeroLinea</c> (máximo + 10000) y comprobar el tope, para que dos altas concurrentes se serialicen.
/// El borrador viene de la RUTA: inexistente -&gt; 404; liberado -&gt; 400 <c>factura.liberada</c>.
/// </summary>
public sealed class CreateLineaFacturaVentaBorradorCommandHandler(
    IUnitOfWork unitOfWork,
    IFacturaVentaBorradorBloqueoService bloqueo,
    IConversionUnidadMedidaService conversion,
    IDerivadorCuentas derivador,
    ILineaFacturaVentaBorradorReadRepository readRepository)
    : IRequestHandler<CreateLineaFacturaVentaBorradorCommand, Result<LineaFacturaVentaBorradorResponse>>
{
    /// <summary>Tope de líneas por borrador (mismo criterio que los lotes de diario).</summary>
    public const int MaximoLineasPorBorrador = 1000;

    public async Task<Result<LineaFacturaVentaBorradorResponse>> Handle(
        CreateLineaFacturaVentaBorradorCommand request, CancellationToken cancellationToken)
    {
        await using var transaccion = await unitOfWork.BeginTransactionAsync(cancellationToken);

        var estado = await bloqueo.BloquearYObtenerEstadoAsync(request.FacturaVentaBorradorId, cancellationToken);
        if (estado is null)
        {
            return Result<LineaFacturaVentaBorradorResponse>.Fallo(
                FacturaVentaBorradorErrores.BorradorNoEncontrado("FacturaVentaBorradorId"));
        }

        if (estado == EstadoFacturaBorrador.Posteada)
        {
            return Result<LineaFacturaVentaBorradorResponse>.Fallo(FacturaVentaBorradorErrores.Posteada("FacturaVentaBorradorId"));
        }

        if (estado == EstadoFacturaBorrador.Liberada)
        {
            return Result<LineaFacturaVentaBorradorResponse>.Fallo(FacturaVentaBorradorErrores.Liberada("FacturaVentaBorradorId"));
        }

        var cabecera = await unitOfWork.Repository<FacturaVentaBorrador>().FirstOrDefaultAsync(
            x => x.Id == request.FacturaVentaBorradorId, cancellationToken: cancellationToken);

        var datos = new LineaFacturaDatos(
            request.Tipo, request.ProductoId, request.CuentaContableId, request.Descripcion, request.AlmacenId, request.UnidadMedidaId,
            request.Cantidad, request.PrecioUnitario, request.PorcentajeDescuentoLinea, request.GrupoIvaProductoId);
        var alta = await LineaFacturaVentaBorradorReglas.AgregarAsync(unitOfWork, conversion, derivador, cabecera!, datos, cancellationToken);
        if (!alta.TryObtenerValor(out var entity))
        {
            return Result<LineaFacturaVentaBorradorResponse>.Fallo(alta);
        }

        await unitOfWork.CommitAsync(cancellationToken);

        return Result<LineaFacturaVentaBorradorResponse>.Exito((await readRepository.GetByIdAsync(entity.Id, cancellationToken))!);
    }
}
