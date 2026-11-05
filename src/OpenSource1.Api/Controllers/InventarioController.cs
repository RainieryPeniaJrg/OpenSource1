using MediatR;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Mvc;
using OpenSource1.Api.Infrastructure;
using OpenSource1.Application.Features.Inventario.Commands;
using OpenSource1.Application.Features.Inventario.Consultas;
using OpenSource1.Application.Features.Inventario.Consultas.Dtos;
using OpenSource1.Application.Security;
using OpenSource1.Application.Services.Inventario;
using OpenSource1.Core.Common;

namespace OpenSource1.Api.Controllers;

[ApiController]
[Route("api/inventario")]
public sealed class InventarioController(ISender sender) : ControllerBase
{
    /// <summary>
    /// Rutina "Ajustar costo movimientos": todos los productos con <c>CostoAjustado = false</c>, o solo
    /// <paramref name="productoId"/>. Idempotente. 200 con <c>{ productosAjustados, movimientosValorCreados }</c>.
    /// </summary>
    [HttpPost("ajustar-costo")]
    [Authorize(Policy = ApplicationPolicies.CanModify)]
    [ProducesResponseType<ResultadoAjusteCosto>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    [ProducesResponseType(StatusCodes.Status409Conflict)]
    public async Task<IActionResult> AjustarCosto([FromQuery] Guid? productoId, CancellationToken cancellationToken)
    {
        var result = await sender.Send(new AjustarCostoMovimientosCommand(productoId), cancellationToken);
        return result.EsFallo ? result.ToActionResult() : Ok(result.Valor);
    }
    /// <summary>
    /// Movimientos de producto (Task 7.2), por defecto en orden cronológico ascendente (<c>FechaRegistro</c>, <c>Id</c>). Con
    /// <c>productoId</c> cada fila trae <c>saldoAcumulado</c> (existencia tras el movimiento, incluido lo anterior a <c>desde</c> y
    /// a la página); sin él la propiedad no se devuelve. <c>tipoMovimiento</c>/<c>tipoOrigen</c> como entero; uno no definido,
    /// o fechas invertidas → 400 (una página enorme → 200 con página vacía). Orden: <c>Id</c>, <c>FechaRegistro</c>, <c>NumeroDocumento</c>,
    /// <c>Cantidad</c>, <c>TipoMovimiento</c>, <c>TipoOrigen</c> (otra columna se ignora).
    /// </summary>
    [HttpGet("movimientos-producto")]
    [Authorize(Policy = ApplicationPolicies.CanConsult)]
    [ProducesResponseType<PagedResult<MovimientoProductoVistaResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ListMovimientosProducto(
        [FromQuery] Guid? productoId,
        [FromQuery] Guid? almacenId,
        [FromQuery] DateOnly? desde,
        [FromQuery] DateOnly? hasta,
        [FromQuery] int? tipoMovimiento,
        [FromQuery] int? tipoOrigen,
        [FromQuery] string? numeroDocumento,
        [FromQuery] int pagina = 1,
        [FromQuery] int tamanoPagina = PageRequest.TamanoPorDefecto,
        [FromQuery] string? ordenarPor = null,
        [FromQuery] bool descendente = false,
        CancellationToken cancellationToken = default)
    {
        var result = await sender.Send(
            new ListMovimientosProductoVistaQuery(
                new MovimientoProductoVistaCriterios(productoId, almacenId, desde, hasta, tipoMovimiento, tipoOrigen, numeroDocumento),
                new PageRequest(pagina, tamanoPagina, ordenarPor, descendente)),
            cancellationToken);
        return result.EsFallo ? result.ToActionResult() : Ok(result.Valor);
    }

    /// <summary>
    /// Movimientos de valor (Task 7.2): mismos filtros que los de producto más <c>soloAjustes</c>; <c>contabilizado</c> =
    /// <c>ImporteCostoPosteadoContabilidad = ImporteCosto</c>. Orden: <c>Id</c>, <c>FechaRegistro</c>, <c>NumeroDocumento</c>,
    /// <c>CantidadValorada</c>, <c>ImporteCosto</c>, <c>TipoMovimiento</c>, <c>TipoOrigen</c>.
    /// </summary>
    [HttpGet("movimientos-valor")]
    [Authorize(Policy = ApplicationPolicies.CanConsult)]
    [ProducesResponseType<PagedResult<MovimientoValorVistaResponse>>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ListMovimientosValor(
        [FromQuery] Guid? productoId,
        [FromQuery] Guid? almacenId,
        [FromQuery] DateOnly? desde,
        [FromQuery] DateOnly? hasta,
        [FromQuery] int? tipoMovimiento,
        [FromQuery] int? tipoOrigen,
        [FromQuery] string? numeroDocumento,
        [FromQuery] bool soloAjustes = false,
        [FromQuery] int pagina = 1,
        [FromQuery] int tamanoPagina = PageRequest.TamanoPorDefecto,
        [FromQuery] string? ordenarPor = null,
        [FromQuery] bool descendente = false,
        CancellationToken cancellationToken = default)
    {
        var result = await sender.Send(
            new ListMovimientosValorVistaQuery(
                new MovimientoValorVistaCriterios(
                    productoId, almacenId, desde, hasta, tipoMovimiento, tipoOrigen, numeroDocumento, soloAjustes),
                new PageRequest(pagina, tamanoPagina, ordenarPor, descendente)),
            cancellationToken);
        return result.EsFallo ? result.ToActionResult() : Ok(result.Valor);
    }

    /// <summary>
    /// Existencia y valor por producto y almacén a la fecha <c>fecha</c> (incluida; por defecto hoy), con el costo medio y el valor
    /// total de todas las filas filtradas. <c>texto</c> busca en código y nombre del producto; <c>soloConExistencia</c> oculta las
    /// Original: filas con existencia 0. Ahora solo oculta filas cuya cantidad Y valor son cero (conserva valor residual).
    /// Orden por defecto: código de producto y de almacén ascendente; columnas: <c>ProductoCodigo</c>,
    /// <c>ProductoNombre</c>, <c>AlmacenCodigo</c>, <c>Existencia</c>, <c>Valor</c>.
    /// </summary>
    [HttpGet("existencias")]
    [Authorize(Policy = ApplicationPolicies.CanConsult)]
    [ProducesResponseType<ExistenciasVistaResponse>(StatusCodes.Status200OK)]
    [ProducesResponseType(StatusCodes.Status400BadRequest)]
    public async Task<IActionResult> ListExistencias(
        [FromQuery] Guid? almacenId,
        [FromQuery] Guid? productoId,
        [FromQuery] string? texto,
        [FromQuery] DateOnly? fecha,
        [FromQuery] bool soloConExistencia = false,
        [FromQuery] int pagina = 1,
        [FromQuery] int tamanoPagina = PageRequest.TamanoPorDefecto,
        [FromQuery] string? ordenarPor = null,
        [FromQuery] bool descendente = false,
        CancellationToken cancellationToken = default)
    {
        var result = await sender.Send(
            new ListExistenciasVistaQuery(
                new ExistenciaVistaCriterios(almacenId, productoId, texto, fecha, soloConExistencia),
                new PageRequest(pagina, tamanoPagina, ordenarPor, descendente)),
            cancellationToken);
        return result.EsFallo ? result.ToActionResult() : Ok(result.Valor);
    }
}
