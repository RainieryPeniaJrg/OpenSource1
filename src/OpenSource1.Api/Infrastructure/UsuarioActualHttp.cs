using System.Security.Claims;
using OpenSource1.Application.Security;
using OpenSource1.Application.Services.Inventario;

namespace OpenSource1.Api.Infrastructure;

/// <summary>
/// <see cref="IUsuarioActual"/> a partir de los claims del JWT de la petición (<c>ClaimTypes.Name</c> y
/// <c>ClaimTypes.NameIdentifier</c>, los que emite <c>AuthService</c>). Sin petición o sin usuario autenticado vale
/// <c>"system"</c> sin id, igual que <c>UsuarioActualSistema</c> y la auditoría de <c>UnitOfWork</c>.
/// </summary>
public sealed class UsuarioActualHttp(IHttpContextAccessor httpContextAccessor) : IUsuarioActual
{
    private ClaimsPrincipal? Usuario => httpContextAccessor.HttpContext?.User;

    public string Nombre
    {
        get
        {
            var nombre = Usuario?.Identity?.Name;
            return string.IsNullOrWhiteSpace(nombre) ? "system" : nombre;
        }
    }

    public Guid? Id =>
        Guid.TryParse(Usuario?.FindFirstValue(ClaimTypes.NameIdentifier), out var id) ? id : null;

    /// <summary>Rol <see cref="ApplicationRoles.Administrator"/> en el JWT (la política <c>CanAdministrar</c> exige ese rol).</summary>
    public bool EsAdministrador => Usuario?.IsInRole(ApplicationRoles.Administrator) == true;
}
