namespace OpenSource1.Application.Features.Inventario.Consultas;

/// <summary>
/// Filtros de <c>GET api/inventario/movimientos-producto</c> (Task 7.2). Rango de <c>FechaRegistro</c> con ambos extremos
/// incluidos. <see cref="TipoMovimiento"/> y <see cref="TipoOrigen"/> van como entero (valores de
/// <c>TipoMovimientoInventario</c>/<c>TipoOrigenMovimiento</c>; uno no definido → 400). <see cref="NumeroDocumento"/> busca por
/// contenido (ILIKE, con la sintaxis de <c>FilterExpressionBuilder</c>). Con <see cref="ProductoId"/> la respuesta trae el
/// saldo acumulado.
/// </summary>
public sealed record MovimientoProductoVistaCriterios(
    Guid? ProductoId = null,
    Guid? AlmacenId = null,
    DateOnly? Desde = null,
    DateOnly? Hasta = null,
    int? TipoMovimiento = null,
    int? TipoOrigen = null,
    string? NumeroDocumento = null);

/// <summary>
/// Filtros de <c>GET api/inventario/movimientos-valor</c>: los mismos que los de movimientos de producto más
/// <see cref="SoloAjustes"/> (<c>true</c> = solo las filas con <c>Ajuste</c>; ausente o <c>false</c> = todas).
/// </summary>
public sealed record MovimientoValorVistaCriterios(
    Guid? ProductoId = null,
    Guid? AlmacenId = null,
    DateOnly? Desde = null,
    DateOnly? Hasta = null,
    int? TipoMovimiento = null,
    int? TipoOrigen = null,
    string? NumeroDocumento = null,
    bool SoloAjustes = false);

/// <summary>
/// Filtros de <c>GET api/inventario/existencias</c>: almacén, producto (por id o por <see cref="Texto"/> sobre código y nombre),
/// fecha de corte (<see cref="Fecha"/>, incluida; el handler la fija a hoy si falta) y <see cref="SoloConExistencia"/>
/// Original: (<c>true</c> = solo filas con existencia distinta de cero).
/// (<c>true</c> = filas con cantidad o valor distinto de cero, para no ocultar valor residual sin existencia física).
/// </summary>
public sealed record ExistenciaVistaCriterios(
    Guid? AlmacenId = null,
    Guid? ProductoId = null,
    string? Texto = null,
    DateOnly? Fecha = null,
    bool SoloConExistencia = false);
