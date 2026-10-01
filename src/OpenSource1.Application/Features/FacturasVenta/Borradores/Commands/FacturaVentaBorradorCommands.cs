using MediatR;
using OpenSource1.Application.Features.FacturasVenta.Borradores.Dtos;
using OpenSource1.Core.Common;

namespace OpenSource1.Application.Features.FacturasVenta.Borradores.Commands;

/// <summary>
/// Alta de un borrador. <c>SocioNegocioFacturarAId</c> <see langword="null"/> = el mismo que el vender-a;
/// <c>FechaRegistro</c> <see langword="null"/> = hoy (UTC); <c>FechaDocumento</c> <see langword="null"/> = <c>FechaRegistro</c>;
/// <c>FechaVencimiento</c> <see langword="null"/> = <c>FechaDocumento</c> + días del término de pago; <c>AlmacenId</c>
/// <see langword="null"/> = el almacén predeterminado. <c>SerieBorradorId</c> no se admite (400 <c>serie.no_editable</c>: se usa la
/// serie configurada); <c>SerieRegistroId</c> solo la envía el Administrador (si no, 400 <c>serie.no_editable</c>);
/// <see langword="null"/> = la configurada (alta) o conservar (modificación). La de borradores da el número del borrador y la de
/// registro (solo validada en el alta) numerará la factura al postear.
/// </summary>
public sealed record CreateFacturaVentaBorradorCommand(
    Guid SocioNegocioId,
    Guid? SocioNegocioFacturarAId,
    DateOnly? FechaRegistro,
    DateOnly? FechaDocumento,
    DateOnly? FechaVencimiento,
    Guid? AlmacenId,
    string? Descripcion,
    Guid? SerieBorradorId = null,
    Guid? SerieRegistroId = null) : IRequest<Result<FacturaVentaBorradorResponse>>;

/// <summary>
/// Modificación de la cabecera con semántica "null = conservar" en todos los campos. Cambiar cualquiera de los socios vuelve
/// a tomar el snapshot del facturar-a, su término de pago y los tres grupos congelados (y recalcula el IVA congelado de las
/// líneas si cambia el grupo de IVA de negocio). Si cambian la fecha de documento o el término y no se envía
/// <c>FechaVencimiento</c>, se recalcula. <c>Descripcion</c> "" = limpiar. <c>SerieBorradorId</c> no se admite (400
/// <c>serie.no_editable</c>: se usa la serie configurada); <c>SerieRegistroId</c> solo la envía el Administrador (si no, 400
/// <c>serie.no_editable</c>); <see langword="null"/> = la configurada (alta) o conservar (modificación); una nueva se valida (tipo
/// <c>FacturaVenta</c> y activa). <c>Xmin</c> obligatorio (409 si no coincide). Un borrador liberado o
/// posteado no se modifica.
/// </summary>
public sealed record UpdateFacturaVentaBorradorCommand(
    Guid Id,
    Guid? SocioNegocioId,
    Guid? SocioNegocioFacturarAId,
    DateOnly? FechaRegistro,
    DateOnly? FechaDocumento,
    DateOnly? FechaVencimiento,
    Guid? AlmacenId,
    string? Descripcion,
    long Xmin,
    Guid? SerieRegistroId = null) : IRequest<Result<FacturaVentaBorradorResponse>>;

/// <summary>Borrado lógico del borrador y de todas sus líneas. Un borrador liberado debe reabrirse antes (400); uno posteado no se borra (409).</summary>
public sealed record DeleteFacturaVentaBorradorCommand(Guid Id) : IRequest<Result>;

/// <summary>Abierta -&gt; Liberada. Exige al menos una línea.</summary>
public sealed record LiberarFacturaVentaBorradorCommand(Guid Id) : IRequest<Result<FacturaVentaBorradorResponse>>;

/// <summary>Liberada -&gt; Abierta.</summary>
public sealed record ReabrirFacturaVentaBorradorCommand(Guid Id) : IRequest<Result<FacturaVentaBorradorResponse>>;
