using OpenSource1.Application.Data.UnitOfWork;
using OpenSource1.Core.Common;
using OpenSource1.Core.Entities;
using OpenSource1.Core.Entities.Ventas;
using OpenSource1.Core.Enums;

namespace OpenSource1.Application.Features.FacturasVenta.Borradores;

/// <summary>Errores comunes de los borradores de factura (404 solo para el recurso de la RUTA).</summary>
internal static class FacturaVentaBorradorErrores
{
    public const int LongitudDescripcion = 200;

    public static Error BorradorNoEncontrado(string campo = "Id") =>
        new("factura_borrador.no_encontrado", "No se encontró el borrador de factura solicitado.", campo);

    public static Error LineaNoEncontrada() =>
        new("factura_linea.no_encontrado", "No se encontró la línea de factura solicitada.", "Id");

    public static Error Liberada(string campo = "Id") =>
        new("factura.liberada", "El borrador está liberado: reábralo para modificarlo.", campo);

    /// <summary>Un borrador <c>Posteada</c> es de solo lectura (spec no-series): toda escritura sobre él o sus líneas -&gt; 409.</summary>
    public static Error Posteada(string campo = "Id") =>
        new("factura_borrador.posteada.conflicto", "El borrador ya se posteó: es de solo lectura (abra su factura).", campo);

    /// <summary>El almacén de la cabecera lo fija el servidor: siempre el predeterminado (spec alta rápida).</summary>
    public static Error AlmacenNoEditable() =>
        new("almacen.no_editable", "El almacén no se elige: el documento usa el almacén predeterminado.", "AlmacenId");
}

/// <summary>Datos que un borrador toma del socio al asignarlo: snapshot del facturar-a, su término y los grupos congelados.</summary>
internal sealed record SnapshotSocios(
    Guid SocioNegocioId,
    Guid SocioNegocioFacturarAId,
    string NombreFacturacion,
    string? RazonSocialFacturacion,
    TipoDocumentoFiscal TipoDocumentoFiscal,
    string? NumeroDocumentoFiscal,
    string? DireccionFacturacionLinea1,
    string? DireccionFacturacionLinea2,
    string? CiudadFacturacion,
    string? PaisCodigoFacturacion,
    Guid? TerminoPagoId,
    Guid GrupoNegocioId,
    Guid GrupoIvaNegocioId,
    Guid GrupoClienteContableId)
{
    public void Aplicar(FacturaVentaBorrador borrador)
    {
        borrador.SocioNegocioId = SocioNegocioId;
        borrador.SocioNegocioFacturarAId = SocioNegocioFacturarAId;
        borrador.NombreFacturacion = NombreFacturacion;
        borrador.RazonSocialFacturacion = RazonSocialFacturacion;
        borrador.TipoDocumentoFiscal = TipoDocumentoFiscal;
        borrador.NumeroDocumentoFiscal = NumeroDocumentoFiscal;
        borrador.DireccionFacturacionLinea1 = DireccionFacturacionLinea1;
        borrador.DireccionFacturacionLinea2 = DireccionFacturacionLinea2;
        borrador.CiudadFacturacion = CiudadFacturacion;
        borrador.PaisCodigoFacturacion = PaisCodigoFacturacion;
        borrador.TerminoPagoId = TerminoPagoId;
        borrador.GrupoNegocioId = GrupoNegocioId;
        borrador.GrupoIvaNegocioId = GrupoIvaNegocioId;
        borrador.GrupoClienteContableId = GrupoClienteContableId;
    }
}

/// <summary>Reglas de la cabecera del borrador, compartidas por el alta y la modificación.</summary>
internal static class FacturaVentaBorradorReglas
{
    /// <summary>
    /// Valida los socios (existen, no borrados, no <see cref="BloqueoSocioNegocio.Todo"/>) y construye el snapshot: datos de
    /// facturación y término del facturar-a; <c>GrupoNegocioId</c>/<c>GrupoIvaNegocioId</c> del vender-a y
    /// <c>GrupoClienteContableId</c> del facturar-a (nulos -&gt; 400 <c>factura.grupo_faltante</c> con el grupo).
    /// </summary>
    public static async Task<Result<SnapshotSocios>> TomarSnapshotAsync(
        IUnitOfWork unitOfWork, Guid venderAId, Guid facturarAId, CancellationToken cancellationToken)
    {
        var socios = unitOfWork.Repository<SocioNegocio>();

        var venderA = await socios.FirstOrDefaultAsync(x => x.Id == venderAId, cancellationToken: cancellationToken);
        if (venderA is null || venderA.Bloqueado == BloqueoSocioNegocio.Todo)
        {
            return Result<SnapshotSocios>.Fallo(new Error(
                "factura.socio_invalido", "El socio vender-a no existe o está bloqueado.", "SocioNegocioId"));
        }

        var facturarA = facturarAId == venderAId
            ? venderA
            : await socios.FirstOrDefaultAsync(x => x.Id == facturarAId, cancellationToken: cancellationToken);
        if (facturarA is null || facturarA.Bloqueado == BloqueoSocioNegocio.Todo)
        {
            return Result<SnapshotSocios>.Fallo(new Error(
                "factura.socio_invalido", "El socio facturar-a no existe o está bloqueado.", "SocioNegocioFacturarAId"));
        }

        if (venderA.GrupoNegocioId is not { } grupoNegocioId)
        {
            return Result<SnapshotSocios>.Fallo(GrupoFaltante("el grupo de negocio", venderA.Codigo, "GrupoNegocioId"));
        }

        if (venderA.GrupoIvaNegocioId is not { } grupoIvaNegocioId)
        {
            return Result<SnapshotSocios>.Fallo(GrupoFaltante("el grupo de IVA de negocio", venderA.Codigo, "GrupoIvaNegocioId"));
        }

        if (facturarA.GrupoClienteContableId is not { } grupoClienteId)
        {
            return Result<SnapshotSocios>.Fallo(GrupoFaltante("el grupo contable de cliente", facturarA.Codigo, "GrupoClienteContableId"));
        }

        return Result<SnapshotSocios>.Exito(new SnapshotSocios(
            venderA.Id,
            facturarA.Id,
            facturarA.NombreComercial,
            facturarA.RazonSocial,
            facturarA.TipoDocumentoFiscal,
            facturarA.NumeroDocumentoFiscal,
            facturarA.Direccion?.Linea1,
            facturarA.Direccion?.Linea2,
            facturarA.Ciudad,
            facturarA.Pais?.Codigo,
            facturarA.TerminoPagoId,
            grupoNegocioId,
            grupoIvaNegocioId,
            grupoClienteId));
    }

    /// <summary><c>FechaDocumento</c> + días del término (si el término no existe o se borró, sin días).</summary>
    public static async Task<DateOnly> CalcularVencimientoAsync(
        IUnitOfWork unitOfWork, DateOnly fechaDocumento, Guid? terminoPagoId, CancellationToken cancellationToken)
    {
        if (terminoPagoId is not { } id)
        {
            return fechaDocumento;
        }

        var termino = await unitOfWork.Repository<TerminoPago>().FirstOrDefaultAsync(x => x.Id == id, cancellationToken: cancellationToken);
        return termino is null ? fechaDocumento : fechaDocumento.AddDays(termino.DiasVencimiento);
    }

    public static Error? ValidarFechas(DateOnly fechaRegistro, DateOnly fechaDocumento, DateOnly fechaVencimiento)
    {
        if (fechaRegistro == default)
        {
            return new Error("factura.fecha_invalida", "La fecha de registro no es válida.", "FechaRegistro");
        }

        if (fechaDocumento == default)
        {
            return new Error("factura.fecha_invalida", "La fecha de documento no es válida.", "FechaDocumento");
        }

        return fechaVencimiento < fechaDocumento
            ? new Error("factura.fecha_invalida", "La fecha de vencimiento no puede ser anterior a la fecha de documento.", "FechaVencimiento")
            : null;
    }

    /// <summary>Almacén indicado (existe y no bloqueado) o, si no se indica, el predeterminado.</summary>
    public static async Task<Result<Guid>> ResolverAlmacenAsync(IUnitOfWork unitOfWork, Guid? almacenId, CancellationToken cancellationToken)
    {
        var almacenes = unitOfWork.Repository<Almacen>();
        var almacen = almacenId is { } id
            ? await almacenes.FirstOrDefaultAsync(x => x.Id == id, cancellationToken: cancellationToken)
            : await almacenes.FirstOrDefaultAsync(x => x.EsPredeterminado, cancellationToken: cancellationToken);

        return almacen is null || almacen.Bloqueado
            ? Result<Guid>.Fallo(new Error(
                "factura.almacen_invalido",
                almacenId is null ? "No hay un almacén predeterminado disponible." : "El almacén no existe o está bloqueado.",
                "AlmacenId"))
            : Result<Guid>.Exito(almacen.Id);
    }

    public static Error? ValidarDescripcion(string? descripcion) =>
        descripcion is { Length: > FacturaVentaBorradorErrores.LongitudDescripcion }
            ? new Error("factura.descripcion_invalida", "La descripción admite como máximo 200 caracteres.", "Descripcion")
            : null;

    public static string? Normalizar(string? valor) => string.IsNullOrWhiteSpace(valor) ? null : valor.Trim();

    private static Error GrupoFaltante(string grupo, string codigoSocio, string campo) => new(
        "factura.grupo_faltante",
        $"El socio {codigoSocio} no tiene {grupo}: es obligatorio para facturarle.",
        campo);
}
