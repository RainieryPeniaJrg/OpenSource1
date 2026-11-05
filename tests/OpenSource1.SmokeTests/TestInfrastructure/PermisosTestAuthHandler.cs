using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenSource1.Application.Security;

namespace OpenSource1.SmokeTests.TestInfrastructure;

/// <summary>
/// Autenticación de prueba del host Blazor. A diferencia de <see cref="TestAuthHandler"/> (API), las políticas del host
/// exigen el claim <c>permission</c> (CanConsult, CanAdd…), igual que la cookie real que crea Login.razor. Sin
/// "X-Test-Permisos" se emiten los permisos coarse del rol (ver <see cref="PermisosDeRol"/>); con el valor
/// <see cref="SinPermisos"/> no se emite ninguno.
/// </summary>
public sealed class PermisosTestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    public const string Esquema = "TestPermisos";
    public const string CabeceraAnonimo = "X-Test-Anonymous";
    public const string CabeceraRoles = "X-Test-Roles";
    public const string CabeceraPermisos = "X-Test-Permisos";

    /// <summary>Centinela de "X-Test-Permisos" para cero permisos (una cabecera vacía no llega al servidor).</summary>
    public const string SinPermisos = "-";

    /// <summary>
    /// Políticas coarse y permisos finos de cada rol, como el JWT/cookie emitido por producción.
    /// </summary>
    public static string PermisosDeRol(string roles)
    {
        var permisos = new HashSet<string>(StringComparer.Ordinal);
        foreach (var rol in roles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
        {
            switch (rol)
            {
                case ApplicationRoles.Administrator:
                    permisos.UnionWith([ApplicationPolicies.CanAdd, ApplicationPolicies.CanModify, ApplicationPolicies.CanDelete, ApplicationPolicies.CanConsult, ApplicationPolicies.CanAdministrar]);
                    break;
                case ApplicationRoles.Supervisor:
                    permisos.UnionWith([ApplicationPolicies.CanModify, ApplicationPolicies.CanConsult]);
                    break;
                case ApplicationRoles.Executor:
                    permisos.UnionWith([ApplicationPolicies.CanAdd, ApplicationPolicies.CanConsult]);
                    break;
                case ApplicationRoles.Salesperson:
                    permisos.Add(ApplicationPolicies.CanConsult);
                    break;
            }
            permisos.UnionWith(PermisosPorRol.ParaRol(rol));
        }

        return string.Join(',', permisos.Order(StringComparer.Ordinal));
    }

    public static ClaimsPrincipal Principal(string roles, string? permisos = null, string esquema = Esquema)
    {
        var listaRoles = roles.Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
        var usuario = listaRoles.Length == 0 ? "anonimo" : listaRoles[0].ToLowerInvariant();
        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, usuario),
            new(ClaimTypes.Name, usuario),
            new(ClaimTypes.Email, $"{usuario}@test.local"),
        };
        claims.AddRange(listaRoles.Select(rol => new Claim(ClaimTypes.Role, rol)));
        claims.AddRange((permisos ?? PermisosDeRol(roles))
            .Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            .Select(p => new Claim("permission", p)));
        return new ClaimsPrincipal(new ClaimsIdentity(claims, esquema));
    }

    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Request.Headers.ContainsKey(CabeceraAnonimo))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var roles = Request.Headers.TryGetValue(CabeceraRoles, out var r) ? r.ToString() : ApplicationRoles.Administrator;
        string? permisos = Request.Headers.TryGetValue(CabeceraPermisos, out var p) ? p.ToString() : null;
        if (permisos == SinPermisos)
        {
            permisos = string.Empty;
        }

        var principal = Principal(roles, permisos, Scheme.Name);
        return Task.FromResult(AuthenticateResult.Success(new AuthenticationTicket(principal, Scheme.Name)));
    }
}
