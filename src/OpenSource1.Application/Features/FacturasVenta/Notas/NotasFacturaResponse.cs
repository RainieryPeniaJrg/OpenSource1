using MediatR;
using OpenSource1.Application.Features.NotasCreditoVenta.Borradores.Dtos;
using OpenSource1.Application.Features.NotasCreditoVenta.Posteadas.Dtos;
using OpenSource1.Core.Common;

namespace OpenSource1.Application.Features.FacturasVenta.Notas;

/// <summary>All credit notes and open drafts of one exact invoice, read from one database snapshot.</summary>
public sealed record NotasFacturaResponse(
    IReadOnlyList<NotaCreditoVentaResponse> Notas,
    IReadOnlyList<CreditoLineaFacturaResponse> Lineas,
    IReadOnlyList<NotaCreditoVentaBorradorResponse> Borradores);

public sealed record CreditoLineaFacturaResponse(long LineaFacturaVentaId, decimal Cantidad, decimal Importe, decimal Devuelto);

public interface INotasFacturaReadRepository
{
    Task<NotasFacturaResponse?> GetAsync(string numero, CancellationToken cancellationToken = default);
}

public sealed record GetNotasFacturaQuery(string Numero) : IRequest<Result<NotasFacturaResponse>>;

public sealed class GetNotasFacturaQueryHandler(INotasFacturaReadRepository repository)
    : IRequestHandler<GetNotasFacturaQuery, Result<NotasFacturaResponse>>
{
    public async Task<Result<NotasFacturaResponse>> Handle(GetNotasFacturaQuery request, CancellationToken cancellationToken)
    {
        if (string.IsNullOrWhiteSpace(request.Numero) || request.Numero.Length > 20)
        {
            return Result<NotasFacturaResponse>.Fallo(new Error("factura.numero_invalido", "Indique un número de factura válido.", "Numero"));
        }

        var response = await repository.GetAsync(request.Numero, cancellationToken);
        return response is null
            ? Result<NotasFacturaResponse>.Fallo(new Error("factura.no_encontrado", "No se encontró la factura de venta posteada solicitada.", "Numero"))
            : Result<NotasFacturaResponse>.Exito(response);
    }
}
