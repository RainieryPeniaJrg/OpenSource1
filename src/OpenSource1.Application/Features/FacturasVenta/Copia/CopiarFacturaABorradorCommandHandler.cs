using MediatR;
using OpenSource1.Application.Data;
using OpenSource1.Application.Data.UnitOfWork;
using OpenSource1.Application.Features.FacturasVenta.Borradores;
using OpenSource1.Application.Features.NotasCreditoVenta;
using OpenSource1.Application.Services.Contabilidad;
using OpenSource1.Application.Services.Inventario;
using OpenSource1.Core.Common;
using OpenSource1.Core.Entities;
using OpenSource1.Core.Entities.Ventas;
using OpenSource1.Core.Enums;

namespace OpenSource1.Application.Features.FacturasVenta.Copia;

/// <summary>
/// UNA transacción: socios <c>FOR SHARE</c> (contra su borrado concurrente) → snapshot actual → almacén → series configuradas y
/// número del borrador (<see cref="NumeracionBorrador.NumerarBorradorAsync"/>, FOR UPDATE de su línea) → líneas. Cualquier fallo
/// devuelve el error sin commit: no se crea nada ni se consume número.
/// </summary>
public sealed class CopiarFacturaABorradorCommandHandler(
    IUnitOfWork unitOfWork,
    IGeneradorNumeroDocumento generadorNumero,
    INotaCreditoVentaDatos facturas,
    IFacturaVentaBorradorDatos borradorDatos,
    IConversionUnidadMedidaService conversion,
    IDerivadorCuentas derivador)
    : IRequestHandler<CopiarFacturaABorradorCommand, Result<CopiaFacturaResponse>>
{
    public async Task<Result<CopiaFacturaResponse>> Handle(CopiarFacturaABorradorCommand request, CancellationToken cancellationToken)
    {
        var numeroFactura = request.FacturaVentaNumero?.Trim() ?? string.Empty;
        if (numeroFactura.Length is 0 or > 20)
        {
            return Fallo(NoEncontrada(numeroFactura));
        }

        // Sin CommitAsync, salir del "await using" deshace la transacción (el número del borrador no se consume).
        await using var transaccion = await unitOfWork.BeginTransactionAsync(cancellationToken);

        var factura = await facturas.ObtenerFacturaAsync(numeroFactura, bloquear: false, cancellationToken);
        if (factura is null)
        {
            return Fallo(NoEncontrada(numeroFactura));
        }

        // Socios (FOR SHARE) antes de validarlos: "cliente bloqueado" = cualquier bloqueo (como el posteo).
        await borradorDatos.BloquearSociosAsync([factura.SocioNegocioId, factura.SocioNegocioFacturarAId], cancellationToken);
        var socios = unitOfWork.Repository<SocioNegocio>();
        foreach (var (socioId, campo) in new[] { (factura.SocioNegocioId, "SocioNegocioId"), (factura.SocioNegocioFacturarAId, "SocioNegocioFacturarAId") })
        {
            var socio = await socios.FirstOrDefaultAsync(x => x.Id == socioId, cancellationToken: cancellationToken);
            if (socio is null || socio.Bloqueado != BloqueoSocioNegocio.Ninguno)
            {
                return Fallo(new Error(
                    "factura.copia_cliente_invalido",
                    socio is null
                        ? $"El cliente de la factura {factura.Numero} ya no existe: no se puede copiar la factura."
                        : $"El cliente {socio.Codigo} de la factura {factura.Numero} está bloqueado: no se puede copiar la factura.",
                    campo));
            }
        }

        var snapshot = await FacturaVentaBorradorReglas.TomarSnapshotAsync(
            unitOfWork, factura.SocioNegocioId, factura.SocioNegocioFacturarAId, cancellationToken);
        if (!snapshot.TryObtenerValor(out var datosSocios))
        {
            return Result<CopiaFacturaResponse>.Fallo(snapshot);
        }

        var avisos = new List<string>();
        var almacen = await FacturaVentaBorradorReglas.ResolverAlmacenAsync(unitOfWork, factura.AlmacenId, cancellationToken);
        if (almacen.EsFallo)
        {
            almacen = await FacturaVentaBorradorReglas.ResolverAlmacenAsync(unitOfWork, null, cancellationToken);
            if (almacen.EsFallo)
            {
                return Result<CopiaFacturaResponse>.Fallo(almacen);
            }

            avisos.Add("El almacén de la factura ya no está disponible: el borrador usa el almacén predeterminado.");
        }

        // Series configuradas (ruling F10: el mismo helper que el alta de borradores).
        var hoy = DateOnly.FromDateTime(DateTime.UtcNow);
        var series = await generadorNumero.NumerarBorradorAsync(
            TipoDocumentoSerie.BorradorFacturaVenta, TipoDocumentoSerie.FacturaVenta, null, hoy, cancellationToken);
        if (!series.TryObtenerValor(out var numeradas))
        {
            return Result<CopiaFacturaResponse>.Fallo(series);
        }

        var entity = new FacturaVentaBorrador
        {
            Numero = numeradas.Numero.Numero,
            SerieBorradorId = numeradas.SerieBorradorId,
            SerieRegistroId = numeradas.SerieRegistroId,
            NombreFacturacion = datosSocios.NombreFacturacion,
            FechaRegistro = hoy,
            FechaDocumento = hoy,
            FechaVencimiento = await FacturaVentaBorradorReglas.CalcularVencimientoAsync(unitOfWork, hoy, datosSocios.TerminoPagoId, cancellationToken),
            AlmacenId = almacen.Valor,
            Estado = EstadoFacturaBorrador.Abierta,
            Moneda = factura.Moneda,
            Descripcion = factura.Descripcion,
        };
        datosSocios.Aplicar(entity);
        await unitOfWork.Repository<FacturaVentaBorrador>().AddAsync(entity, cancellationToken);

        var lineasRepo = unitOfWork.Repository<LineaFacturaVentaBorrador>();
        // LineasFacturaAsync ya las devuelve por NumeroLinea.
        foreach (var original in await facturas.LineasFacturaAsync(factura.Numero, cancellationToken))
        {
            var comentario = original.Tipo == TipoLineaFactura.Comentario;

            // El almacén de la cabecera de la factura no se fija en la línea: sigue al de la cabecera del borrador (quizá sustituido).
            // Un almacén propio de la línea que ya no vale se sustituye por el de la cabecera del borrador, con aviso (ruling NSC5).
            var almacenLinea = original.AlmacenId == factura.AlmacenId ? null : original.AlmacenId;
            if (almacenLinea is { } propio
                && (await FacturaVentaBorradorReglas.ResolverAlmacenAsync(unitOfWork, propio, cancellationToken)).EsFallo)
            {
                almacenLinea = null;
                avisos.Add($"Línea {original.NumeroLinea}: el almacén de la línea ya no está disponible: usa el almacén del borrador.");
            }

            var datos = new LineaFacturaDatos(
                original.Tipo,
                original.ProductoId,
                original.CuentaContableId,
                original.Descripcion,
                almacenLinea,
                original.UnidadMedidaId,
                comentario ? null : original.Cantidad,
                comentario ? null : original.PrecioUnitario,
                comentario ? null : original.PorcentajeDescuentoLinea,
                original.Tipo == TipoLineaFactura.CuentaContable ? original.GrupoIvaProductoId : null);
            var calculo = await LineaFacturaVentaBorradorReglas.ValidarYCalcularAsync(unitOfWork, conversion, derivador, entity, datos, cancellationToken);
            if (!calculo.TryObtenerValor(out var valores))
            {
                avisos.Add($"Línea {original.NumeroLinea}: {calculo.Errores[0].Mensaje} La línea no se copió.");
                continue;
            }

            var linea = new LineaFacturaVentaBorrador { FacturaVentaBorradorId = entity.Id, NumeroLinea = original.NumeroLinea };
            valores.Aplicar(linea);
            await lineasRepo.AddAsync(linea, cancellationToken);
        }

        await unitOfWork.CommitAsync(cancellationToken);
        return Result<CopiaFacturaResponse>.Exito(new CopiaFacturaResponse(entity.Id, entity.Numero, avisos));
    }

    private static Error NoEncontrada(string numero) => new("factura.no_encontrado", $"No existe la factura {numero}.", "Numero");

    private static Result<CopiaFacturaResponse> Fallo(Error error) => Result<CopiaFacturaResponse>.Fallo(error);
}
