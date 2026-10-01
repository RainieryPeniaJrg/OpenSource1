using OpenSource1.Application.Data.UnitOfWork;
using OpenSource1.Application.Features.FacturasVenta.Borradores.Handlers;
using OpenSource1.Application.Services.Contabilidad;
using OpenSource1.Application.Services.Inventario;
using OpenSource1.Core.Common;
using OpenSource1.Core.Entities;
using OpenSource1.Core.Entities.Contabilidad;
using OpenSource1.Core.Entities.Ventas;
using OpenSource1.Core.Enums;

namespace OpenSource1.Application.Features.FacturasVenta.Borradores;

/// <summary>Datos de una línea a validar, comunes al alta y la modificación.</summary>
internal sealed record LineaFacturaDatos(
    TipoLineaFactura Tipo,
    Guid? ProductoId,
    Guid? CuentaContableId,
    string? Descripcion,
    Guid? AlmacenId,
    Guid? UnidadMedidaId,
    decimal? Cantidad,
    decimal? PrecioUnitario,
    decimal? PorcentajeDescuentoLinea,
    Guid? GrupoIvaProductoId);

/// <summary>Valores definitivos de una línea ya validada, con todo lo congelado (factor, grupos, IVA) y los importes.</summary>
internal sealed record LineaFacturaCalculada(
    TipoLineaFactura Tipo,
    Guid? ProductoId,
    Guid? CuentaContableId,
    string? Descripcion,
    Guid? AlmacenId,
    Guid? UnidadMedidaId,
    decimal CantidadPorUnidadMedida,
    decimal Cantidad,
    decimal PrecioUnitario,
    decimal PorcentajeDescuentoLinea,
    decimal ImporteDescuentoLinea,
    decimal ImporteLinea,
    Guid? GrupoProductoId,
    Guid? GrupoIvaProductoId,
    Guid? GrupoInventarioId,
    string? IdentificadorIva,
    decimal PorcentajeIva)
{
    public void Aplicar(LineaFacturaVentaBorrador linea)
    {
        linea.Tipo = Tipo;
        linea.ProductoId = ProductoId;
        linea.CuentaContableId = CuentaContableId;
        linea.Descripcion = Descripcion;
        linea.AlmacenId = AlmacenId;
        linea.UnidadMedidaId = UnidadMedidaId;
        linea.CantidadPorUnidadMedida = CantidadPorUnidadMedida;
        linea.Cantidad = Cantidad;
        linea.PrecioUnitario = PrecioUnitario;
        linea.PorcentajeDescuentoLinea = PorcentajeDescuentoLinea;
        linea.ImporteDescuentoLinea = ImporteDescuentoLinea;
        linea.ImporteLinea = ImporteLinea;
        linea.GrupoProductoId = GrupoProductoId;
        linea.GrupoIvaProductoId = GrupoIvaProductoId;
        linea.GrupoInventarioId = GrupoInventarioId;
        linea.IdentificadorIva = IdentificadorIva;
        linea.PorcentajeIva = PorcentajeIva;
    }
}

/// <summary>
/// Validación y cálculo de una línea de borrador de factura (Task 6.2). Fail-fast: devuelve el primer error. Reutiliza los
/// criterios de <c>LineaDiarioReglas</c> (cotas de <c>numeric</c>, factor congelado vía
/// <see cref="IConversionUnidadMedidaService"/>, cantidad en unidad base acotada) y congela el IVA de
/// <see cref="IDerivadorCuentas.IvaAsync"/>(GrupoIvaNegocio de la cabecera × GrupoIvaProducto de la línea).
/// </summary>
internal static class LineaFacturaVentaBorradorReglas
{
    /// <summary>Máximo de <c>numeric(18,4)</c>.</summary>
    public const decimal ImporteMaximo = 99_999_999_999_999.9999m;

    /// <summary>Máximo de <c>numeric(18,6)</c>.</summary>
    public const decimal CantidadMaxima = 999_999_999_999.999999m;

    /// <summary>
    /// Alta de una línea nueva en <paramref name="cabecera"/> SIN guardar (lo hace el commit del llamador, dentro de su transacción):
    /// valida y calcula (<see cref="ValidarYCalcularAsync"/>), comprueba el tope de
    /// <see cref="CreateLineaFacturaVentaBorradorCommandHandler.MaximoLineasPorBorrador"/> líneas y asigna <c>NumeroLinea</c> = máximo + 10000.
    /// Única lógica de alta de línea: la usan el alta de una línea y el alta del borrador con su primera línea (spec alta rápida).
    /// </summary>
    public static async Task<Result<LineaFacturaVentaBorrador>> AgregarAsync(
        IUnitOfWork unitOfWork,
        IConversionUnidadMedidaService conversion,
        IDerivadorCuentas derivador,
        FacturaVentaBorrador cabecera,
        LineaFacturaDatos datos,
        CancellationToken cancellationToken)
    {
        var calculo = await ValidarYCalcularAsync(unitOfWork, conversion, derivador, cabecera, datos, cancellationToken);
        if (!calculo.TryObtenerValor(out var valores))
        {
            return Result<LineaFacturaVentaBorrador>.Fallo(calculo);
        }

        var lineasRepo = unitOfWork.Repository<LineaFacturaVentaBorrador>();
        var lineas = await lineasRepo.ListAsync(x => x.FacturaVentaBorradorId == cabecera.Id, cancellationToken);
        if (lineas.Count >= CreateLineaFacturaVentaBorradorCommandHandler.MaximoLineasPorBorrador)
        {
            return Result<LineaFacturaVentaBorrador>.Fallo(new Error(
                "factura.limite_lineas",
                $"El borrador ya tiene el máximo de {CreateLineaFacturaVentaBorradorCommandHandler.MaximoLineasPorBorrador} líneas.",
                "FacturaVentaBorradorId"));
        }

        var entity = new LineaFacturaVentaBorrador
        {
            FacturaVentaBorradorId = cabecera.Id,
            NumeroLinea = (lineas.Count == 0 ? 0 : lineas.Max(x => x.NumeroLinea)) + 10000,
        };
        valores.Aplicar(entity);
        await lineasRepo.AddAsync(entity, cancellationToken);
        return Result<LineaFacturaVentaBorrador>.Exito(entity);
    }

    public static async Task<Result<LineaFacturaCalculada>> ValidarYCalcularAsync(
        IUnitOfWork unitOfWork,
        IConversionUnidadMedidaService conversion,
        IDerivadorCuentas derivador,
        FacturaVentaBorrador cabecera,
        LineaFacturaDatos datos,
        CancellationToken cancellationToken)
    {
        var descripcion = FacturaVentaBorradorReglas.Normalizar(datos.Descripcion);
        if (FacturaVentaBorradorReglas.ValidarDescripcion(descripcion) is { } errorDescripcion)
        {
            return Result<LineaFacturaCalculada>.Fallo(errorDescripcion);
        }

        return datos.Tipo switch
        {
            TipoLineaFactura.Comentario => Comentario(datos, descripcion),
            TipoLineaFactura.Producto => await ProductoAsync(unitOfWork, conversion, derivador, cabecera, datos, descripcion, cancellationToken),
            TipoLineaFactura.CuentaContable => await CuentaAsync(unitOfWork, derivador, cabecera, datos, descripcion, cancellationToken),
            _ => Fallo("factura.tipo_linea_invalido", "El tipo de línea no es válido (Producto=1, CuentaContable=2, Comentario=3).", "Tipo")
        };
    }

    /// <summary>
    /// <c>ImporteDescuentoLinea = ROUND(Cantidad × Precio × % / 100, 2)</c> e <c>ImporteLinea = ROUND(Cantidad × Precio, 2) −
    /// ImporteDescuentoLinea</c>, <see cref="MidpointRounding.AwayFromZero"/>. <see langword="false"/> si desborda
    /// <c>decimal</c> o <c>numeric(18,4)</c>.
    /// </summary>
    public static bool TryCalcularImportes(
        decimal cantidad, decimal precioUnitario, decimal porcentajeDescuento, out decimal importeDescuento, out decimal importeLinea)
    {
        importeDescuento = 0m;
        importeLinea = 0m;
        try
        {
            var bruto = cantidad * precioUnitario;
            importeDescuento = Math.Round(bruto * porcentajeDescuento / 100m, 2, MidpointRounding.AwayFromZero);
            importeLinea = Math.Round(bruto, 2, MidpointRounding.AwayFromZero) - importeDescuento;
        }
        catch (OverflowException)
        {
            return false;
        }

        return Math.Abs(importeLinea) <= ImporteMaximo && Math.Abs(importeDescuento) <= ImporteMaximo;
    }

    private static Result<LineaFacturaCalculada> Comentario(LineaFacturaDatos datos, string? descripcion)
    {
        if (datos.ProductoId is not null || datos.CuentaContableId is not null || datos.AlmacenId is not null
            || datos.UnidadMedidaId is not null || datos.GrupoIvaProductoId is not null)
        {
            return Fallo("factura.campo_no_aplica", "Una línea de comentario solo admite la descripción.", "Tipo");
        }

        if (datos.Cantidad is not (null or 0m) || datos.PrecioUnitario is not (null or 0m) || datos.PorcentajeDescuentoLinea is not (null or 0m))
        {
            return Fallo("factura.campo_no_aplica", "Una línea de comentario no lleva cantidades, precios ni descuentos.", "Cantidad");
        }

        if (descripcion is null)
        {
            return Fallo("factura.descripcion_requerida", "La línea de comentario requiere una descripción.", "Descripcion");
        }

        return Result<LineaFacturaCalculada>.Exito(new LineaFacturaCalculada(
            TipoLineaFactura.Comentario, null, null, descripcion, null, null, 0m, 0m, 0m, 0m, 0m, 0m, null, null, null, null, 0m));
    }

    private static async Task<Result<LineaFacturaCalculada>> ProductoAsync(
        IUnitOfWork unitOfWork,
        IConversionUnidadMedidaService conversion,
        IDerivadorCuentas derivador,
        FacturaVentaBorrador cabecera,
        LineaFacturaDatos datos,
        string? descripcion,
        CancellationToken cancellationToken)
    {
        if (datos.CuentaContableId is not null || datos.GrupoIvaProductoId is not null)
        {
            return Fallo(
                "factura.campo_no_aplica",
                "Una línea de producto no admite cuenta contable ni grupo de IVA (se toma el del producto).",
                datos.CuentaContableId is not null ? "CuentaContableId" : "GrupoIvaProductoId");
        }

        // 1. Producto: existe, no borrado, no bloqueado para la venta (Venta ni Todo).
        var producto = datos.ProductoId is { } productoId
            ? await unitOfWork.Repository<Producto>().FirstOrDefaultAsync(x => x.Id == productoId, cancellationToken: cancellationToken)
            : null;
        if (producto is null || producto.Bloqueado != BloqueoProducto.Ninguno)
        {
            return Fallo("factura.producto_invalido", "El producto no existe o está bloqueado para la venta.", "ProductoId");
        }

        // 2. Grupos del producto: se congelan en la línea; los tres son necesarios para postear (ventas, IVA, inventario).
        if (producto.GrupoProductoId is not { } grupoProductoId)
        {
            return GrupoFaltante("el grupo contable de producto", producto.Codigo);
        }

        if (producto.GrupoIvaProductoId is not { } grupoIvaProductoId)
        {
            return GrupoFaltante("el grupo de IVA de producto", producto.Codigo);
        }

        if (producto.GrupoInventarioId is not { } grupoInventarioId)
        {
            return GrupoFaltante("el grupo de inventario", producto.Codigo);
        }

        // 3. Almacén: por defecto el de la cabecera; existe y no bloqueado.
        var almacenId = datos.AlmacenId ?? cabecera.AlmacenId;
        var almacen = await unitOfWork.Repository<Almacen>().FirstOrDefaultAsync(x => x.Id == almacenId, cancellationToken: cancellationToken);
        if (almacen is null || almacen.Bloqueado)
        {
            return Fallo("factura.almacen_invalido", "El almacén no existe o está bloqueado.", "AlmacenId");
        }

        // 4. Cantidad.
        if (ValidarCantidad(datos.Cantidad) is { } errorCantidad)
        {
            return Result<LineaFacturaCalculada>.Fallo(errorCantidad);
        }

        var cantidad = datos.Cantidad!.Value;

        // 5. Unidad (por defecto la base) y factor congelado, como en los diarios.
        var unidadMedidaId = datos.UnidadMedidaId ?? producto.UnidadMedidaBaseId;
        var conversionResultado = await conversion.ObtenerConversionAsync(producto.Id, unidadMedidaId, cancellationToken);
        if (!conversionResultado.TryObtenerValor(out var conversionUnidad))
        {
            var original = conversionResultado.Errores[0];
            return Result<LineaFacturaCalculada>.Fallo(new Error(original.Codigo, original.Mensaje, "UnidadMedidaId"));
        }

        // El factor congelado (redondeado a 6) es el mismo con el que el posteo convertirá. La cantidad en unidad base debe
        // caber en numeric(18,6) y ser EXACTA con los decimales de la unidad base: nunca se redondea (2.5 UND con 0 decimales
        // se rechaza; el inventario no puede decir 3 cuando la factura dice 2.5).
        var factor = conversionUnidad.Factor;
        var cantidadBaseResultado = conversionUnidad.ConvertirExacta(cantidad);
        if (cantidadBaseResultado.EsFallo)
        {
            var original = cantidadBaseResultado.Errores[0];
            return original.Codigo == "conversion.cantidad_no_exacta"
                ? Result<LineaFacturaCalculada>.Fallo(original with { Campo = "Cantidad" })
                : CantidadBaseDemasiadoGrande();
        }

        // 6. Precio: por defecto el PrecioVenta del producto (en unidad base) por el factor de la unidad de la línea. El precio
        // por defecto también debe ser > 0 (Task 8.4): un producto sin precio de venta exige informarlo.
        decimal precio;
        if (datos.PrecioUnitario is { } precioInformado)
        {
            precio = precioInformado;
        }
        else
        {
            try
            {
                precio = Math.Round(producto.PrecioVenta * factor, 4, MidpointRounding.AwayFromZero);
            }
            catch (OverflowException)
            {
                precio = decimal.MaxValue;
            }

            if (precio <= 0m)
            {
                return Fallo(
                    "factura.precio_invalido",
                    $"El producto {producto.Codigo} no tiene precio de venta en esa unidad: indique el precio unitario " +
                    $"(mayor que cero; {AyudaRegalo}).",
                    "PrecioUnitario");
            }
        }

        if (ValidarPrecio(precio) is { } errorPrecio)
        {
            return Result<LineaFacturaCalculada>.Fallo(errorPrecio);
        }

        return await CompletarAsync(
            derivador, cabecera, datos, TipoLineaFactura.Producto, producto.Id, null,
            descripcion ?? Truncar(producto.Nombre), almacen.Id, unidadMedidaId, factor, cantidad, precio,
            grupoProductoId, grupoIvaProductoId, grupoInventarioId, "ProductoId", cancellationToken);
    }

    private static async Task<Result<LineaFacturaCalculada>> CuentaAsync(
        IUnitOfWork unitOfWork,
        IDerivadorCuentas derivador,
        FacturaVentaBorrador cabecera,
        LineaFacturaDatos datos,
        string? descripcion,
        CancellationToken cancellationToken)
    {
        if (datos.ProductoId is not null || datos.AlmacenId is not null || datos.UnidadMedidaId is not null)
        {
            return Fallo(
                "factura.campo_no_aplica",
                "Una línea de cuenta contable no admite producto, almacén ni unidad de medida.",
                datos.ProductoId is not null ? "ProductoId" : datos.AlmacenId is not null ? "AlmacenId" : "UnidadMedidaId");
        }

        // 1. Cuenta: de Posteo, no bloqueada y de posteo directo (las cuentas "del sistema" no se capturan a mano).
        var cuenta = datos.CuentaContableId is { } cuentaId
            ? await unitOfWork.Repository<CuentaContable>().FirstOrDefaultAsync(x => x.Id == cuentaId, cancellationToken: cancellationToken)
            : null;
        if (cuenta is null || cuenta.TipoCuenta != TipoCuentaContable.Posteo || cuenta.Bloqueada || !cuenta.PosteoDirecto)
        {
            return Fallo(
                "factura.cuenta_invalida",
                "La cuenta contable no existe o no admite captura directa: debe ser de posteo, no bloqueada y de posteo directo.",
                "CuentaContableId");
        }

        // 2. Grupo de IVA de producto: obligatorio en este tipo (no hay producto del que tomarlo).
        if (datos.GrupoIvaProductoId is not { } grupoIvaProductoId)
        {
            return Fallo(
                "factura.grupo_faltante", "La línea de cuenta contable requiere el grupo de IVA de producto.", "GrupoIvaProductoId");
        }

        var grupoIva = await unitOfWork.Repository<GrupoIvaProducto>().FirstOrDefaultAsync(
            x => x.Id == grupoIvaProductoId, cancellationToken: cancellationToken);
        if (grupoIva is null)
        {
            return Fallo("factura.grupo_iva_invalido", "El grupo de IVA de producto indicado no existe.", "GrupoIvaProductoId");
        }

        // 3. Cantidad y precio (obligatorio: no hay precio por defecto).
        if (ValidarCantidad(datos.Cantidad) is { } errorCantidad)
        {
            return Result<LineaFacturaCalculada>.Fallo(errorCantidad);
        }

        if (datos.PrecioUnitario is not { } precio)
        {
            return Fallo("factura.precio_invalido", "La línea de cuenta contable requiere el precio unitario.", "PrecioUnitario");
        }

        if (ValidarPrecio(precio) is { } errorPrecio)
        {
            return Result<LineaFacturaCalculada>.Fallo(errorPrecio);
        }

        return await CompletarAsync(
            derivador, cabecera, datos, TipoLineaFactura.CuentaContable, null, cuenta.Id,
            descripcion ?? Truncar(cuenta.Nombre), null, null, 1m, datos.Cantidad!.Value, precio,
            null, grupoIvaProductoId, null, "GrupoIvaProductoId", cancellationToken);
    }

    /// <summary>Descuento, importes e IVA congelado: tramo común de Producto y CuentaContable.</summary>
    private static async Task<Result<LineaFacturaCalculada>> CompletarAsync(
        IDerivadorCuentas derivador,
        FacturaVentaBorrador cabecera,
        LineaFacturaDatos datos,
        TipoLineaFactura tipo,
        Guid? productoId,
        Guid? cuentaContableId,
        string? descripcion,
        Guid? almacenId,
        Guid? unidadMedidaId,
        decimal factor,
        decimal cantidad,
        decimal precio,
        Guid? grupoProductoId,
        Guid grupoIvaProductoId,
        Guid? grupoInventarioId,
        string campoIva,
        CancellationToken cancellationToken)
    {
        var porcentajeDescuento = datos.PorcentajeDescuentoLinea ?? 0m;
        if (porcentajeDescuento < 0m || porcentajeDescuento > 100m || decimal.Round(porcentajeDescuento, 5) != porcentajeDescuento)
        {
            return Fallo(
                "factura.descuento_invalido",
                "El porcentaje de descuento debe estar entre 0 y 100 y tener como máximo 5 decimales.", "PorcentajeDescuentoLinea");
        }

        if (!TryCalcularImportes(cantidad, precio, porcentajeDescuento, out var importeDescuento, out var importeLinea))
        {
            return Fallo("factura.importe_invalido", "El importe de la línea resultante es demasiado grande.", "Cantidad");
        }

        if (ValidarImporte(importeLinea, porcentajeDescuento) is { } errorImporte)
        {
            return Result<LineaFacturaCalculada>.Fallo(errorImporte);
        }

        var iva = await derivador.IvaAsync(cabecera.GrupoIvaNegocioId, grupoIvaProductoId, cancellationToken);
        if (!iva.TryObtenerValor(out var setupIva))
        {
            var original = iva.Errores[0];
            return Result<LineaFacturaCalculada>.Fallo(new Error(original.Codigo, original.Mensaje, campoIva));
        }

        return Result<LineaFacturaCalculada>.Exito(new LineaFacturaCalculada(
            tipo, productoId, cuentaContableId, descripcion, almacenId, unidadMedidaId, factor, cantidad, precio,
            porcentajeDescuento, importeDescuento, importeLinea, grupoProductoId, grupoIvaProductoId, grupoInventarioId,
            setupIva.IdentificadorIva, setupIva.PorcentajeIva));
    }

    private static Error? ValidarCantidad(decimal? cantidad) =>
        cantidad is not { } valor || valor <= 0 || valor > CantidadMaxima || decimal.Round(valor, 6) != valor
            ? new Error(
                "factura.cantidad_invalida",
                $"La cantidad es obligatoria, debe ser mayor que cero, hasta {CantidadMaxima:0.######} y tener como máximo 6 decimales.",
                "Cantidad")
            : null;

    /// <summary>Ayuda común a los errores de precio e importe 0 (Task 8.4).</summary>
    public const string AyudaRegalo = "para regalar, use 100 % de descuento";

    /// <summary>
    /// Precio unitario de una línea Producto o CuentaContable: mayor que cero (Task 8.4: el precio 0 está bloqueado; un regalo
    /// se factura con 100 % de descuento), menor que 1e14 y con 4 decimales como máximo. También lo revalida el posteo.
    /// </summary>
    public static Error? ValidarPrecio(decimal precio) =>
        precio <= 0 || precio > ImporteMaximo || decimal.Round(precio, 4) != precio
            ? new Error(
                "factura.precio_invalido",
                $"El precio unitario debe ser mayor que cero ({AyudaRegalo}), menor que 1e14 y tener como máximo 4 decimales.",
                "PrecioUnitario")
            : null;

    /// <summary>
    /// Importe de línea 0 solo con 100 % de descuento (Task 8.4). Con descuento parcial puede ocurrir por redondeo (cantidad ×
    /// precio &lt; 0.005). También lo revalida el posteo.
    /// </summary>
    public static Error? ValidarImporte(decimal importeLinea, decimal porcentajeDescuento) =>
        importeLinea == 0m && porcentajeDescuento != 100m
            ? new Error(
                "factura.importe_invalido",
                $"El importe de la línea resultante es 0: aumente la cantidad o el precio ({AyudaRegalo}).",
                "Cantidad")
            : null;

    private static string Truncar(string texto) =>
        texto.Length <= FacturaVentaBorradorErrores.LongitudDescripcion ? texto : texto[..FacturaVentaBorradorErrores.LongitudDescripcion];

    private static Result<LineaFacturaCalculada> CantidadBaseDemasiadoGrande() =>
        Fallo("factura.cantidad_invalida", "La cantidad, convertida a la unidad base del producto, es demasiado grande.", "Cantidad");

    private static Result<LineaFacturaCalculada> GrupoFaltante(string grupo, string codigoProducto) =>
        Fallo("factura.grupo_faltante", $"El producto {codigoProducto} no tiene {grupo}: es obligatorio para venderlo.", "ProductoId");

    private static Result<LineaFacturaCalculada> Fallo(string codigo, string mensaje, string campo) =>
        Result<LineaFacturaCalculada>.Fallo(new Error(codigo, mensaje, campo));
}
