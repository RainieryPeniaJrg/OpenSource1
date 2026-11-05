using System.Globalization;
using System.Net.Http.Json;
using OpenSource1.Application.Features.Reportes;
using OpenSource1.Application.Features.MovimientosCliente.Dtos;
using OpenSource1.Blazor.Reporting;

namespace OpenSource1.Blazor.Services;

public sealed class ReportesEmpresarialesApiClient(HttpClient http)
{
    public static readonly string[] Tipos = ["ventas", "inventario", "top-productos", "cuentas-por-cobrar"];
    public static string Titulo(string tipo) => tipo switch
    {
        "ventas" => "Ventas por período", "inventario" => "Inventario disponible",
        "top-productos" => "Productos más vendidos (por valor neto)", "cuentas-por-cobrar" => "Cuentas por cobrar",
        _ => throw new ArgumentException("Tipo de reporte inválido.", nameof(tipo))
    };

    public async Task<ReporteTabla> TablaAsync(string tipo, ReporteCriterios criterios, bool completo, CancellationToken ct = default)
    {
        _ = Titulo(tipo);
        var filas = new List<object?[]>();
        ReporteTotales totales = new();
        var corte = criterios.Hasta ?? DateOnly.FromDateTime(DateTime.UtcNow);
        criterios = criterios with { Hasta = corte, Pagina = completo ? 1 : criterios.Pagina };
        long total;
        var url = Url(tipo, criterios, completo);
            switch (tipo)
            {
                case "ventas":
                    var ventas = await Obtener<VentaReporteFila>(url, ct);
                    filas.AddRange(ventas.Pagina.Items.Select(x => new object?[] { x.Numero, x.TipoDocumento, x.Fecha, x.ClienteNombre, x.Moneda, x.Venta, x.Devolucion, x.Neto }));
                    (totales, corte, total) = (ventas.Totales, ventas.FechaCorte, ventas.Pagina.Total);
                    break;
                case "inventario":
                    var inventario = await Obtener<InventarioReporteFila>(url, ct);
                    filas.AddRange(inventario.Pagina.Items.Select(x => new object?[] { x.ProductoCodigo, x.ProductoNombre, x.AlmacenCodigo, x.UnidadBaseCodigo, x.Existencia, x.Valor }));
                    (totales, corte, total) = (inventario.Totales, inventario.FechaCorte, inventario.Pagina.Total);
                    break;
                case "top-productos":
                    var top = await Obtener<TopProductoReporteFila>(url, ct);
                    filas.AddRange(top.Pagina.Items.Select(x => new object?[] { x.ProductoCodigo, x.ProductoNombre, x.UnidadBaseCodigo, x.CantidadVendida, x.CantidadDevuelta, x.CantidadNeta, x.Ventas, x.Devoluciones, x.Neto }));
                    (totales, corte, total) = (top.Totales, top.FechaCorte, top.Pagina.Total);
                    break;
                default:
                    var cuentas = await Obtener<EstadoCuentaClienteResponse>(url, ct);
                    filas.AddRange(cuentas.Pagina.Items.Select(x => new object?[] { x.Codigo, x.Nombre, x.DocumentosAbiertos, x.Corriente, x.Dias1a30, x.Dias31a60, x.Dias61a90, x.Mas90, x.SinAplicar, x.Total }));
                    (totales, corte, total) = (cuentas.Totales, cuentas.FechaCorte, cuentas.Pagina.Total);
                    break;
            }
        if (completo && filas.Count != total)
            throw new HttpRequestException("El reporte recibido está incompleto. No se generó el archivo; vuelva a intentar.", null, System.Net.HttpStatusCode.Conflict);

        string[] columnas = tipo switch
        {
            "ventas" => ["Documento", "Tipo", "Fecha", "Cliente", "Moneda", "Ventas", "Devoluciones", "Neto"],
            "inventario" => ["Código", "Producto", "Almacén", "Unidad", "Existencia", "Valor"],
            "top-productos" => ["Código", "Producto", "Unidad", "Vendida", "Devuelta", "Neta", "Ventas", "Devoluciones", "Neto"],
            _ => ["Código", "Cliente", "Documentos", "Corriente", "1–30", "31–60", "61–90", ">90", "Sin aplicar", "Saldo"]
        };
        var resumen = tipo switch
        {
            "inventario" => $"Valor total: {totales.Valor:N2}. Cantidades en la unidad base de cada producto; no sumar unidades diferentes.",
            "cuentas-por-cobrar" => $"Saldo total: {totales.CuentasPorCobrar:N2}",
            _ => $"Ventas: {totales.Ventas:N2} · Devoluciones: {totales.Devoluciones:N2} · Neto: {totales.Neto:N2}"
        };
        return new(Titulo(tipo), $"Desde: {criterios.Desde?.ToString("yyyy-MM-dd") ?? "inicio"} · Corte: {corte:yyyy-MM-dd} · Moneda: {criterios.Moneda} · Estado: {criterios.Estado} · Cliente: {criterios.ClienteId?.ToString() ?? "todos"} · Producto: {criterios.ProductoId?.ToString() ?? "todos"} · Almacén: {criterios.AlmacenId?.ToString() ?? "todos"}", columnas, filas, resumen, total);
    }

    private async Task<ReporteResponse<T>> Obtener<T>(string url, CancellationToken ct)
    {
        using var response = await http.GetAsync(url, ct);
        if (!response.IsSuccessStatusCode)
        {
            var mensaje = response.StatusCode switch
            {
                System.Net.HttpStatusCode.BadRequest => "Los filtros no son válidos para este reporte. Revise fechas, identificadores y estado.",
                System.Net.HttpStatusCode.Forbidden => "No tiene permisos para consultar este reporte.",
                System.Net.HttpStatusCode.Unauthorized => "La sesión ha vencido. Inicie sesión nuevamente.",
                _ => "No fue posible consultar el reporte. Intente nuevamente."
            };
            throw new HttpRequestException(mensaje, null, response.StatusCode);
        }
        return await response.Content.ReadFromJsonAsync<ReporteResponse<T>>(ct)
            ?? throw new HttpRequestException("La API no devolvió el reporte.");
    }

    public static string Url(string tipo, ReporteCriterios c, bool exportacion = false)
    {
        _ = Titulo(tipo);
        var p = new List<string> { $"estado={Uri.EscapeDataString(c.Estado)}", $"pagina={c.Pagina}", $"tamanoPagina={c.TamanoPagina}", $"moneda={Uri.EscapeDataString(c.Moneda)}" };
        if (c.Desde is { } desde) p.Add($"desde={desde.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}");
        if (c.Hasta is { } hasta) p.Add($"hasta={hasta.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}");
        if (c.ClienteId is { } cliente) p.Add($"clienteId={cliente}");
        if (c.ProductoId is { } producto) p.Add($"productoId={producto}");
        if (c.AlmacenId is { } almacen) p.Add($"almacenId={almacen}");
        return $"api/reportes/{tipo}{(exportacion ? "/exportacion" : "")}?{string.Join('&', p)}";
    }
}
