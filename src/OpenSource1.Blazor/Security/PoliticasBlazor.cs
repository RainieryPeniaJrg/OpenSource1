using Microsoft.AspNetCore.Authorization;
using OpenSource1.Application.Security;

namespace OpenSource1.Blazor.Security;

/// <summary>
/// Políticas coarse del host Blazor: cada una exige el claim "permission" que la API emite al iniciar sesión (ver
/// AuthService.GetPermissions). Definición única para Program.cs y para los tests del registro de módulos.
/// </summary>
public static class PoliticasBlazor
{
    public static readonly string[] Todas =
    [
        ApplicationPolicies.CanAdd,
        ApplicationPolicies.CanModify,
        ApplicationPolicies.CanDelete,
        ApplicationPolicies.CanConsult,
        ApplicationPolicies.CanAdministrar,
    ];

    public static void Configurar(AuthorizationOptions options)
    {
        foreach (var politica in Todas)
        {
            options.AddPolicy(politica, policy => policy.RequireClaim("permission", politica));
        }
        options.AddPolicy(ApplicationPolicies.CanCapturarFactura, policy => policy
            .RequireAuthenticatedUser()
            .RequireAssertion(context => context.User.HasClaim("permission", ApplicationPolicies.CanAdd)
                || context.User.HasClaim("permission", Permisos.Facturacion.Factura.Capturar)));
        options.AddPolicy(ApplicationPolicies.CanEmitirFactura, policy => policy
            .RequireAuthenticatedUser()
            .RequireAssertion(context => context.User.HasClaim("permission", ApplicationPolicies.CanModify)
                || context.User.HasClaim("permission", Permisos.Facturacion.Factura.Emitir)));
    }
}
