using MediatR;
using OpenSource1.Application.Data;
using OpenSource1.Application.Data.UnitOfWork;
using OpenSource1.Application.Features.NotasCreditoVenta.Borradores.Commands;
using OpenSource1.Application.Features.NotasCreditoVenta.Borradores.Dtos;
using OpenSource1.Application.Services.Inventario;
using OpenSource1.Core.Common;
using OpenSource1.Core.Entities;
using OpenSource1.Core.Entities.Ventas;
using OpenSource1.Core.Enums;

namespace OpenSource1.Application.Features.NotasCreditoVenta.Borradores.Handlers;

/// <summary>
/// Alta de un borrador de nota de crédito desde una factura posteada (Task 8.6), en una transacción: la factura existe
/// (<c>nota_credito.factura_invalida</c>, 400: es una referencia del cuerpo), le queda algo por acreditar
/// (<c>nota_credito.factura_sin_pendiente</c>), sus socios siguen vivos (<c>FOR SHARE</c> antes de comprobarlo, contra su borrado
/// concurrente) y la fecha de registro no es anterior a la suya. Copia de la factura la cabecera y de su movimiento de cliente la
/// CxC congelada; con <c>CopiarLineas</c>, una línea por todo lo pendiente de cada línea acreditable. Número de la serie configurada
/// para <c>BorradorNotaCreditoVenta</c> (nunca elegible); serie de registro la configurada para <c>NotaCreditoVenta</c> o la que elija
/// el Administrador (<see cref="NumeracionBorrador.ValidarSeriesSolicitadas"/>, antes de la transacción: 400 <c>serie.no_editable</c>;
/// <see cref="NumeracionBorrador.NumerarBorradorAsync"/>: una serie de registro inexistente, de otro tipo o inactiva -&gt; 400 en su campo).
/// </summary>
public sealed class CreateNotaCreditoVentaBorradorCommandHandler(
    IUnitOfWork unitOfWork,
    IGeneradorNumeroDocumento generadorNumero,
    INotaCreditoVentaDatos datos,
    IConversionUnidadMedidaService conversion,
    INotaCreditoVentaBorradorReadRepository readRepository,
    IUsuarioActual usuario)
    : IRequestHandler<CreateNotaCreditoVentaBorradorCommand, Result<NotaCreditoVentaBorradorResponse>>
{
    public async Task<Result<NotaCreditoVentaBorradorResponse>> Handle(
        CreateNotaCreditoVentaBorradorCommand request, CancellationToken cancellationToken)
    {
        if (NumeracionBorrador.ValidarSeriesSolicitadas(request.SerieBorradorId, request.SerieRegistroId, usuario.EsAdministrador) is { } noEditable)
        {
            return Result<NotaCreditoVentaBorradorResponse>.Fallo(noEditable);
        }

        var descripcion = NotaCreditoVentaErrores.Normalizar(request.Descripcion);
        if (NotaCreditoVentaErrores.ValidarDescripcion(descripcion) is { } errorDescripcion)
        {
            return Fallo(errorDescripcion);
        }

        if (string.IsNullOrWhiteSpace(request.FacturaVentaNumero) || request.FacturaVentaNumero.Length > 20)
        {
            return Fallo(NotaCreditoVentaErrores.FacturaInvalida());
        }

        // Sin CommitAsync, salir del "await using" deshace la transacción (el número del borrador no se consume).
        await using var transaccion = await unitOfWork.BeginTransactionAsync(cancellationToken);

        var factura = await datos.ObtenerFacturaAsync(request.FacturaVentaNumero.Trim(), bloquear: false, cancellationToken);
        if (factura is null)
        {
            return Fallo(NotaCreditoVentaErrores.FacturaInvalida());
        }

        await datos.BloquearSociosAsync([factura.SocioNegocioId, factura.SocioNegocioFacturarAId], cancellationToken);
        var socios = unitOfWork.Repository<SocioNegocio>();
        foreach (var (socioId, campo) in new[] { (factura.SocioNegocioId, "SocioNegocioId"), (factura.SocioNegocioFacturarAId, "SocioNegocioFacturarAId") })
        {
            if (await socios.FirstOrDefaultAsync(x => x.Id == socioId, cancellationToken: cancellationToken) is null)
            {
                return Fallo(new Error("nota_credito.socio_invalido", "El socio de la factura ya no existe.", campo));
            }
        }

        var fechaRegistro = request.FechaRegistro ?? DateOnly.FromDateTime(DateTime.UtcNow);
        var fechaDocumento = request.FechaDocumento ?? fechaRegistro;
        if (fechaRegistro < factura.FechaRegistro)
        {
            return Fallo(NotaCreditoVentaErrores.FechaAnteriorAFactura(factura.FechaRegistro));
        }

        if (fechaDocumento == default)
        {
            return Fallo(new Error("nota_credito.fecha_invalida", "La fecha de documento no es válida.", "FechaDocumento"));
        }

        var lineasFactura = await datos.LineasFacturaAsync(factura.Numero, cancellationToken);
        var acreditadas = await datos.CantidadesAcreditadasAsync(factura.Numero, cancellationToken);
        var pendientes = lineasFactura
            .Where(l => l.Tipo != TipoLineaFactura.Comentario && l.Cantidad - acreditadas.GetValueOrDefault(l.Id) > 0m)
            .ToList();
        if (pendientes.Count == 0)
        {
            return Fallo(new Error(
                "nota_credito.factura_sin_pendiente",
                $"La factura {factura.Numero} ya está acreditada por completo: no le queda nada pendiente de acreditar.",
                "FacturaVentaNumero"));
        }

        var movimiento = await datos.MovimientoClienteFacturaAsync(factura.Numero, cancellationToken);

        // Series del borrador (spec alta rápida): la de borradores (siempre la configurada) da el número; la de registro numerará la
        // nota al postear.
        var series = await generadorNumero.NumerarBorradorAsync(
            TipoDocumentoSerie.BorradorNotaCreditoVenta, TipoDocumentoSerie.NotaCreditoVenta, request.SerieRegistroId,
            DateOnly.FromDateTime(DateTime.UtcNow), cancellationToken);
        if (!series.TryObtenerValor(out var numeradas))
        {
            return Result<NotaCreditoVentaBorradorResponse>.Fallo(series);
        }

        var entity = new NotaCreditoVentaBorrador
        {
            Numero = numeradas.Numero.Numero,
            SerieBorradorId = numeradas.SerieBorradorId,
            SerieRegistroId = numeradas.SerieRegistroId,
            Estado = EstadoNotaCreditoBorrador.Abierta,
            FacturaVentaNumero = factura.Numero,
            SocioNegocioId = factura.SocioNegocioId,
            SocioNegocioFacturarAId = factura.SocioNegocioFacturarAId,
            NombreFacturacion = factura.NombreFacturacion,
            RazonSocialFacturacion = factura.RazonSocialFacturacion,
            TipoDocumentoFiscal = factura.TipoDocumentoFiscal,
            NumeroDocumentoFiscal = factura.NumeroDocumentoFiscal,
            DireccionFacturacionLinea1 = factura.DireccionFacturacionLinea1,
            DireccionFacturacionLinea2 = factura.DireccionFacturacionLinea2,
            CiudadFacturacion = factura.CiudadFacturacion,
            PaisCodigoFacturacion = factura.PaisCodigoFacturacion,
            FechaRegistro = fechaRegistro,
            FechaDocumento = fechaDocumento,
            GrupoNegocioId = factura.GrupoNegocioId,
            GrupoIvaNegocioId = factura.GrupoIvaNegocioId,
            GrupoClienteContableId = factura.GrupoClienteContableId,
            CuentaCxCId = movimiento?.CuentaCxCId,
            Moneda = factura.Moneda,
            Descripcion = descripcion,
        };
        await unitOfWork.Repository<NotaCreditoVentaBorrador>().AddAsync(entity, cancellationToken);

        if (request.CopiarLineas)
        {
            var lineasRepo = unitOfWork.Repository<LineaNotaCreditoVentaBorrador>();
            var errores = new List<Error>();
            foreach (var original in pendientes)
            {
                var calculo = await LineaNotaCreditoVentaReglas.ValidarAsync(
                    unitOfWork, conversion, original, acreditadas.GetValueOrDefault(original.Id),
                    original.Cantidad - acreditadas.GetValueOrDefault(original.Id),
                    request.DevolverInventario && original.Tipo == TipoLineaFactura.Producto, cancellationToken);
                if (!calculo.TryObtenerValor(out var valores))
                {
                    errores.Add(NotaCreditoVentaErrores.DeLinea(original.NumeroLinea, calculo.Errores[0]));
                    continue;
                }

                var linea = new LineaNotaCreditoVentaBorrador { NotaCreditoVentaBorradorId = entity.Id };
                valores.Aplicar(linea);
                await lineasRepo.AddAsync(linea, cancellationToken);
            }

            if (errores.Count > 0)
            {
                return Result<NotaCreditoVentaBorradorResponse>.Fallo([.. errores]);
            }
        }

        await unitOfWork.CommitAsync(cancellationToken);

        return Result<NotaCreditoVentaBorradorResponse>.Exito((await readRepository.GetByIdAsync(entity.Id, cancellationToken))!);
    }

    private static Result<NotaCreditoVentaBorradorResponse> Fallo(Error error) => Result<NotaCreditoVentaBorradorResponse>.Fallo(error);
}
