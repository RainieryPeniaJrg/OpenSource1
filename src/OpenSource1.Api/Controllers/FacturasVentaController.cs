using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenSource1.Api.Infrastructure;
using OpenSource1.Application.Features.FacturasVenta.Borradores;
using OpenSource1.Application.Features.FacturasVenta.Borradores.Commands;
using OpenSource1.Application.Features.FacturasVenta.Borradores.Dtos;
using OpenSource1.Application.Features.FacturasVenta.Borradores.Queries;
using OpenSource1.Application.Features.FacturasVenta.Calculo;
using OpenSource1.Application.Features.FacturasVenta.Copia;
using OpenSource1.Application.Features.FacturasVenta.Posteadas;
using OpenSource1.Application.Features.FacturasVenta.Posteo;
using OpenSource1.Application.Features.FacturasVenta.Posteadas.Dtos;
using OpenSource1.Application.Features.FacturasVenta.Posteadas.Queries;
using OpenSource1.Application.Security;
using OpenSource1.Core.Common;
using OpenSource1.Core.Enums;

namespace OpenSource1.Api.Controllers;

/// <summary>
/// Facturación de ventas (Fase 6). Task 6.2: borradores (cabecera y líneas) y vista previa de totales. Task 6.3: consultas de
/// facturas posteadas (<c>GET /</c> y <c>GET /{numero}</c>; los segmentos literales <c>borradores</c> y <c>lineas-borrador</c>
/// tienen precedencia sobre <c>{numero}</c>). Permisos: consultar = CanConsult; alta de borrador y de línea = CanAdd; modificar,
/// liberar, reabrir y postear (Task 6.4) = CanModify; borrar = CanDelete. Alta rápida: <c>POST borradores</c> admite <c>primeraLinea</c>
/// (mismo cuerpo que <c>POST borradores/{id}/lineas</c>); el alta sin líneas sigue disponible por compatibilidad.
/// </summary>
[ApiController]
[Route("api/facturas-venta")]
public sealed class FacturasVentaController(ISender sender) : ControllerBase
{
    // ----- Facturas posteadas (Task 6.3) -----

    /// <summary>
    /// Facturas posteadas, paginadas; por defecto por número descendente (lo último posteado primero). <c>socioId</c> filtra
    /// vender-a o facturar-a; <c>desde</c>/<c>hasta</c>, la fecha de registro (incluidas).
    /// </summary>
    [HttpGet]
    [Authorize(Policy = ApplicationPolicies.CanConsult)]
    [ProducesResponseType<PagedResult<FacturaVentaResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> List(
        [FromQuery] string? numero,
        [FromQuery] string? nombreFacturacion,
        [FromQuery] Guid? socioId,
        [FromQuery] DateOnly? desde,
        [FromQuery] DateOnly? hasta,
        [FromQuery] int pagina = 1,
        [FromQuery] int tamanoPagina = PageRequest.TamanoPorDefecto,
        [FromQuery] string? ordenarPor = null,
        [FromQuery] bool descendente = true,
        CancellationToken cancellationToken = default)
    {
        var result = await sender.Send(
            new ListFacturasVentaQuery(
                new FacturaVentaSearchCriteria(numero, nombreFacturacion, socioId, desde, hasta),
                new PageRequest(pagina, tamanoPagina, ordenarPor, descendente)),
            cancellationToken);

        return result.EsFallo ? result.ToActionResult() : Ok(result.Valor);
    }

    /// <summary>Factura posteada: cabecera, líneas (por número de línea) y líneas de IVA (una por identificador).</summary>
    [HttpGet("{numero:maxlength(20)}")]
    [Authorize(Policy = ApplicationPolicies.CanConsult)]
    [ProducesResponseType<FacturaVentaDetalleResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetByNumero(string numero, CancellationToken cancellationToken)
    {
        var result = await sender.Send(new GetFacturaVentaByNumeroQuery(numero), cancellationToken);
        return result.EsFallo ? result.ToActionResult() : Ok(result.Valor);
    }

    /// <summary>
    /// Copiar una factura posteada a un borrador nuevo (spec no-series): 201 con el borrador y los avisos (líneas omitidas o almacén
    /// sustituido; vacío = copia completa). CanAdd. 404 si la factura no existe; 400 si el cliente está bloqueado o borrado, o por la
    /// numeración.
    /// </summary>
    [HttpPost("{numero:maxlength(20)}/copiar-a-borrador")]
    [Authorize(Policy = ApplicationPolicies.CanAdd)]
    [ProducesResponseType<CopiaFacturaResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CopiarABorrador(string numero, CancellationToken cancellationToken)
    {
        var result = await sender.Send(new CopiarFacturaABorradorCommand(numero), cancellationToken);
        return result.EsFallo
            ? result.ToActionResult()
            : CreatedAtAction(nameof(GetBorradorById), new { id = result.Valor.BorradorId }, result.Valor);
    }

    // ----- Borradores (cabecera) -----

    /// <summary>
    /// Listado paginado. <c>estado</c> como entero (1 = Abierta, 2 = Liberada, 3 = Posteada; sin estado = Abierta y Liberada);
    /// <c>socioId</c> filtra vender-a o facturar-a.
    /// </summary>
    [HttpGet("borradores")]
    [Authorize(Policy = ApplicationPolicies.CanConsult)]
    [ProducesResponseType<PagedResult<FacturaVentaBorradorResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ListBorradores(
        [FromQuery] string? numero,
        [FromQuery] string? nombreFacturacion,
        [FromQuery] Guid? socioId,
        [FromQuery] int? estado,
        [FromQuery] int pagina = 1,
        [FromQuery] int tamanoPagina = PageRequest.TamanoPorDefecto,
        [FromQuery] string? ordenarPor = null,
        [FromQuery] bool descendente = true,
        CancellationToken cancellationToken = default)
    {
        var result = await sender.Send(
            new ListFacturasVentaBorradorQuery(
                new FacturaVentaBorradorSearchCriteria(numero, nombreFacturacion, socioId, (EstadoFacturaBorrador?)estado),
                new PageRequest(pagina, tamanoPagina, ordenarPor, descendente)),
            cancellationToken);

        return result.EsFallo ? result.ToActionResult() : Ok(result.Valor);
    }

    [HttpGet("borradores/{id:guid}")]
    [Authorize(Policy = ApplicationPolicies.CanConsult)]
    [ProducesResponseType<FacturaVentaBorradorResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetBorradorById(Guid id, CancellationToken cancellationToken)
    {
        var result = await sender.Send(new GetFacturaVentaBorradorByIdQuery(id), cancellationToken);
        return result.EsFallo ? result.ToActionResult() : Ok(result.Valor);
    }

    [HttpPost("borradores")]
    [Authorize(Policy = ApplicationPolicies.CanAdd)]
    [ProducesResponseType<FacturaVentaBorradorResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateBorrador(CreateFacturaVentaBorradorRequest request, CancellationToken cancellationToken)
    {
        var result = await sender.Send(
            new CreateFacturaVentaBorradorCommand(
                request.SocioNegocioId, request.SocioNegocioFacturarAId, request.FechaRegistro, request.FechaDocumento,
                request.FechaVencimiento, request.AlmacenId, request.Descripcion, request.SerieBorradorId, request.SerieRegistroId,
                request.PrimeraLinea is { } l
                    ? new PrimeraLineaFacturaVenta(
                        l.Tipo, l.ProductoId, l.CuentaContableId, l.Descripcion, l.AlmacenId, l.UnidadMedidaId, l.Cantidad, l.PrecioUnitario,
                        l.PorcentajeDescuentoLinea, l.GrupoIvaProductoId)
                    : null),
            cancellationToken);

        return result.EsFallo
            ? result.ToActionResult()
            : CreatedAtAction(nameof(GetBorradorById), new { id = result.Valor.Id }, result.Valor);
    }

    [HttpPut("borradores/{id:guid}")]
    [Authorize(Policy = ApplicationPolicies.CanModify)]
    [ProducesResponseType<FacturaVentaBorradorResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> UpdateBorrador(Guid id, UpdateFacturaVentaBorradorRequest request, CancellationToken cancellationToken)
    {
        var result = await sender.Send(
            new UpdateFacturaVentaBorradorCommand(
                id, request.SocioNegocioId, request.SocioNegocioFacturarAId, request.FechaRegistro, request.FechaDocumento,
                request.FechaVencimiento, request.AlmacenId, request.Descripcion, request.Xmin, request.SerieRegistroId),
            cancellationToken);

        return result.EsFallo ? result.ToActionResult() : Ok(result.Valor);
    }

    [HttpDelete("borradores/{id:guid}")]
    [Authorize(Policy = ApplicationPolicies.CanDelete)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteBorrador(Guid id, CancellationToken cancellationToken)
    {
        var result = await sender.Send(new DeleteFacturaVentaBorradorCommand(id), cancellationToken);
        return result.EsFallo ? result.ToActionResult() : NoContent();
    }

    [HttpPost("borradores/{id:guid}/liberar")]
    [Authorize(Policy = ApplicationPolicies.CanModify)]
    [ProducesResponseType<FacturaVentaBorradorResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> LiberarBorrador(Guid id, CancellationToken cancellationToken)
    {
        var result = await sender.Send(new LiberarFacturaVentaBorradorCommand(id), cancellationToken);
        return result.EsFallo ? result.ToActionResult() : Ok(result.Valor);
    }

    [HttpPost("borradores/{id:guid}/reabrir")]
    [Authorize(Policy = ApplicationPolicies.CanModify)]
    [ProducesResponseType<FacturaVentaBorradorResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ReabrirBorrador(Guid id, CancellationToken cancellationToken)
    {
        var result = await sender.Send(new ReabrirFacturaVentaBorradorCommand(id), cancellationToken);
        return result.EsFallo ? result.ToActionResult() : Ok(result.Valor);
    }

    /// <summary>
    /// Posteo del borrador (Task 6.4): documento legal, salidas de inventario, libro de clientes y asiento en una transacción.
    /// Admite un borrador Abierta o Liberada. 200 con el número de la factura, su total y el número del registro contable.
    /// </summary>
    [HttpPost("borradores/{id:guid}/postear")]
    [Authorize(Policy = ApplicationPolicies.CanModify)]
    [ProducesResponseType<ResultadoPosteoFactura>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> PostearBorrador(Guid id, CancellationToken cancellationToken)
    {
        var result = await sender.Send(new PostearFacturaVentaCommand(id), cancellationToken);
        return result.EsFallo ? result.ToActionResult() : Ok(result.Valor);
    }

    /// <summary>Vista previa de totales (derivados de las líneas, IVA agrupado por identificador).</summary>
    [HttpGet("borradores/{id:guid}/totales")]
    [Authorize(Policy = ApplicationPolicies.CanConsult)]
    [ProducesResponseType<TotalesFactura>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetTotalesBorrador(Guid id, CancellationToken cancellationToken)
    {
        var result = await sender.Send(new GetTotalesFacturaVentaBorradorQuery(id), cancellationToken);
        return result.EsFallo ? result.ToActionResult() : Ok(result.Valor);
    }

    // ----- Líneas de borrador -----

    [HttpGet("borradores/{id:guid}/lineas")]
    [Authorize(Policy = ApplicationPolicies.CanConsult)]
    [ProducesResponseType<IReadOnlyList<LineaFacturaVentaBorradorResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ListLineas(Guid id, CancellationToken cancellationToken)
    {
        var result = await sender.Send(new ListLineasFacturaVentaBorradorQuery(id), cancellationToken);
        return result.EsFallo ? result.ToActionResult() : Ok(result.Valor);
    }

    [HttpPost("borradores/{id:guid}/lineas")]
    [Authorize(Policy = ApplicationPolicies.CanAdd)]
    [ProducesResponseType<LineaFacturaVentaBorradorResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CreateLinea(Guid id, CreateLineaFacturaVentaBorradorRequest request, CancellationToken cancellationToken)
    {
        var result = await sender.Send(
            new CreateLineaFacturaVentaBorradorCommand(
                id, request.Tipo, request.ProductoId, request.CuentaContableId, request.Descripcion, request.AlmacenId,
                request.UnidadMedidaId, request.Cantidad, request.PrecioUnitario, request.PorcentajeDescuentoLinea,
                request.GrupoIvaProductoId),
            cancellationToken);

        return result.EsFallo
            ? result.ToActionResult()
            : CreatedAtAction(nameof(GetLineaById), new { id = result.Valor.Id }, result.Valor);
    }

    [HttpGet("lineas-borrador/{id:guid}")]
    [Authorize(Policy = ApplicationPolicies.CanConsult)]
    [ProducesResponseType<LineaFacturaVentaBorradorResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetLineaById(Guid id, CancellationToken cancellationToken)
    {
        var result = await sender.Send(new GetLineaFacturaVentaBorradorByIdQuery(id), cancellationToken);
        return result.EsFallo ? result.ToActionResult() : Ok(result.Valor);
    }

    [HttpPut("lineas-borrador/{id:guid}")]
    [Authorize(Policy = ApplicationPolicies.CanModify)]
    [ProducesResponseType<LineaFacturaVentaBorradorResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> UpdateLinea(Guid id, UpdateLineaFacturaVentaBorradorRequest request, CancellationToken cancellationToken)
    {
        var result = await sender.Send(
            new UpdateLineaFacturaVentaBorradorCommand(
                id, request.Tipo, request.ProductoId, request.CuentaContableId, request.Descripcion, request.AlmacenId,
                request.UnidadMedidaId, request.Cantidad, request.PrecioUnitario, request.PorcentajeDescuentoLinea,
                request.GrupoIvaProductoId, request.Xmin),
            cancellationToken);

        return result.EsFallo ? result.ToActionResult() : Ok(result.Valor);
    }

    [HttpDelete("lineas-borrador/{id:guid}")]
    [Authorize(Policy = ApplicationPolicies.CanDelete)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteLinea(Guid id, CancellationToken cancellationToken)
    {
        var result = await sender.Send(new DeleteLineaFacturaVentaBorradorCommand(id), cancellationToken);
        return result.EsFallo ? result.ToActionResult() : NoContent();
    }
}

public sealed record CreateFacturaVentaBorradorRequest(
    Guid SocioNegocioId,
    Guid? SocioNegocioFacturarAId = null,
    DateOnly? FechaRegistro = null,
    DateOnly? FechaDocumento = null,
    DateOnly? FechaVencimiento = null,
    Guid? AlmacenId = null,
    string? Descripcion = null,
    Guid? SerieBorradorId = null,
    Guid? SerieRegistroId = null,
    CreateLineaFacturaVentaBorradorRequest? PrimeraLinea = null);

/// <summary>Todos los campos salvo <c>Xmin</c> con semántica "null = conservar" (<c>descripcion</c> "" = limpiar).</summary>
public sealed record UpdateFacturaVentaBorradorRequest(
    long Xmin,
    Guid? SocioNegocioId = null,
    Guid? SocioNegocioFacturarAId = null,
    DateOnly? FechaRegistro = null,
    DateOnly? FechaDocumento = null,
    DateOnly? FechaVencimiento = null,
    Guid? AlmacenId = null,
    string? Descripcion = null,
    Guid? SerieRegistroId = null);

public sealed record CreateLineaFacturaVentaBorradorRequest(
    TipoLineaFactura Tipo,
    Guid? ProductoId = null,
    Guid? CuentaContableId = null,
    string? Descripcion = null,
    Guid? AlmacenId = null,
    Guid? UnidadMedidaId = null,
    decimal? Cantidad = null,
    decimal? PrecioUnitario = null,
    decimal? PorcentajeDescuentoLinea = null,
    Guid? GrupoIvaProductoId = null);

public sealed record UpdateLineaFacturaVentaBorradorRequest(
    TipoLineaFactura Tipo,
    long Xmin,
    Guid? ProductoId = null,
    Guid? CuentaContableId = null,
    string? Descripcion = null,
    Guid? AlmacenId = null,
    Guid? UnidadMedidaId = null,
    decimal? Cantidad = null,
    decimal? PrecioUnitario = null,
    decimal? PorcentajeDescuentoLinea = null,
    Guid? GrupoIvaProductoId = null);
