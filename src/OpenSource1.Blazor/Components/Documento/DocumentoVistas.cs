using System.Globalization;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Forms;
using OpenSource1.Application.Features.ConfiguracionNumeracion.Dtos;
using OpenSource1.Application.Features.FacturasVenta.Borradores.Dtos;
using OpenSource1.Application.Features.FacturasVenta.Calculo;
using OpenSource1.Application.Features.FacturasVenta.Posteadas.Dtos;
using OpenSource1.Application.Features.NotasCreditoVenta.Borradores.Dtos;
using OpenSource1.Application.Features.NotasCreditoVenta.Posteadas.Dtos;
using OpenSource1.Blazor.Services;
using OpenSource1.Core.Common;
using OpenSource1.Core.Enums;

namespace OpenSource1.Blazor.Components.Documento;

/// <summary>
/// Un dato de la cabecera de un documento en solo lectura (spec no-series, Parte 4): <c>Href</c> lo convierte en enlace,
/// <c>Detalle</c> es una segunda línea tenue (RNC, dirección…), <c>Ancho</c> ocupa toda la fila (descripción) y <c>Contenido</c>,
/// si se indica, sustituye al valor (lo usa <see cref="CamposDocumento.Series"/>).
/// </summary>
public sealed record CampoDocumento(
    string Etiqueta, string Valor, string? Href = null, string? Detalle = null, bool Ancho = false, RenderFragment? Contenido = null);

/// <summary>Campos comunes de la cabecera en solo lectura.</summary>
public static class CamposDocumento
{
    /// <summary>
    /// Campo "Series" con <see cref="SerieNumeracionInfo"/> (I3: ÚNICA forma de mostrar la serie en modo lectura, en S13, S14 y S15).
    /// Borrador no Posteada: pasar el <see cref="ProximoVista"/>; Posteada o documento posteado: <paramref name="proximo"/> null.
    /// </summary>
    public static CampoDocumento Series(string? serieRegistroCodigo, ProximoVista? proximo = null) =>
        new("Series", serieRegistroCodigo ?? "—", Contenido: builder =>
        {
            builder.OpenComponent<SerieNumeracionInfo>(0);
            builder.AddComponentParameter(1, nameof(SerieNumeracionInfo.Codigo), serieRegistroCodigo);
            builder.AddComponentParameter(2, nameof(SerieNumeracionInfo.ProximoNumero), proximo?.Numero);
            builder.AddComponentParameter(3, nameof(SerieNumeracionInfo.Aviso), proximo?.Aviso);
            builder.AddComponentParameter(4, nameof(SerieNumeracionInfo.Error), proximo?.Error);
            builder.CloseComponent();
        });
}

/// <summary>
/// Totales al pie del documento: subtotal (antes de descuentos), descuentos, importe sin ITBIS, ITBIS y total, con el IVA agrupado
/// por identificador. <see cref="Desde"/> los arma desde los <see cref="TotalesFactura"/> de la API (vista previa del borrador o
/// factura/nota posteada) y la suma de descuentos de las líneas. <c>CuentasIva</c> (opcional, alineada con <c>Grupos</c>) es la
/// cuenta contable de cada grupo de IVA de un documento posteado.
/// </summary>
public sealed record TotalesDocumento(
    decimal Subtotal, decimal Descuentos, decimal ImporteSinIva, decimal Itbis, decimal Total, IReadOnlyList<GrupoIvaCalculado> Grupos,
    IReadOnlyList<string?>? CuentasIva = null)
{
    public static TotalesDocumento Desde(TotalesFactura totales, decimal descuentos) =>
        new(totales.ImporteSinIva + descuentos, descuentos, totales.ImporteSinIva, totales.ImporteIva, totales.ImporteTotal, totales.Grupos);

    /// <summary>Totales de una factura posteada: importes de la cabecera, grupos de IVA guardados y descuentos de sus líneas.</summary>
    public static TotalesDocumento Desde(FacturaVentaDetalleResponse d) => Posteado(
        d.Cabecera.ImporteSinIva, d.Cabecera.ImporteIva, d.Cabecera.ImporteTotal, d.Lineas.Sum(l => l.ImporteDescuentoLinea),
        d.LineasIva.Select(g => (new GrupoIvaCalculado(g.IdentificadorIva, g.PorcentajeIva, g.BaseImponible, g.ImporteIva), g.CuentaIvaNumero)));

    /// <summary>Totales de una nota de crédito posteada (misma aritmética que la factura).</summary>
    public static TotalesDocumento Desde(NotaCreditoVentaDetalleResponse d) => Posteado(
        d.Cabecera.ImporteSinIva, d.Cabecera.ImporteIva, d.Cabecera.ImporteTotal, d.Lineas.Sum(l => l.ImporteDescuentoLinea),
        d.LineasIva.Select(g => (new GrupoIvaCalculado(g.IdentificadorIva, g.PorcentajeIva, g.BaseImponible, g.ImporteIva), g.CuentaIvaNumero)));

    /// <summary>El subtotal es el importe sin ITBIS más los descuentos de línea (los importes guardados ya los descuentan).</summary>
    private static TotalesDocumento Posteado(
        decimal importeSinIva, decimal importeIva, decimal importeTotal, decimal descuentos, IEnumerable<(GrupoIvaCalculado Grupo, string? Cuenta)> grupos)
    {
        var lista = grupos.ToList();
        return new(importeSinIva + descuentos, descuentos, importeSinIva, importeIva, importeTotal, [.. lista.Select(g => g.Grupo)], [.. lista.Select(g => g.Cuenta)]);
    }
}

/// <summary>Variante de <see cref="DocumentoFilaFormulario"/>: <c>Alta</c> = <c>fila-nueva</c> (verde), <c>Edicion</c> = <c>fila-edicion</c> (ámbar).</summary>
public enum VarianteFilaFormulario
{
    Alta,
    Edicion,
}

/// <summary>Columna de <see cref="DocumentoLineasLectura{TLinea}"/>: título, valor (texto), alineación, clase de la celda y detalle tenue.</summary>
public sealed record ColumnaDocumento<TLinea>(
    string Titulo, Func<TLinea, string?> Valor, bool Derecha = false, string? Clase = null, Func<TLinea, string?>? Detalle = null);

/// <summary>
/// Línea de factura o nota en solo lectura (NS20), común a borradores y documentos posteados. <c>Devolucion</c> solo aplica a notas
/// (null en facturas). Las de comentario dejan vacías las columnas numéricas.
/// </summary>
public sealed record LineaDocumento(
    int NumeroLinea, TipoLineaFactura Tipo, string? Codigo, string? Descripcion, string? Almacen, decimal Cantidad, string? Unidad,
    decimal PrecioUnitario, decimal PorcentajeDescuento, decimal ImporteLinea, string? IdentificadorIva, decimal PorcentajeIva,
    bool? Devolucion = null)
{
    public bool EsComentario => Tipo == TipoLineaFactura.Comentario;

    public static LineaDocumento Desde(LineaFacturaVentaBorradorResponse l) => new(
        l.NumeroLinea, l.Tipo, l.ProductoCodigo ?? l.CuentaContableNumero, l.Descripcion, l.AlmacenCodigo, l.Cantidad, l.UnidadMedidaCodigo,
        l.PrecioUnitario, l.PorcentajeDescuentoLinea, l.ImporteLinea, l.IdentificadorIva, l.PorcentajeIva);

    public static LineaDocumento Desde(LineaFacturaVentaResponse l) => new(
        l.NumeroLinea, l.Tipo, l.ProductoCodigo ?? l.CuentaContableNumero, l.Descripcion, l.AlmacenCodigo, l.Cantidad, l.UnidadMedidaCodigo,
        l.PrecioUnitario, l.PorcentajeDescuentoLinea, l.ImporteLinea, l.IdentificadorIva, l.PorcentajeIva);

    public static LineaDocumento Desde(LineaNotaCreditoVentaBorradorResponse l) => new(
        l.NumeroLinea, l.Tipo, l.ProductoCodigo ?? l.CuentaContableNumero, l.Descripcion, l.AlmacenCodigo, l.Cantidad, l.UnidadMedidaCodigo,
        l.PrecioUnitario, l.PorcentajeDescuentoLinea, l.ImporteLinea, l.IdentificadorIva, l.PorcentajeIva, l.DevolverInventario);

    public static LineaDocumento Desde(LineaNotaCreditoVentaResponse l) => new(
        l.NumeroLinea, l.Tipo, l.ProductoCodigo ?? l.CuentaContableNumero, l.Descripcion, l.AlmacenCodigo, l.Cantidad, l.UnidadMedidaCodigo,
        l.PrecioUnitario, l.PorcentajeDescuentoLinea, l.ImporteLinea, l.IdentificadorIva, l.PorcentajeIva, l.DevolverInventario);
}

/// <summary>Los dos usos fijos de <see cref="DocumentoLineasLectura{TLinea}"/> (I2): líneas de factura (10 columnas) y de nota (11).</summary>
public static class ColumnasDocumento
{
    private static readonly CultureInfo Inv = CultureInfo.InvariantCulture;

    /// <summary># · Tipo · Producto / Cuenta · Descripción · Almacén · Cantidad (+unidad) · Precio · Desc. % · Importe · IVA.</summary>
    public static readonly IReadOnlyList<ColumnaDocumento<LineaDocumento>> Factura = Comunes();

    /// <summary>Las de <see cref="Factura"/> + Devolución (Sí/No).</summary>
    public static readonly IReadOnlyList<ColumnaDocumento<LineaDocumento>> Nota =
        [.. Comunes(), new("Devolución", l => l.EsComentario ? null : l.Devolucion == true ? "Sí" : "No", Clase: "text-xs text-slate-500")];

    private static List<ColumnaDocumento<LineaDocumento>> Comunes() =>
    [
        new("#", l => l.NumeroLinea.ToString(Inv), Clase: "text-xs text-slate-400"),
        new("Tipo", l => VentasOpciones.TipoLinea(l.Tipo), Clase: "text-xs text-slate-700"),
        new("Producto / Cuenta", l => l.Codigo ?? "—", Clase: "text-xs text-slate-700"),
        new("Descripción", l => l.Descripcion),
        new("Almacén", l => l.Almacen ?? "—", Clase: "text-xs text-slate-500"),
        new("Cantidad", l => l.EsComentario ? null : l.Cantidad.ToString("0.######", Inv), Derecha: true, Detalle: l => l.EsComentario ? null : l.Unidad),
        new("Precio", l => l.EsComentario ? null : l.PrecioUnitario.ToString("0.00##", Inv), Derecha: true),
        new("Desc. %", l => l.EsComentario ? null : l.PorcentajeDescuento == 0 ? "—" : l.PorcentajeDescuento.ToString("0.#####", Inv), Derecha: true, Clase: "text-slate-500"),
        new("Importe", l => l.EsComentario ? null : VentasOpciones.Importe(l.ImporteLinea), Derecha: true, Clase: "font-semibold text-slate-800"),
        new("IVA", l => l.EsComentario ? null : $"{l.IdentificadorIva} {l.PorcentajeIva.ToString("0.##", Inv)}%", Clase: "text-xs text-slate-500"),
    ];
}

/// <summary>Un dato derivado en solo lectura (spec alta rápida): <see cref="DatosDerivados"/> lo pinta como <c>&lt;dt&gt;/&lt;dd&gt;</c>.</summary>
public sealed record DatoDerivado(string Etiqueta, string Valor, string? Href = null);

/// <summary>Valor del botón que envía la cabecera editable (<c>name="UpdateInput.Accion"</c>): qué hacer después de guardarla.</summary>
public static class AccionCabecera
{
    /// <summary>Guardar y abrir la confirmación de "Registrar".</summary>
    public const string Registrar = "registrar";

    /// <summary>Guardar y volver a la lista "En preparación".</summary>
    public const string Despues = "despues";
}

/// <summary>Numeración vista desde la UI del documento (spec alta rápida, Parte 1.5).</summary>
public static partial class NumeracionDocumento
{
    /// <summary>
    /// Motivo para no capturar ni registrar: falta la fila de configuración de <paramref name="tipoBorrador"/> o de
    /// <paramref name="tipoRegistro"/>, o su serie está inactiva. <see langword="null"/> = se puede; también si la configuración no
    /// cargó (<paramref name="configuracion"/> null): no se bloquea por un fallo de lectura, la API valida al guardar (como F7).
    /// </summary>
    public static string? MotivoBloqueo(
        IReadOnlyList<ConfiguracionNumeracionResponse>? configuracion, TipoDocumentoSerie tipoBorrador, TipoDocumentoSerie tipoRegistro, string documentos)
    {
        if (configuracion is null)
        {
            return null;
        }

        var falta = new[] { tipoBorrador, tipoRegistro }.Any(t => Configurada(configuracion, t) is null);
        return falta
            ? $"No hay una serie configurada para {documentos} (o la serie configurada está inactiva): no se puede preparar ni registrar."
            : null;
    }

    /// <summary>Fila de configuración del tipo con su serie activa; <see langword="null"/> si no hay o está inactiva.</summary>
    public static ConfiguracionNumeracionResponse? Configurada(IReadOnlyList<ConfiguracionNumeracionResponse>? configuracion, TipoDocumentoSerie tipo) =>
        configuracion?.FirstOrDefault(c => c.TipoDocumento == tipo && c.SerieActiva);

    /// <summary>Número de línea de un mensaje de la API ("Línea 20000: …") para enlazar a su fila <c>#linea-20000</c>; si no, null.</summary>
    public static int? LineaDelMensaje(string mensaje)
    {
        var coincidencia = PrefijoLinea().Match(mensaje);
        return coincidencia.Success && int.TryParse(coincidencia.Groups[1].Value, out var numero) ? numero : null;
    }

    [GeneratedRegex(@"\ALínea (\d{1,9}):")]
    private static partial Regex PrefijoLinea();
}

/// <summary>Rutas de los documentos en preparación (spec alta rápida, Parte 2).</summary>
public static class RutasPreparacion
{
    public const string Factura = "/facturas-venta/en-preparacion";
    public const string Nota = "/notas-credito-venta/en-preparacion";
    public const string ListaFacturas = "/facturas-venta?estado=preparacion";
    public const string ListaNotas = "/notas-credito-venta?estado=preparacion";

    /// <summary>Destino de una ruta antigua (<c>…/borradores/{id}</c>): la nueva, con el <c>returnUrl</c> solo si es local (si no, la lista).</summary>
    public static string Redireccion(string baseRuta, Guid id, string? returnUrl, string listaPorDefecto)
    {
        var destino = $"{baseRuta}/{id}";
        return string.IsNullOrWhiteSpace(returnUrl)
            ? destino
            : RetornoLocal.ConRetorno(destino, RetornoLocal.Validar(returnUrl, listaPorDefecto));
    }

    /// <summary>Documento en preparación con la pestaña de tipo de línea de la fila de alta (sin query para Producto, el tipo por defecto).</summary>
    public static string ConTipo(string baseRuta, Guid id, TipoLineaFactura tipo) =>
        tipo == TipoLineaFactura.Producto ? $"{baseRuta}/{id}" : $"{baseRuta}/{id}?tipo={(int)tipo}";

    /// <summary>
    /// Destino tras guardar la cabecera editable según el botón que la envió (<see cref="AccionCabecera"/>, el
    /// <c>{Prefijo}.Accion</c> de <see cref="BotonesPreparacion"/>): "Registrar …" abre la confirmación (<paramref name="urlRegistrar"/>),
    /// "Guardar para después" vuelve a la lista en preparación (<paramref name="listaPreparacion"/>, <c>ok=guardada</c>) y cualquier
    /// otro valor ("Guardar cambios") se queda en el documento (<paramref name="urlPropia"/>, <c>ok=modificado</c>).
    /// </summary>
    public static string DestinoTrasGuardar(string? accion, string urlRegistrar, string urlPropia, string listaPreparacion) => accion switch
    {
        AccionCabecera.Registrar => urlRegistrar,
        AccionCabecera.Despues => RetornoLocal.ConParametro(listaPreparacion, "ok", "guardada"),
        _ => RetornoLocal.ConParametro(urlPropia, "ok", "modificado"),
    };
}

/// <summary>Textos derivados comunes de la cabecera.</summary>
public static class DatosDocumento
{
    /// <summary>"Razón social RNC" o "—".</summary>
    public static string Fiscal(string? razonSocial, string? numeroDocumento) =>
        string.IsNullOrWhiteSpace($"{razonSocial} {numeroDocumento}") ? "—" : $"{razonSocial} {numeroDocumento}".Trim();
}

/// <summary>
/// Lógica común de las listas "En preparación" (<see cref="ListaFacturasEnPreparacion"/> y <see cref="ListaNotasEnPreparacion"/>;
/// spec alta rápida, Parte 2): página de la API, URL de la lista con sus filtros, mensajes de <c>?ok=</c> y el descarte.
/// </summary>
public static class ListaPreparacion
{
    /// <summary>Insignia de estado de un documento en preparación (texto, no solo color).</summary>
    public const string CssEstadoAbierta = "inline-flex rounded-full bg-green-50 px-2 py-0.5 font-semibold text-green-700";

    /// <summary>Insignia de una factura heredada Liberada.</summary>
    public const string CssEstadoLiberada = "inline-flex rounded-full bg-amber-50 px-2 py-0.5 font-semibold text-amber-700";

    /// <summary>
    /// Mensaje del <c>?ok=</c> con que vuelven a la lista la página del documento ("Guardar para después" → <c>guardada</c>,
    /// "Descartar" → <c>descartada</c>) y el descarte desde la propia lista; <c>eliminado</c> es el de la versión anterior.
    /// <paramref name="documento"/> en minúscula y femenino ("factura", "nota de crédito").
    /// </summary>
    public static string? MensajeOk(string? ok, string documento) => ok switch
    {
        "descartada" or "eliminado" => $"{char.ToUpperInvariant(documento[0])}{documento[1..]} descartada.",
        "guardada" => $"La {documento} quedó guardada en preparación.",
        _ => null,
    };

    /// <summary>Página pedida a la API: lo último creado primero.</summary>
    public static PageRequest Peticion(int pagina) => new(pagina, PageRequest.TamanoPorDefecto, "CreatedAtUtc", Descendente: true);

    /// <summary>Carga la página (mínimo 1); si la pedida ya no existe, la última que haya.</summary>
    public static async Task<(PagedResult<T> Resultado, int Pagina)> CargarAsync<T>(Func<PageRequest, Task<PagedResult<T>>> listar, int pagina)
    {
        pagina = Math.Max(pagina, 1);
        var resultado = await listar(Peticion(pagina));
        if (resultado.TotalPaginas > 0 && pagina > resultado.TotalPaginas)
        {
            pagina = resultado.TotalPaginas;
            resultado = await listar(Peticion(pagina));
        }

        return (resultado, pagina);
    }

    /// <summary>URL de la lista (<see cref="RutasPreparacion.ListaFacturas"/> o <see cref="RutasPreparacion.ListaNotas"/>) con los filtros no vacíos, la página y <c>deleteId</c>.</summary>
    public static string Url(string lista, IEnumerable<(string Clave, string? Valor)> filtros, int pagina, Guid? deleteId = null)
    {
        var url = lista;
        foreach (var (clave, valor) in filtros)
        {
            if (Vacio(valor) is { } v)
            {
                url = RetornoLocal.ConParametro(url, clave, v);
            }
        }

        if (pagina > 1) url = RetornoLocal.ConParametro(url, "pagina", pagina.ToString(CultureInfo.InvariantCulture));
        if (deleteId is { } id) url = RetornoLocal.ConParametro(url, "deleteId", id.ToString());
        return url;
    }

    /// <summary>
    /// Descarta en la API: devuelve el destino (<paramref name="lista"/> con <c>ok=descartada</c>) o el mensaje y los errores reales.
    /// La página navega FUERA de su try (R17).
    /// </summary>
    public static async Task<(string? Destino, string? Error, IReadOnlyList<string> Errores)> DescartarAsync(
        Func<Task<VentaOperationResult<bool>>> borrar, string lista, ILogger logger, string errorGenerico)
    {
        try
        {
            var result = await borrar();
            return result.Succeeded
                ? (RetornoLocal.ConParametro(lista, "ok", "descartada"), null, [])
                : (null, result.Message, result.Errors ?? []);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not discard a document en preparación.");
            return (null, errorGenerico, []);
        }
    }

    public static string? Vacio(string? valor) => string.IsNullOrWhiteSpace(valor) ? null : valor.Trim();
}

/// <summary>Formulario de "Descartar" de las listas en preparación (<c>delete-borrador</c> / <c>delete-borrador-nota</c>): solo el id.</summary>
public sealed class DescartarDocumentoForm
{
    public Guid Id { get; set; }
}

/// <summary>
/// Errores de validación de un formulario (DataAnnotations / IValidatableObject) hacia el ResumenErrores único de la página (R9-C1):
/// el <c>OnInvalidSubmit</c> del EditForm los vuelca aquí para que el resumen (role="alert") aparezca y app.errores.js lo enfoque,
/// igual que con los errores de la API. Con <c>numeroLinea</c> cada mensaje lleva "Línea N: " y el resumen enlaza a la fila #linea-N.
/// </summary>
public static class ErroresFormulario
{
    public const string Mensaje = "Revise los datos marcados.";

    public static IReadOnlyList<string> Desde(EditContext contexto, int? numeroLinea = null) =>
        [.. contexto.GetValidationMessages().Distinct().Select(m => numeroLinea is { } n ? $"Línea {n}: {m}" : m)];
}
