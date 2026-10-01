using MediatR;
using OpenSource1.Application.Data;
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
/// Alta de un borrador (spec alta rápida): series según <see cref="NumeracionBorrador.ValidarSeriesSolicitadas"/> (antes de abrir la
/// transacción); snapshot del facturar-a y grupos congelados (<see cref="FacturaVentaBorradorReglas.TomarSnapshotAsync"/>), con los
/// socios bloqueados <c>FOR SHARE</c> ANTES de validarlos; vencimiento por el término de pago; almacén SIEMPRE el predeterminado
/// (otro -&gt; 400 <c>almacen.no_editable</c>); moneda DOP; número de la serie de borradores configurada y serie de registro configurada o
/// la del Administrador (<see cref="NumeracionBorrador.NumerarBorradorAsync"/>). Con <c>PrimeraLinea</c>, la línea se valida y se añade
/// con <see cref="LineaFacturaVentaBorradorReglas.AgregarAsync"/> en la MISMA transacción: si falla, no se hace commit y no queda ni el
/// borrador ni el número consumido.
/// </summary>
public sealed class CreateFacturaVentaBorradorCommandHandler(
    IUnitOfWork unitOfWork,
    IGeneradorNumeroDocumento generadorNumero,
    IFacturaVentaBorradorReadRepository readRepository,
    IFacturaVentaBorradorDatos borradorDatos,
    IConversionUnidadMedidaService conversion,
    IDerivadorCuentas derivador,
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

        // Almacén: siempre el predeterminado (spec alta rápida); uno distinto se rechaza ANTES de numerar.
        var almacen = await FacturaVentaBorradorReglas.ResolverAlmacenAsync(unitOfWork, null, cancellationToken);
        if (!almacen.TryObtenerValor(out var almacenId))
        {
            return Result<FacturaVentaBorradorResponse>.Fallo(almacen);
        }

        if (request.AlmacenId is { } almacenSolicitado && almacenSolicitado != almacenId)
        {
            return Result<FacturaVentaBorradorResponse>.Fallo(FacturaVentaBorradorErrores.AlmacenNoEditable());
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

        if (request.PrimeraLinea is { } primera)
        {
            var linea = await LineaFacturaVentaBorradorReglas.AgregarAsync(
                unitOfWork, conversion, derivador, entity, primera.ADatos(), cancellationToken);
            if (linea.EsFallo)
            {
                // Sin commit: la cabecera, la línea y el número del borrador se deshacen con la transacción.
                return Result<FacturaVentaBorradorResponse>.Fallo(
                    [.. linea.Errores.Select(e => e with { Campo = e.Campo is null ? "PrimeraLinea" : $"PrimeraLinea.{e.Campo}" })]);
            }
        }

        await unitOfWork.CommitAsync(cancellationToken);

        return Result<FacturaVentaBorradorResponse>.Exito((await readRepository.GetByIdAsync(entity.Id, cancellationToken))!);
    }
}
