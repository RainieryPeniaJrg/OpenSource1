using OpenSource1.Application.Security;

namespace OpenSource1.Blazor.Navigation;

/// <summary>
/// Fuente única de grupos y módulos (Fix-Features A1). Menú, inicio, páginas de grupo, /buscar y la paleta Ctrl+K leen de
/// aquí; <c>RegistroModulosTests</c> comprueba que cada listado con @page está registrado y que cada ruta existe.
/// </summary>
public static class CatalogoModulos
{
    private static readonly string[] Admin = [ApplicationRoles.Administrator];
    private static readonly string[] AdminSupervisor = [ApplicationRoles.Administrator, ApplicationRoles.Supervisor];
    private const string Consultar = ApplicationPolicies.CanConsult;

    public static IReadOnlyList<GrupoModulo> Grupos { get; } =
    [
        new("clientes", "Clientes", IconosModulo.Personas, "Fichas de clientes y su panel de indicadores.", 1),
        new("productos", "Productos", IconosModulo.Cubo, "Catálogo de productos, categorías y panel.", 2),
        new("inventario", "Inventario", IconosModulo.Tabla, "Existencias, almacenes, diarios y movimientos.", 3),
        new("ventas", "Ventas", IconosModulo.Tarjeta, "Cobros, libro de clientes y estado de cuenta.", 4),
        new("facturacion", "Facturación", IconosModulo.Documento, "Facturas y notas de crédito, registradas y en preparación.", 5),
        new("contabilidad", "Contabilidad", IconosModulo.Libro, "Plan de cuentas, movimientos, balance y costo de inventario.", 6),
        new("reportes", "Reportes", IconosModulo.Grafico, "Reportería y bitácora de actividad.", 7),
        new("configuracion", "Configuración", IconosModulo.Ajustes, "Catálogos auxiliares y parámetros contables.", 8),
        new("administracion", "Administración", IconosModulo.Escudo, "Usuarios, roles y estado de las cuentas.", 9),
    ];

    public static IReadOnlyList<Modulo> Modulos { get; } =
    [
        new("clientes", "clientes", "Clientes", "Fichas, búsqueda, filtros y reportes de clientes.", "/clientes", IconosModulo.Persona, Consultar, null, ["socios", "cliente", "rnc", "cedula"]),
        new("panel-clientes", "clientes", "Panel de clientes", "Indicadores y gráficos de clientes.", "/dashboard/clientes", IconosModulo.Grafico, Consultar, null, ["dashboard", "graficos"]),

        new("productos", "productos", "Productos", "Catálogo con precio, existencia y costo.", "/productos", IconosModulo.Cubo, Consultar, null, ["articulos", "items"]),
        new("categorias-producto", "productos", "Categorías de producto", "Jerarquía de categorías.", "/categorias-producto", IconosModulo.Etiqueta, Consultar, null, ["categoria", "familia"]),
        new("panel-productos", "productos", "Panel de productos", "Indicadores y gráficos de productos.", "/dashboard/productos", IconosModulo.Grafico, Consultar, null, ["dashboard", "graficos"]),

        new("existencias", "inventario", "Existencias", "Existencia por almacén a una fecha.", "/inventario/existencias", IconosModulo.Tabla, Consultar, null, ["stock", "disponible"]),
        new("almacenes", "inventario", "Almacenes", "Almacenes y almacén predeterminado.", "/almacenes", IconosModulo.Lista, Consultar, null, ["bodega", "deposito"]),
        new("diarios-inventario", "inventario", "Diarios de inventario", "Lotes de ajustes y transferencias.", "/diarios-inventario", IconosModulo.Lista, Consultar, null, ["ajuste", "transferencia", "lote"]),
        new("movimientos-producto", "inventario", "Movimientos de producto", "Libro de cantidades por producto.", "/inventario/movimientos-producto", IconosModulo.Lista, Consultar, null, ["kardex", "entradas", "salidas"]),
        new("movimientos-valor", "inventario", "Movimientos de valor", "Libro de costo por producto.", "/inventario/movimientos-valor", IconosModulo.Lista, Consultar, null, ["costo", "valor"]),

        new("cobros", "ventas", "Cobros", "Registro y aplicación de pagos de clientes.", "/cobros", IconosModulo.Tarjeta, Consultar, null, ["pagos", "recibos"]),
        new("movimientos-cliente", "ventas", "Movimientos de cliente", "Libro de clientes con importes pendientes.", "/ventas/movimientos-cliente", IconosModulo.Lista, Consultar, null, ["cxc", "cuentas por cobrar"]),
        new("estado-cuenta", "ventas", "Estado de cuenta (CxC)", "Saldos por cliente a una fecha de corte.", "/ventas/estado-cuenta", IconosModulo.Documento, Consultar, null, ["saldo", "cxc", "antiguedad"]),

        new("facturas", "facturacion", "Facturas", "Facturas de venta registradas y en preparación.", "/facturas-venta", IconosModulo.Documento, Consultar, null,
            ["factura", "venta", "fv", "borrador", "en preparación", "en preparacion"]),
        new("notas-credito", "facturacion", "Notas de crédito", "Notas de crédito registradas y en preparación.", "/notas-credito-venta", IconosModulo.Documento, Consultar, null,
            ["nc", "devolucion", "nota de credito", "borrador", "en preparación", "en preparacion"]),

        new("plan-cuentas", "contabilidad", "Plan de cuentas", "Catálogo de cuentas contables.", "/cuentas-contables", IconosModulo.Libro, Consultar, null, ["cuentas", "catalogo"]),
        new("movimientos-contables", "contabilidad", "Movimientos contables", "Libro de movimientos contables.", "/contabilidad/movimientos", IconosModulo.Lista, Consultar, null, ["asientos", "mayor", "diario"]),
        new("balance-comprobacion", "contabilidad", "Balance de comprobación", "Saldos por cuenta a una fecha.", "/contabilidad/balance-comprobacion", IconosModulo.Tabla, Consultar, null, ["balanza"]),
        new("costo-inventario", "contabilidad", "Costo de inventario", "Ajuste y posteo del costo de inventario.", "/contabilidad", IconosModulo.Reloj, Consultar, null, ["costo", "ajuste"]),

        new("reporteria", "reportes", "Reportería", "Reportes PDF y Excel de clientes y productos.", "/reporteria", IconosModulo.Descargar, Consultar, null, ["pdf", "excel", "reporte"]),
        new("bitacora", "reportes", "Bitácora", "Registro de actividad del sistema.", "/bitacora", IconosModulo.Reloj, null, AdminSupervisor, ["auditoria", "log", "actividad"]),

        new("terminos-pago", "configuracion", "Términos de pago", "Vencimiento y descuento por pronto pago.", "/terminos-pago", IconosModulo.Calendario, Consultar, null, ["credito", "dias"]),
        new("unidades-medida", "configuracion", "Unidades de medida", "Unidades y decimales de redondeo.", "/unidades-medida", IconosModulo.Ajustes, Consultar, null, ["um", "unidad"]),
        new("grupos-contables", "configuracion", "Grupos contables", "Grupos de negocio, producto, IVA e inventario.", "/grupos-contables", IconosModulo.Ajustes, Consultar, null, ["grupo", "iva", "itbis"]),
        new("grupos-cliente-contable", "configuracion", "Grupos de cliente contable", "Cuenta de CxC por grupo de cliente.", "/grupos-cliente-contable", IconosModulo.Ajustes, Consultar, null, ["cxc", "grupo cliente"]),
        new("setups-contables", "configuracion", "Setups contables", "Cuentas por combinación de grupos.", "/setups-contables", IconosModulo.Ajustes, Consultar, null, ["setup", "configuracion contable"]),
        new("fechas-registro", "configuracion", "Fechas de registro", "Rango de fechas de registro permitidas.", "/admin/fechas-registro", IconosModulo.Calendario, ApplicationPolicies.CanAdministrar, null, ["periodo", "cierre", "fechas"]),
        new("series-numeracion", "configuracion", "Series de numeración", "Series, líneas, último usado y próximo número.", "/series", IconosModulo.Lista, ApplicationPolicies.CanAdministrar, null, ["numeracion", "serie", "prefijo", "correlativo"]),
        new("configuracion-numeracion", "configuracion", "Configuración de numeración", "Serie predeterminada de cada tipo de documento.", "/configuracion/numeracion", IconosModulo.Ajustes, ApplicationPolicies.CanAdministrar, null, ["numeracion", "series por defecto"]),

        new("usuarios", "administracion", "Usuarios", "Cuentas, roles y estado de los usuarios.", "/admin/users", IconosModulo.Personas, null, Admin, ["roles", "cuentas"]),
    ];

    /// <summary>
    /// Rutas que no cuelgan del listado de su módulo pero lo marcan en el menú (Puerta C). Se comparan como prefijo por segmentos,
    /// igual que <see cref="Modulo.Ruta"/>. Spec alta rápida: las altas y las páginas en preparación cuelgan de la ruta de su
    /// documento (/facturas-venta/…, /notas-credito-venta/…) y el prefijo ya las marca; se conserva para futuras rutas que no
    /// cuelguen de su listado.
    /// </summary>
    public static IReadOnlyDictionary<string, string> RutasAsociadas { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
}
