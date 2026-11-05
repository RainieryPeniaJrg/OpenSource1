namespace OpenSource1.Application.Security;

/// <summary>
/// Mapea cada rol del sistema al subconjunto del catálogo fino de <see cref="Permisos"/> que le
/// corresponde. Es el equivalente por recurso de la matriz coarse actual
/// (<see cref="ApplicationPolicies.CanConsult"/>/<see cref="ApplicationPolicies.CanAdd"/>/
/// <see cref="ApplicationPolicies.CanModify"/>/<see cref="ApplicationPolicies.CanDelete"/>).
/// </summary>
/// <remarks>
/// <para>
/// Criterio de agrupación de acciones en "familias" (documentado explícitamente porque el
/// catálogo tiene más verbos que las 4 políticas coarse: consultar, agregar, modificar,
/// eliminar, capturar, postear, emitir, registrar, aplicar):
/// </para>
/// <list type="bullet">
/// <item><description><c>consultar</c>: solo lectura. Equivale a <c>CanConsult</c>. La tienen
/// los 3 roles.</description></item>
/// <item><description><c>agregar</c>, <c>capturar</c>, <c>registrar</c>: crean un registro o
/// capturan/registran un documento nuevo que todavía no compromete datos existentes (p.ej.
/// capturar un movimiento de inventario, registrar un cobro recibido). Se agrupan con
/// <c>CanAdd</c> porque son, en esencia, altas.</description></item>
/// <item><description><c>modificar</c>, <c>postear</c>, <c>emitir</c>, <c>aplicar</c>: alteran o
/// comprometen datos que ya existen (postear un asiento/movimiento, emitir una factura, aplicar
/// un cobro a un saldo). Se agrupan con <c>CanModify</c> porque son operaciones de riesgo
/// equivalente a una modificación: cambian el estado de algo que ya fue capturado.</description></item>
/// <item><description><c>eliminar</c>: borrado. Equivale a <c>CanDelete</c>, reservado a
/// <see cref="ApplicationRoles.Administrator"/>.</description></item>
/// </list>
/// <para>Mapeo por rol resultante (igual a la matriz coarse actual):</para>
/// <list type="bullet">
/// <item><description><see cref="ApplicationRoles.Administrator"/>: todas las familias (todo el
/// catálogo).</description></item>
/// <item><description><see cref="ApplicationRoles.Supervisor"/>: familia "consultar" + familia
/// "modificar" (incluye postear/emitir/aplicar). Sin agregar/capturar/registrar ni
/// eliminar.</description></item>
/// <item><description><see cref="ApplicationRoles.Executor"/>: familia "consultar" + familia
/// "agregar" (incluye capturar/registrar). Sin modificar/postear/emitir/aplicar ni
/// eliminar.</description></item>
/// </list>
/// </remarks>
public static class PermisosPorRol
{
    private static readonly HashSet<string> AccionesConsultar = new(StringComparer.OrdinalIgnoreCase)
    {
        "consultar"
    };

    private static readonly HashSet<string> AccionesAgregar = new(StringComparer.OrdinalIgnoreCase)
    {
        "agregar", "capturar", "registrar"
    };

    private static readonly HashSet<string> AccionesModificar = new(StringComparer.OrdinalIgnoreCase)
    {
        "modificar", "postear", "emitir", "aplicar"
    };

    // La familia "eliminar" no se filtra explícitamente: solo Administrador la tiene, y
    // Administrador recibe Permisos.All completo (ver el switch de ParaRol) sin pasar por
    // Filtrar(). Se documenta igual arriba en <remarks> para que el criterio quede completo.

    /// <summary>Devuelve el subconjunto del catálogo de <see cref="Permisos"/> que corresponde a
    /// un rol. Un rol desconocido no obtiene ningún permiso fino.</summary>
    public static IReadOnlyCollection<string> ParaRol(string rol) => rol switch
    {
        ApplicationRoles.Administrator => Permisos.All.ToArray(),
        ApplicationRoles.Supervisor => Filtrar(AccionesConsultar, AccionesModificar),
        ApplicationRoles.Executor => Filtrar(AccionesConsultar, AccionesAgregar),
        ApplicationRoles.Salesperson =>
        [
            Permisos.Maestros.Socio.Consultar,
            Permisos.Maestros.Producto.Consultar,
            Permisos.Inventario.Almacen.Consultar,
            Permisos.Facturacion.Factura.Capturar,
            Permisos.Facturacion.Factura.Emitir,
        ],
        _ => []
    };

    private static IReadOnlyCollection<string> Filtrar(params HashSet<string>[] familias) =>
        Permisos.All.Where(permiso => familias.Any(familia => EsDeFamilia(permiso, familia))).ToArray();

    private static bool EsDeFamilia(string permiso, HashSet<string> familia)
    {
        var accion = permiso[(permiso.LastIndexOf('.') + 1)..];
        return familia.Contains(accion);
    }
}
