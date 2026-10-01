using MediatR;
using OpenSource1.Application.Data;
using OpenSource1.Application.Data.UnitOfWork;
using OpenSource1.Application.Features.NotasCreditoVenta.Borradores.Commands;
using OpenSource1.Application.Features.NotasCreditoVenta.Borradores.Dtos;
using OpenSource1.Application.Services.Inventario;
using OpenSource1.Core.Common;
using OpenSource1.Core.Entities.Ventas;
using OpenSource1.Core.Enums;

namespace OpenSource1.Application.Features.NotasCreditoVenta.Borradores.Handlers;

/// <summary>
/// Modificación de las fechas, la descripción y la serie de registro bajo el <c>FOR UPDATE</c> del borrador. Inexistente -&gt; 404;
/// Posteada -&gt; 409 (<c>nota_credito_borrador.posteada.conflicto</c>); <c>Xmin</c> desactualizado -&gt; 409; fecha de registro
/// anterior a la de la factura -&gt; 400; serie de registro enviada por quien no es Administrador -&gt; 400 <c>serie.no_editable</c>
/// (antes de abrir la transacción); serie de registro nueva inexistente, de otro tipo o inactiva -&gt; 400 en <c>SerieRegistroId</c>.
/// </summary>
public sealed class UpdateNotaCreditoVentaBorradorCommandHandler(
    IUnitOfWork unitOfWork,
    INotaCreditoVentaDatos datos,
    IGeneradorNumeroDocumento generadorNumero,
    INotaCreditoVentaBorradorReadRepository readRepository,
    IUsuarioActual usuario)
    : IRequestHandler<UpdateNotaCreditoVentaBorradorCommand, Result<NotaCreditoVentaBorradorResponse>>
{
    public async Task<Result<NotaCreditoVentaBorradorResponse>> Handle(
        UpdateNotaCreditoVentaBorradorCommand request, CancellationToken cancellationToken)
    {
        if (NumeracionBorrador.ValidarSeriesSolicitadas(null, request.SerieRegistroId, usuario.EsAdministrador) is { } noEditable)
        {
            return Result<NotaCreditoVentaBorradorResponse>.Fallo(noEditable);
        }

        string? descripcion = null;
        if (request.Descripcion is not null)
        {
            descripcion = NotaCreditoVentaErrores.Normalizar(request.Descripcion);
            if (NotaCreditoVentaErrores.ValidarDescripcion(descripcion) is { } errorDescripcion)
            {
                return Result<NotaCreditoVentaBorradorResponse>.Fallo(errorDescripcion);
            }
        }

        await using var transaccion = await unitOfWork.BeginTransactionAsync(cancellationToken);

        var estado = await datos.BloquearBorradorAsync(request.Id, cancellationToken);
        if (estado is null)
        {
            return Result<NotaCreditoVentaBorradorResponse>.Fallo(NotaCreditoVentaErrores.BorradorNoEncontrado());
        }

        if (estado == EstadoNotaCreditoBorrador.Posteada)
        {
            return Result<NotaCreditoVentaBorradorResponse>.Fallo(NotaCreditoVentaErrores.Posteada());
        }

        var repository = unitOfWork.Repository<NotaCreditoVentaBorrador>();
        var entity = (await repository.FirstOrDefaultAsync(x => x.Id == request.Id, asTracking: true, cancellationToken: cancellationToken))!;

        var fechaRegistro = request.FechaRegistro ?? entity.FechaRegistro;
        var fechaDocumento = request.FechaDocumento ?? entity.FechaDocumento;
        var factura = await datos.ObtenerFacturaAsync(entity.FacturaVentaNumero, bloquear: false, cancellationToken);
        if (factura is not null && fechaRegistro < factura.FechaRegistro)
        {
            return Result<NotaCreditoVentaBorradorResponse>.Fallo(NotaCreditoVentaErrores.FechaAnteriorAFactura(factura.FechaRegistro));
        }

        if (fechaDocumento == default)
        {
            return Result<NotaCreditoVentaBorradorResponse>.Fallo(
                new Error("nota_credito.fecha_invalida", "La fecha de documento no es válida.", "FechaDocumento"));
        }

        if (request.SerieRegistroId is { } serieRegistroId && serieRegistroId != entity.SerieRegistroId)
        {
            var valida = await generadorNumero.ValidarSerieAsync(serieRegistroId, TipoDocumentoSerie.NotaCreditoVenta, cancellationToken);
            if (valida.EsFallo)
            {
                return Result<NotaCreditoVentaBorradorResponse>.Fallo(valida.Errores[0] with { Campo = "SerieRegistroId" });
            }

            entity.SerieRegistroId = serieRegistroId;
        }

        repository.EstablecerVersionOriginal(entity, request.Xmin);
        entity.FechaRegistro = fechaRegistro;
        entity.FechaDocumento = fechaDocumento;
        if (request.Descripcion is not null)
        {
            entity.Descripcion = descripcion;
        }

        repository.Update(entity);
        await unitOfWork.CommitAsync(cancellationToken);

        return Result<NotaCreditoVentaBorradorResponse>.Exito((await readRepository.GetByIdAsync(entity.Id, cancellationToken))!);
    }
}
