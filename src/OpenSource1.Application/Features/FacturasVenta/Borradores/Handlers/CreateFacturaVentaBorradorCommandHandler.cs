using MediatR;
using OpenSource1.Application.Data;
using OpenSource1.Application.Data.UnitOfWork;
using OpenSource1.Application.Features.FacturasVenta.Borradores.Commands;
using OpenSource1.Application.Features.FacturasVenta.Borradores.Dtos;
using OpenSource1.Application.Services.Inventario;
using OpenSource1.Core.Common;
using OpenSource1.Core.Entities.Ventas;
using OpenSource1.Core.Enums;

namespace OpenSource1.Application.Features.FacturasVenta.Borradores.Handlers;

/// <summary>
/// Alta de un borrador: snapshot del facturar-a y grupos congelados (<see cref="FacturaVentaBorradorReglas.TomarSnapshotAsync"/>),
/// vencimiento por el término de pago, almacén predeterminado, moneda DOP y número de la serie configurada para
/// <c>BorradorFacturaVenta</c> (nunca elegible), con la serie de registro configurada para <c>FacturaVenta</c> o la que elija el
/// Administrador (<see cref="NumeracionBorrador.ValidarSeriesSolicitadas"/>, antes de la transacción: 400 <c>serie.no_editable</c>), validada sin numerar
/// (<see cref="NumeracionBorrador.NumerarBorradorAsync"/>, dentro de la transacción, que <see cref="IGeneradorNumeroDocumento"/> exige;
/// errores de serie en <c>SerieBorradorId</c>/<c>SerieRegistroId</c>, sin consumir número). Los socios se bloquean <c>FOR SHARE</c> dentro de la transacción
/// ANTES de validarlos: un borrado concurrente del socio no puede dejar un borrador de un socio borrado.
/// </summary>
public sealed class CreateFacturaVentaBorradorCommandHandler(
    IUnitOfWork unitOfWork,
    IGeneradorNumeroDocumento generadorNumero,
    IFacturaVentaBorradorReadRepository readRepository,
    IFacturaVentaBorradorDatos borradorDatos,
    IUsuarioActual usuario)
    : IRequestHandler<CreateFacturaVentaBorradorCommand, Result<FacturaVentaBorradorResponse>>
{
    public async Task<Result<FacturaVentaBorradorResponse>> Handle(CreateFacturaVentaBorradorCommand request, CancellationToken cancellationToken)
    {
        if (NumeracionBorrador.ValidarSeriesSolicitadas(request.SerieBorradorId, request.SerieRegistroId, usuario.EsAdministrador) is { } noEditable)
        {
            return Result<FacturaVentaBorradorResponse>.Fallo(noEditable);
        }

        var descripcion = FacturaVentaBorradorReglas.Normalizar(request.Descripcion);
        if (FacturaVentaBorradorReglas.ValidarDescripcion(descripcion) is { } errorDescripcion)
        {
            return Result<FacturaVentaBorradorResponse>.Fallo(errorDescripcion);
        }

        // Sin CommitAsync, salir del "await using" deshace la transacción (el número del borrador no se consume).
        await using var transaccion = await unitOfWork.BeginTransactionAsync(cancellationToken);

        // Socios (FOR SHARE) antes de validarlos: el borrado del socio espera a este commit o este alta ve el socio borrado.
        var facturarAId = request.SocioNegocioFacturarAId ?? request.SocioNegocioId;
        await borradorDatos.BloquearSociosAsync([request.SocioNegocioId, facturarAId], cancellationToken);
        var snapshot = await FacturaVentaBorradorReglas.TomarSnapshotAsync(
            unitOfWork, request.SocioNegocioId, facturarAId, cancellationToken);
        if (!snapshot.TryObtenerValor(out var socios))
        {
            return Result<FacturaVentaBorradorResponse>.Fallo(snapshot);
        }

        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
        var fechaRegistro = request.FechaRegistro ?? hoy;
        var fechaDocumento = request.FechaDocumento ?? fechaRegistro;
        var fechaVencimiento = request.FechaVencimiento
            ?? await FacturaVentaBorradorReglas.CalcularVencimientoAsync(unitOfWork, fechaDocumento, socios.TerminoPagoId, cancellationToken);
        if (FacturaVentaBorradorReglas.ValidarFechas(fechaRegistro, fechaDocumento, fechaVencimiento) is { } errorFecha)
        {
            return Result<FacturaVentaBorradorResponse>.Fallo(errorFecha);
        }

        var almacen = await FacturaVentaBorradorReglas.ResolverAlmacenAsync(unitOfWork, request.AlmacenId, cancellationToken);
        if (!almacen.TryObtenerValor(out var almacenId))
        {
            return Result<FacturaVentaBorradorResponse>.Fallo(almacen);
        }

        // Series del borrador (spec alta rápida): la de borradores es siempre la configurada y da el número; la de registro (la
        // configurada o la del Administrador) solo se valida (numerará la factura al postear).
        var series = await generadorNumero.NumerarBorradorAsync(
            TipoDocumentoSerie.BorradorFacturaVenta, TipoDocumentoSerie.FacturaVenta, request.SerieRegistroId, hoy, cancellationToken);
        if (!series.TryObtenerValor(out var numeradas))
        {
            return Result<FacturaVentaBorradorResponse>.Fallo(series);
        }

        var entity = new FacturaVentaBorrador
        {
            Numero = numeradas.Numero.Numero,
            SerieBorradorId = numeradas.SerieBorradorId,
            SerieRegistroId = numeradas.SerieRegistroId,
            NombreFacturacion = socios.NombreFacturacion,
            FechaRegistro = fechaRegistro,
            FechaDocumento = fechaDocumento,
            FechaVencimiento = fechaVencimiento,
            AlmacenId = almacenId,
            Estado = EstadoFacturaBorrador.Abierta,
            Moneda = SerieFacturaVentaIds.MonedaPorDefecto,
            Descripcion = descripcion
        };
        socios.Aplicar(entity);

        await unitOfWork.Repository<FacturaVentaBorrador>().AddAsync(entity, cancellationToken);
        await unitOfWork.CommitAsync(cancellationToken);

        return Result<FacturaVentaBorradorResponse>.Exito((await readRepository.GetByIdAsync(entity.Id, cancellationToken))!);
    }
}
