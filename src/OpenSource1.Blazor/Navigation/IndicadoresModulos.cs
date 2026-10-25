using System.Globalization;
using System.Security.Claims;
using Microsoft.AspNetCore.Authorization;
using OpenSource1.Application.Security;
using OpenSource1.Blazor.Components.Documento;
using OpenSource1.Blazor.Services;
using OpenSource1.Core.Common;

namespace OpenSource1.Blazor.Navigation;

public sealed class IndicadoresModulos(
    IAuthorizationService autorizacion,
    ISocioNegocioApiClient socios,
    IProductoApiClient productos,
    ICategoriaProductoApiClient categorias,
    IAlmacenApiClient almacenes,
    IFacturaVentaApiClient facturas,
    INotaCreditoVentaApiClient notas,
    ICuentaContableApiClient cuentas,
    ITerminoPagoApiClient terminos,
    IUnidadMedidaApiClient unidades,
    ILogger<IndicadoresModulos> logger) : IIndicadoresModulos
{
    // Solo el total: una página de un elemento basta para leer PagedResult.Total.
    private static readonly PageRequest Uno = new(1, 1);

    /// <summary>Tiempo máximo por indicador: si vence, el indicador se omite como el que falla (la página no espera más).</summary>
    public TimeSpan TiempoMaximoPorIndicador { get; set; } = TimeSpan.FromSeconds(5);

    public async Task<IReadOnlyList<Indicador>> ObtenerAsync(string grupo, ClaimsPrincipal usuario, CancellationToken cancellationToken = default)
    {
        if (!(await autorizacion.AuthorizeAsync(usuario, ApplicationPolicies.CanConsult)).Succeeded)
        {
            return [];
        }

        var resultado = new List<Indicador>();
        foreach (var (titulo, ruta, contar) in Definiciones(grupo).Take(3))
        {
            try
            {
                using var limite = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                limite.CancelAfter(TiempoMaximoPorIndicador);
                // WaitAsync: el tiempo máximo se cumple aunque el cliente no atienda el token.
                var total = await contar(limite.Token).WaitAsync(limite.Token);
                resultado.Add(new Indicador(titulo, total.ToString("N0", CultureInfo.InvariantCulture), ruta));
            }
            catch (Exception ex) when (!cancellationToken.IsCancellationRequested)
            {
                logger.LogWarning(ex, "No fue posible calcular el indicador {Indicador} del grupo {Grupo}.", titulo, grupo);
            }
        }

        return resultado;
    }

    private List<(string Titulo, string? Ruta, Func<CancellationToken, Task<long>> Contar)> Definiciones(string grupo) => grupo switch
    {
        "clientes" =>
        [
            ("Clientes registrados", "/clientes", async ct => (await socios.ListAsync(null, Uno, ct)).Total),
        ],
        "productos" =>
        [
            ("Productos", "/productos", async ct => (await productos.ListAsync(null, Uno, ct)).Total),
            ("Sin existencia", "/productos?estado=without", async ct => (await productos.ListAsync(new ProductoSearchFilter(null, null, null, null, null, null, null, null, "without"), Uno, ct)).Total),
            ("Categorías", "/categorias-producto", async ct => (await categorias.ListAsync(null, Uno, ct)).Total),
        ],
        "inventario" =>
        [
            ("Almacenes", "/almacenes", async ct => (await almacenes.ListAsync(null, Uno, ct)).Total),
        ],
        "facturacion" =>
        [
            // En preparación = sin filtro de estado (facturas Abierta + Liberada; notas Abierta), como la pestaña de la lista.
            ("Facturas en preparación", RutasPreparacion.ListaFacturas, async ct => (await facturas.ListBorradoresAsync(null, Uno, ct)).Total),
            ("Facturas registradas", "/facturas-venta", async ct => (await facturas.ListFacturasAsync(null, Uno, ct)).Total),
            ("Notas de crédito en preparación", RutasPreparacion.ListaNotas, async ct => (await notas.ListBorradoresAsync(null, Uno, ct)).Total),
        ],
        "contabilidad" =>
        [
            ("Cuentas contables", "/cuentas-contables", async ct => (await cuentas.ListAsync(null, Uno, ct)).Total),
        ],
        "configuracion" =>
        [
            ("Términos de pago", "/terminos-pago", async ct => (await terminos.ListAsync(null, Uno, ct)).Total),
            ("Unidades de medida", "/unidades-medida", async ct => (await unidades.ListAsync(null, Uno, ct)).Total),
        ],
        _ => [],
    };
}
