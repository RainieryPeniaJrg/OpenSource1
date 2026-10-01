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
/// Modificación de la cabecera ("null = conservar", ver <see cref="UpdateFacturaVentaBorradorCommand"/>). Toma el
/// <c>FOR UPDATE</c> del borrador antes de leer, igual que las líneas: si cambia el grupo de IVA de negocio, el IVA congelado de
/// las líneas se recalcula en la misma transacción (un setup inexistente para alguna línea -&gt; 400
/// <c>Lineas[n].GrupoIvaProductoId</c> y no se guarda nada). Si cambia algún socio, los nuevos se bloquean <c>FOR SHARE</c>
/// (después del borrador) antes de validarlos. Solo el Administrador envía una serie de registro (si no, 400 <c>serie.no_editable</c>
/// antes de abrir la transacción); una nueva se valida con
/// <see cref="IGeneradorNumeroDocumento.ValidarSerieAsync"/> (tipo <c>FacturaVenta</c> y activa; error en <c>SerieRegistroId</c>).
/// Un borrador <c>Posteada</c> es de solo lectura (409).
/// </summary>
public sealed class UpdateFacturaVentaBorradorCommandHandler(
    IUnitOfWork unitOfWork,
    IFacturaVentaBorradorBloqueoService bloqueo,
    IDerivadorCuentas derivador,
    IFacturaVentaBorradorReadRepository readRepository,
    IFacturaVentaBorradorDatos borradorDatos,
    IGeneradorNumeroDocumento generadorNumero,
    IUsuarioActual usuario)
    : IRequestHandler<UpdateFacturaVentaBorradorCommand, Result<FacturaVentaBorradorResponse>>
{
    public async Task<Result<FacturaVentaBorradorResponse>> Handle(UpdateFacturaVentaBorradorCommand request, CancellationToken cancellationToken)
    {
        if (NumeracionBorrador.ValidarSeriesSolicitadas(null, request.SerieRegistroId, usuario.EsAdministrador) is { } noEditable)
        {
            return Result<FacturaVentaBorradorResponse>.Fallo(noEditable);
        }

        await using var transaccion = await unitOfWork.BeginTransactionAsync(cancellationToken);

        var estado = await bloqueo.BloquearYObtenerEstadoAsync(request.Id, cancellationToken);
        if (estado is null)
        {
            return Result<FacturaVentaBorradorResponse>.Fallo(FacturaVentaBorradorErrores.BorradorNoEncontrado());
        }

        if (estado == EstadoFacturaBorrador.Posteada)
        {
            return Result<FacturaVentaBorradorResponse>.Fallo(FacturaVentaBorradorErrores.Posteada());
        }

        if (estado == EstadoFacturaBorrador.Liberada)
        {
            return Result<FacturaVentaBorradorResponse>.Fallo(FacturaVentaBorradorErrores.Liberada());
        }

        var repository = unitOfWork.Repository<FacturaVentaBorrador>();
        var entity = await repository.FirstOrDefaultAsync(x => x.Id == request.Id, asTracking: true, cancellationToken: cancellationToken);
        if (entity is null)
        {
            return Result<FacturaVentaBorradorResponse>.Fallo(FacturaVentaBorradorErrores.BorradorNoEncontrado());
        }

        // Descripción: null = conservar; "" = limpiar.
        var descripcion = request.Descripcion is null ? entity.Descripcion : FacturaVentaBorradorReglas.Normalizar(request.Descripcion);
        if (FacturaVentaBorradorReglas.ValidarDescripcion(descripcion) is { } errorDescripcion)
        {
            return Result<FacturaVentaBorradorResponse>.Fallo(errorDescripcion);
        }

        // Socios: si cambia cualquiera de los dos, se vuelve a tomar el snapshot completo (datos, término y grupos).
        var venderAId = request.SocioNegocioId ?? entity.SocioNegocioId;
        var facturarAId = request.SocioNegocioFacturarAId ?? entity.SocioNegocioFacturarAId;
        var terminoAnterior = entity.TerminoPagoId;
        var grupoIvaNegocioAnterior = entity.GrupoIvaNegocioId;
        if (venderAId != entity.SocioNegocioId || facturarAId != entity.SocioNegocioFacturarAId)
        {
            // Orden de locks: borrador (ya tomado) → socios (FOR SHARE) antes de validarlos, contra su borrado concurrente.
            await borradorDatos.BloquearSociosAsync([venderAId, facturarAId], cancellationToken);
            var snapshot = await FacturaVentaBorradorReglas.TomarSnapshotAsync(unitOfWork, venderAId, facturarAId, cancellationToken);
            if (!snapshot.TryObtenerValor(out var socios))
            {
                return Result<FacturaVentaBorradorResponse>.Fallo(snapshot);
            }

            socios.Aplicar(entity);
        }

        // Fechas: el vencimiento se recalcula si no se envía y cambió la fecha de documento o el término.
        var fechaRegistro = request.FechaRegistro ?? entity.FechaRegistro;
        var fechaDocumento = request.FechaDocumento ?? entity.FechaDocumento;
        var fechaVencimiento = request.FechaVencimiento
            ?? (fechaDocumento != entity.FechaDocumento || entity.TerminoPagoId != terminoAnterior
                ? await FacturaVentaBorradorReglas.CalcularVencimientoAsync(unitOfWork, fechaDocumento, entity.TerminoPagoId, cancellationToken)
                : entity.FechaVencimiento);
        if (FacturaVentaBorradorReglas.ValidarFechas(fechaRegistro, fechaDocumento, fechaVencimiento) is { } errorFecha)
        {
            return Result<FacturaVentaBorradorResponse>.Fallo(errorFecha);
        }

        if (request.AlmacenId is { } almacenSolicitado && almacenSolicitado != entity.AlmacenId)
        {
            var almacen = await FacturaVentaBorradorReglas.ResolverAlmacenAsync(unitOfWork, almacenSolicitado, cancellationToken);
            if (!almacen.TryObtenerValor(out var almacenId))
            {
                return Result<FacturaVentaBorradorResponse>.Fallo(almacen);
            }

            entity.AlmacenId = almacenId;
        }

        if (entity.GrupoIvaNegocioId != grupoIvaNegocioAnterior)
        {
            var recalculo = await RecalcularIvaLineasAsync(entity, cancellationToken);
            if (recalculo.EsFallo)
            {
                return Result<FacturaVentaBorradorResponse>.Fallo(recalculo);
            }
        }

        // Serie de registro: null = conservar; una nueva se valida (no numera: la factura se numera al postear).
        if (request.SerieRegistroId is { } serieRegistroId && serieRegistroId != entity.SerieRegistroId)
        {
            var valida = await generadorNumero.ValidarSerieAsync(serieRegistroId, TipoDocumentoSerie.FacturaVenta, cancellationToken);
            if (valida.EsFallo)
            {
                return Result<FacturaVentaBorradorResponse>.Fallo(valida.Errores[0] with { Campo = "SerieRegistroId" });
            }

            entity.SerieRegistroId = serieRegistroId;
        }

        repository.EstablecerVersionOriginal(entity, request.Xmin);
        entity.FechaRegistro = fechaRegistro;
        entity.FechaDocumento = fechaDocumento;
        entity.FechaVencimiento = fechaVencimiento;
        entity.Descripcion = descripcion;

        repository.Update(entity);
        await unitOfWork.CommitAsync(cancellationToken);

        return Result<FacturaVentaBorradorResponse>.Exito((await readRepository.GetByIdAsync(entity.Id, cancellationToken))!);
    }

    private async Task<Result> RecalcularIvaLineasAsync(FacturaVentaBorrador cabecera, CancellationToken cancellationToken)
    {
        var lineasRepo = unitOfWork.Repository<LineaFacturaVentaBorrador>();
        var lineas = await lineasRepo.ListAsync(
            x => x.FacturaVentaBorradorId == cabecera.Id && x.Tipo != TipoLineaFactura.Comentario, cancellationToken);

        foreach (var lectura in lineas.OrderBy(x => x.NumeroLinea))
        {
            var iva = await derivador.IvaAsync(cabecera.GrupoIvaNegocioId, lectura.GrupoIvaProductoId, cancellationToken);
            if (!iva.TryObtenerValor(out var setup))
            {
                var original = iva.Errores[0];
                return Result.Fallo(new Error(
                    original.Codigo, $"Línea {lectura.NumeroLinea}: {original.Mensaje}", $"Lineas[{lectura.NumeroLinea}].GrupoIvaProductoId"));
            }

            // FindAsync: entidad rastreada (con su xmin real) para que el UPDATE no falle por concurrencia.
            var linea = await lineasRepo.GetByIdAsync([lectura.Id], cancellationToken);
            linea!.IdentificadorIva = setup.IdentificadorIva;
            linea.PorcentajeIva = setup.PorcentajeIva;
            lineasRepo.Update(linea);
        }

        return Result.Exito();
    }
}
