using OpenSource1.Application.Features.Almacenes.Dtos;
using OpenSource1.Application.Features.CuentasContables.Dtos;
using OpenSource1.Application.Features.SociosNegocio.Dtos;
using OpenSource1.Blazor.Services;
using OpenSource1.Core.Common;
using OpenSource1.Core.Enums;

namespace OpenSource1.Blazor.Components;

/// <summary>Opciones de un <c>&lt;select&gt;</c> cargadas de la API y si su carga falló (la página bloquea Guardar entonces).</summary>
public sealed record OpcionesCargadas<T>(IReadOnlyList<T> Items, bool CargaFallida)
{
    public static readonly OpcionesCargadas<T> SinCargar = new([], false);
}

/// <summary>
/// Cargas de opciones de las páginas de ventas (Task 6.6), con el tiempo máximo común de <see cref="ProductoOpciones.TiempoMaximo"/>:
/// socios (buscados por nombre, primeros <see cref="MaximoSocios"/>), almacenes, cuentas de captura directa (Posteo, no
/// bloqueadas, <c>PosteoDirecto</c>) y grupos de IVA de producto.
/// </summary>
public static class VentasOpciones
{
    public const int MaximoSocios = 50;

    /// <summary>Número de la cuenta de caja que la API usa por defecto en los cobros.</summary>
    public const string NumeroCuentaCajaPorDefecto = "1101";

    public static async Task<OpcionesCargadas<SocioNegocioResponse>> SociosAsync(
        ISocioNegocioApiClient client, string? busqueda, ILogger logger, CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(ProductoOpciones.TiempoMaximo);
        try
        {
            var filtro = new SocioNegocioSearchFilter(
                string.IsNullOrWhiteSpace(busqueda) ? null : busqueda.Trim(), null, null, null, null, null);
            var pagina = await client.ListAsync(filtro, new PageRequest(1, MaximoSocios, "NombreComercial", Descendente: false), cts.Token);
            return new(pagina.Items, false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not load socios de negocio options from API.");
            return new([], true);
        }
    }

    public static async Task<OpcionesCargadas<AlmacenResponse>> AlmacenesAsync(
        IAlmacenApiClient client, ILogger logger, CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(ProductoOpciones.TiempoMaximo);
        try
        {
            var pagina = await client.ListAsync(null, new PageRequest(1, PageRequest.TamanoMaximo, "Codigo", Descendente: false), cts.Token);
            return new([.. pagina.Items.Where(a => !a.Bloqueado)], false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not load almacenes options from API.");
            return new([], true);
        }
    }

    /// <summary>Cuentas de Posteo, no bloqueadas y de posteo directo (todas las páginas): las únicas que la API acepta a mano.</summary>
    public static async Task<OpcionesCargadas<CuentaContableResponse>> CuentasCapturaDirectaAsync(
        ICuentaContableApiClient client, ILogger logger, CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(ProductoOpciones.TiempoMaximo);
        try
        {
            var todas = new List<CuentaContableResponse>();
            var filtro = new CuentaContableSearchFilter(null, null, TipoCuentaContable.Posteo, null, Bloqueada: false);
            for (var pagina = 1; ; pagina++)
            {
                var resultado = await client.ListAsync(filtro, new PageRequest(pagina, PageRequest.TamanoMaximo, "Numero", Descendente: false), cts.Token);
                todas.AddRange(resultado.Items.Where(c => c.PosteoDirecto && !c.Bloqueada && c.TipoCuenta == TipoCuentaContable.Posteo));
                if (resultado.Items.Count == 0 || pagina >= resultado.TotalPaginas)
                {
                    break;
                }
            }

            return new(todas, false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not load cuentas contables options from API.");
            return new([], true);
        }
    }

    public static async Task<OpcionesCargadas<GrupoOpcion>> GruposIvaProductoAsync(
        IGrupoContableApiClient client, ILogger logger, CancellationToken cancellationToken = default)
    {
        using var cts = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        cts.CancelAfter(ProductoOpciones.TiempoMaximo);
        try
        {
            return new(GruposOpciones.Mapear(await client.ListAllAsync(TipoGrupoContable.IvaProducto, cts.Token)), false);
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Could not load grupos de IVA de producto from API.");
            return new([], true);
        }
    }

    public static string Estado(EstadoFacturaBorrador estado) => estado switch
    {
        EstadoFacturaBorrador.Abierta => "En preparación",
        EstadoFacturaBorrador.Liberada => "En preparación (liberada)",
        EstadoFacturaBorrador.Posteada => "Registrada",
        _ => estado.ToString()
    };

    public static string EstadoNota(EstadoNotaCreditoBorrador estado) => estado switch
    {
        EstadoNotaCreditoBorrador.Abierta => "En preparación",
        EstadoNotaCreditoBorrador.Posteada => "Registrada",
        _ => estado.ToString()
    };

    public static string TipoLinea(TipoLineaFactura tipo) => tipo switch
    {
        TipoLineaFactura.Producto => "Producto",
        TipoLineaFactura.CuentaContable => "Cuenta contable",
        TipoLineaFactura.Comentario => "Comentario",
        _ => tipo.ToString()
    };

    public static string TipoDocumento(TipoDocumentoCliente tipo) => tipo switch
    {
        TipoDocumentoCliente.Factura => "Factura",
        TipoDocumentoCliente.NotaCredito => "Nota de crédito",
        TipoDocumentoCliente.Pago => "Pago",
        TipoDocumentoCliente.Ajuste => "Ajuste",
        _ => tipo.ToString()
    };

    /// <summary>Importe con 2 decimales y separador de miles, independiente de la cultura del servidor.</summary>
    public static string Importe(decimal valor) => valor.ToString("#,##0.00", System.Globalization.CultureInfo.InvariantCulture);
}
