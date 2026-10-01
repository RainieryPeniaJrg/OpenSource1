using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenSource1.Api.Infrastructure;
using OpenSource1.Application.Features.FacturasVenta.Calculo;
using OpenSource1.Application.Features.NotasCreditoVenta.Borradores;
using OpenSource1.Application.Features.NotasCreditoVenta.Borradores.Commands;
using OpenSource1.Application.Features.NotasCreditoVenta.Borradores.Dtos;
using OpenSource1.Application.Features.NotasCreditoVenta.Borradores.Queries;
using OpenSource1.Application.Features.NotasCreditoVenta.Posteadas;
using OpenSource1.Application.Features.NotasCreditoVenta.Posteadas.Dtos;
using OpenSource1.Application.Features.NotasCreditoVenta.Posteadas.Queries;
using OpenSource1.Application.Features.NotasCreditoVenta.Posteo;
using OpenSource1.Application.Security;
using OpenSource1.Core.Common;
using OpenSource1.Core.Enums;

namespace OpenSource1.Api.Controllers;

/// <summary>
/// Notas de crédito de venta (Task 8.6), siempre ligadas a una factura posteada: borradores (cabecera y líneas, con <c>Xmin</c>),
/// líneas acreditables de la factura, vista previa de totales, posteo y notas posteadas (los segmentos literales
/// <c>borradores</c> y <c>lineas-borrador</c> tienen precedencia sobre <c>{numero}</c>). Permisos: consultar = CanConsult; alta de
/// borrador y de línea = CanAdd; modificar y postear = CanModify; borrar = CanDelete.
/// </summary>
[ApiController]
[Route("api/notas-credito-venta")]
public sealed class NotasCreditoVentaController(ISender sender) : ControllerBase
{
    // ----- Notas posteadas -----

    /// <summary>Notas posteadas, paginadas (por defecto por número descendente); filtros por número, factura, nombre, socio y fechas.</summary>
    [HttpGet]
    [Authorize(Policy = ApplicationPolicies.CanConsult)]
    [ProducesResponseType<PagedResult<NotaCreditoVentaResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> List(
        [FromQuery] string? numero,
        [FromQuery] string? facturaVentaNumero,
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
            new ListNotasCreditoVentaQuery(
                new NotaCreditoVentaSearchCriteria(numero, facturaVentaNumero, nombreFacturacion, socioId, desde, hasta),
                new PageRequest(pagina, tamanoPagina, ordenarPor, descendente)),
            cancellationToken);

        return result.EsFallo ? result.ToActionResult() : Ok(result.Valor);
    }

    /// <summary>Nota posteada: cabecera, líneas y líneas de IVA.</summary>
    [HttpGet("{numero:maxlength(20)}")]
    [Authorize(Policy = ApplicationPolicies.CanConsult)]
    [ProducesResponseType<NotaCreditoVentaDetalleResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetByNumero(string numero, CancellationToken cancellationToken)
    {
        var result = await sender.Send(new GetNotaCreditoVentaByNumeroQuery(numero), cancellationToken);
        return result.EsFallo ? result.ToActionResult() : Ok(result.Valor);
    }

    // ----- Borradores (cabecera) -----

    /// <summary>Borradores por estado: <c>estado</c> 1 = Abierta (por defecto), 3 = Posteada; cualquier otro valor, 400 en <c>Estado</c>.</summary>
    [HttpGet("borradores")]
    [Authorize(Policy = ApplicationPolicies.CanConsult)]
    [ProducesResponseType<PagedResult<NotaCreditoVentaBorradorResponse>>(StatusCodes.Status200OK)]
    public async Task<IActionResult> ListBorradores(
        [FromQuery] string? numero,
        [FromQuery] string? facturaVentaNumero,
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
            new ListNotasCreditoVentaBorradorQuery(
                new NotaCreditoVentaBorradorSearchCriteria(
                    numero, facturaVentaNumero, nombreFacturacion, socioId,
                    // Fuera del rango de short, un valor no definido (0) para que el handler lo rechace en vez de truncarlo.
                    estado is { } e ? (EstadoNotaCreditoBorrador)(e is >= short.MinValue and <= short.MaxValue ? e : 0) : null),
                new PageRequest(pagina, tamanoPagina, ordenarPor, descendente)),
            cancellationToken);

        return result.EsFallo ? result.ToActionResult() : Ok(result.Valor);
    }

    [HttpGet("borradores/{id:guid}")]
    [Authorize(Policy = ApplicationPolicies.CanConsult)]
    [ProducesResponseType<NotaCreditoVentaBorradorResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetBorradorById(Guid id, CancellationToken cancellationToken)
    {
        var result = await sender.Send(new GetNotaCreditoVentaBorradorByIdQuery(id), cancellationToken);
        return result.EsFallo ? result.ToActionResult() : Ok(result.Valor);
    }

    /// <summary>
    /// Crea un borrador desde una factura posteada (spec alta rápida): por defecto con todas sus líneas pendientes (<c>copiarLineas</c>)
    /// y la devolución al inventario de las de producto (<c>devolverInventario</c>); series configuradas (la de registro solo la cambia el Administrador).
    /// </summary>
    [HttpPost("borradores")]
    [Authorize(Policy = ApplicationPolicies.CanAdd)]
    [ProducesResponseType<NotaCreditoVentaBorradorResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> CreateBorrador(CreateNotaCreditoVentaBorradorRequest request, CancellationToken cancellationToken)
    {
        var result = await sender.Send(
            new CreateNotaCreditoVentaBorradorCommand(
                request.FacturaVentaNumero, request.FechaRegistro, request.FechaDocumento, request.Descripcion,
                request.CopiarLineas ?? true, request.DevolverInventario ?? true, request.SerieBorradorId, request.SerieRegistroId),
            cancellationToken);

        return result.EsFallo
            ? result.ToActionResult()
            : CreatedAtAction(nameof(GetBorradorById), new { id = result.Valor.Id }, result.Valor);
    }

    [HttpPut("borradores/{id:guid}")]
    [Authorize(Policy = ApplicationPolicies.CanModify)]
    [ProducesResponseType<NotaCreditoVentaBorradorResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> UpdateBorrador(Guid id, UpdateNotaCreditoVentaBorradorRequest request, CancellationToken cancellationToken)
    {
        var result = await sender.Send(
            new UpdateNotaCreditoVentaBorradorCommand(
                id, request.FechaRegistro, request.FechaDocumento, request.Descripcion, request.Xmin, request.SerieRegistroId),
            cancellationToken);

        return result.EsFallo ? result.ToActionResult() : Ok(result.Valor);
    }

    [HttpDelete("borradores/{id:guid}")]
    [Authorize(Policy = ApplicationPolicies.CanDelete)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> DeleteBorrador(Guid id, CancellationToken cancellationToken)
    {
        var result = await sender.Send(new DeleteNotaCreditoVentaBorradorCommand(id), cancellationToken);
        return result.EsFallo ? result.ToActionResult() : NoContent();
    }

    /// <summary>
    /// Posteo del borrador: documento legal, devolución de inventario, movimiento de cliente con aplicación automática a la factura y
    /// asiento inverso, en una transacción. 200 con el número, el total, lo aplicado y el número del registro contable.
    /// </summary>
    [HttpPost("borradores/{id:guid}/postear")]
    [Authorize(Policy = ApplicationPolicies.CanModify)]
    [ProducesResponseType<ResultadoPosteoNotaCredito>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> PostearBorrador(Guid id, CancellationToken cancellationToken)
    {
        var result = await sender.Send(new PostearNotaCreditoVentaCommand(id), cancellationToken);
        return result.EsFallo ? result.ToActionResult() : Ok(result.Valor);
    }

    [HttpGet("borradores/{id:guid}/totales")]
    [Authorize(Policy = ApplicationPolicies.CanConsult)]
    [ProducesResponseType<TotalesFactura>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetTotalesBorrador(Guid id, CancellationToken cancellationToken)
    {
        var result = await sender.Send(new GetTotalesNotaCreditoVentaBorradorQuery(id), cancellationToken);
        return result.EsFallo ? result.ToActionResult() : Ok(result.Valor);
    }

    /// <summary>Líneas de la factura del borrador que se pueden acreditar, con lo facturado, acreditado y pendiente.</summary>
    [HttpGet("borradores/{id:guid}/lineas-acreditables")]
    [Authorize(Policy = ApplicationPolicies.CanConsult)]
    [ProducesResponseType<IReadOnlyList<LineaFacturaAcreditableResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ListLineasAcreditables(Guid id, CancellationToken cancellationToken)
    {
        var result = await sender.Send(new ListLineasAcreditablesNotaCreditoVentaQuery(id), cancellationToken);
        return result.EsFallo ? result.ToActionResult() : Ok(result.Valor);
    }

    // ----- Líneas de borrador -----

    [HttpGet("borradores/{id:guid}/lineas")]
    [Authorize(Policy = ApplicationPolicies.CanConsult)]
    [ProducesResponseType<IReadOnlyList<LineaNotaCreditoVentaBorradorResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> ListLineas(Guid id, CancellationToken cancellationToken)
    {
        var result = await sender.Send(new ListLineasNotaCreditoVentaBorradorQuery(id), cancellationToken);
        return result.EsFallo ? result.ToActionResult() : Ok(result.Valor);
    }

    [HttpPost("borradores/{id:guid}/lineas")]
    [Authorize(Policy = ApplicationPolicies.CanAdd)]
    [ProducesResponseType<LineaNotaCreditoVentaBorradorResponse>(StatusCodes.Status201Created)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> CreateLinea(Guid id, CreateLineaNotaCreditoVentaBorradorRequest request, CancellationToken cancellationToken)
    {
        var result = await sender.Send(
            new CreateLineaNotaCreditoVentaBorradorCommand(id, request.LineaFacturaVentaId, request.Cantidad, request.DevolverInventario),
            cancellationToken);

        return result.EsFallo
            ? result.ToActionResult()
            : CreatedAtAction(nameof(GetLineaById), new { id = result.Valor.Id }, result.Valor);
    }

    [HttpGet("lineas-borrador/{id:guid}")]
    [Authorize(Policy = ApplicationPolicies.CanConsult)]
    [ProducesResponseType<LineaNotaCreditoVentaBorradorResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> GetLineaById(Guid id, CancellationToken cancellationToken)
    {
        var result = await sender.Send(new GetLineaNotaCreditoVentaBorradorByIdQuery(id), cancellationToken);
        return result.EsFallo ? result.ToActionResult() : Ok(result.Valor);
    }

    [HttpPut("lineas-borrador/{id:guid}")]
    [Authorize(Policy = ApplicationPolicies.CanModify)]
    [ProducesResponseType<LineaNotaCreditoVentaBorradorResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> UpdateLinea(Guid id, UpdateLineaNotaCreditoVentaBorradorRequest request, CancellationToken cancellationToken)
    {
        var result = await sender.Send(
            new UpdateLineaNotaCreditoVentaBorradorCommand(id, request.Cantidad, request.DevolverInventario, request.Xmin),
            cancellationToken);

        return result.EsFallo ? result.ToActionResult() : Ok(result.Valor);
    }

    [HttpDelete("lineas-borrador/{id:guid}")]
    [Authorize(Policy = ApplicationPolicies.CanDelete)]
    [ProducesResponseType(StatusCodes.Status204NoContent)]
    [ProducesResponseType(StatusCodes.Status404NotFound)]
    public async Task<IActionResult> DeleteLinea(Guid id, CancellationToken cancellationToken)
    {
        var result = await sender.Send(new DeleteLineaNotaCreditoVentaBorradorCommand(id), cancellationToken);
        return result.EsFallo ? result.ToActionResult() : NoContent();
    }
}

public sealed record CreateNotaCreditoVentaBorradorRequest(
    string FacturaVentaNumero,
    DateOnly? FechaRegistro = null,
    DateOnly? FechaDocumento = null,
    string? Descripcion = null,
    bool? CopiarLineas = null,
    bool? DevolverInventario = null,
    Guid? SerieBorradorId = null,
    Guid? SerieRegistroId = null);

/// <summary>
/// Fechas, descripción y serie de registro con semántica "null = conservar" (<c>descripcion</c> "" = limpiar); solo en un borrador
/// Abierta (Posteada -&gt; 409).
/// </summary>
public sealed record UpdateNotaCreditoVentaBorradorRequest(
    long Xmin, DateOnly? FechaRegistro = null, DateOnly? FechaDocumento = null, string? Descripcion = null, Guid? SerieRegistroId = null);

public sealed record CreateLineaNotaCreditoVentaBorradorRequest(long LineaFacturaVentaId, decimal? Cantidad, bool DevolverInventario = false);

public sealed record UpdateLineaNotaCreditoVentaBorradorRequest(long Xmin, decimal? Cantidad, bool DevolverInventario = false);
