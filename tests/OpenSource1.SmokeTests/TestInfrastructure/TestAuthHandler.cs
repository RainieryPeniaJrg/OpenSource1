using System.Security.Claims;
using System.Text.Encodings.Web;
using Microsoft.AspNetCore.Authentication;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenSource1.Application.Security;

namespace OpenSource1.SmokeTests.TestInfrastructure;

public sealed class TestAuthHandler(
    IOptionsMonitor<AuthenticationSchemeOptions> options,
    ILoggerFactory logger,
    UrlEncoder encoder) : AuthenticationHandler<AuthenticationSchemeOptions>(options, logger, encoder)
{
    protected override Task<AuthenticateResult> HandleAuthenticateAsync()
    {
        if (Request.Headers.ContainsKey("X-Test-Anonymous"))
        {
            return Task.FromResult(AuthenticateResult.NoResult());
        }

        var userName = Request.Headers.TryGetValue("X-Test-User", out var userValues)
            ? userValues.ToString()
            : "admin";

        var roles = Request.Headers.TryGetValue("X-Test-Roles", out var roleValues)
            ? roleValues.ToString().Split(',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries)
            : ["Administrador"];

        // "X-Test-UserId" (opcional): Id del usuario (NameIdentifier) distinto del nombre, p. ej. el Guid de un usuario real de
        // Identity para las excepciones por usuario de las fechas de registro permitidas (Task 8.5).
        var userId = Request.Headers.TryGetValue("X-Test-UserId", out var userIdValues)
            ? userIdValues.ToString()
            : userName;

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, userId),
            new(ClaimTypes.Name, userName),
            new(ClaimTypes.Email, $"{userName}@test.local")
        };

        claims.AddRange(roles.Select(role => new Claim(ClaimTypes.Role, role)));
        claims.AddRange(roles.SelectMany(PermisosPorRol.ParaRol).Distinct()
            .Select(permission => new Claim("permission", permission)));

        var identity = new ClaimsIdentity(claims, Scheme.Name);
        var principal = new ClaimsPrincipal(identity);
        var ticket = new AuthenticationTicket(principal, Scheme.Name);

        return Task.FromResult(AuthenticateResult.Success(ticket));
    }
}
