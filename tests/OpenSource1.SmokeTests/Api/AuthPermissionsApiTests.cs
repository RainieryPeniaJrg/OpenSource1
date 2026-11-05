using System.IdentityModel.Tokens.Jwt;
using System.Net.Http.Json;
using System.Security.Claims;
using Microsoft.AspNetCore.Mvc.Testing;
using OpenSource1.Application.Security;
using OpenSource1.Application.Services.Auth.Dtos;
using OpenSource1.SmokeTests.TestInfrastructure;

namespace OpenSource1.SmokeTests.Api;

/// <summary>
/// Verifica, contra la API real (login real vía <c>IAuthService</c> + Postgres real, sin el
/// <see cref="TestAuthHandler"/> que las demás pruebas de <c>Api/*</c> usan para simular roles),
/// que el catálogo fino de permisos (<see cref="Permisos"/>) se añade al MISMO array/claim
/// "permission" que las 4 políticas coarse existentes, y que el flujo de autorización de Blazor
/// (que construye su cookie iterando <c>AuthResponse.Permissions</c> — ver Login.razor — y
/// evalúa políticas con <c>RequireClaim("permission", ...)</c> — ver Program.cs) sigue
/// funcionando sin ningún cambio en Blazor.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class AuthPermissionsApiTests : IClassFixture<PostgresTestFixture>
{
    private readonly HttpClient _client;

    public AuthPermissionsApiTests(PostgresTestFixture fixture)
    {
        _client = fixture.CreateFactory().CreateClient(new WebApplicationFactoryClientOptions { AllowAutoRedirect = false });
    }

    [Theory]
    [InlineData("admin", ApplicationRoles.Administrator)]
    [InlineData("supervisor", ApplicationRoles.Supervisor)]
    [InlineData("ejecutor", ApplicationRoles.Executor)]
    [InlineData("vendedor", ApplicationRoles.Salesperson)]
    public async Task Login_Incluye_Permisos_Coarse_Y_Finos_En_El_Mismo_Array(string userName, string rolEsperado)
    {
        var response = await LoginAsync(userName);

        // Las 4 políticas coarse existentes SIGUEN presentes: si esto falla, Blazor se rompe en
        // silencio (Program.cs evalúa RequireClaim("permission", ApplicationPolicies.CanX)).
        var coarseEsperadas = CoarsePermissionsEsperadas(rolEsperado);
        foreach (var coarse in coarseEsperadas)
        {
            Assert.Contains(coarse, response.Permissions);
        }

        // Los permisos finos del catálogo también están en el MISMO array.
        var finosEsperados = PermisosPorRol.ParaRol(rolEsperado);
        foreach (var fino in finosEsperados)
        {
            Assert.Contains(fino, response.Permissions);
        }

        // No hay permisos "de más": el array es exactamente coarse esperados + finos esperados.
        var esperado = coarseEsperadas.Concat(finosEsperados)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase)
            .Distinct(StringComparer.OrdinalIgnoreCase);
        var real = response.Permissions.OrderBy(p => p, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(esperado, real);
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("supervisor")]
    [InlineData("ejecutor")]
    [InlineData("vendedor")]
    public async Task Jwt_Emite_Un_Claim_Permission_Por_Cada_Entrada_De_Permissions(string userName)
    {
        var response = await LoginAsync(userName);

        var handler = new JwtSecurityTokenHandler();
        var token = handler.ReadJwtToken(response.AccessToken);

        var permisosDelToken = token.Claims
            .Where(c => c.Type == "permission")
            .Select(c => c.Value)
            .OrderBy(p => p, StringComparer.OrdinalIgnoreCase);

        var permisosDelResponse = response.Permissions.OrderBy(p => p, StringComparer.OrdinalIgnoreCase);

        Assert.Equal(permisosDelResponse, permisosDelToken);

        // No se creó un claim "permiso" (español) separado, en paralelo al existente "permission".
        Assert.DoesNotContain(token.Claims, c => c.Type == "permiso");
    }

    [Fact]
    public async Task Simulacion_Del_Flujo_De_Blazor_Autoriza_CanAdd_CanModify_CanDelete_CanConsult_Sin_Cambios()
    {
        // Replica exactamente Login.razor (construcción de claims de cookie a partir de
        // AuthResponse.Permissions) y Program.cs de Blazor (RequireClaim("permission", CanX))
        // sin tocar ni un archivo de OpenSource1.Blazor: si esto pasa, el login de Blazor para un
        // Administrador sigue pudiendo autorizar las 4 políticas coarse tal como antes de este lote.
        var response = await LoginAsync("admin");

        var claims = new List<Claim>
        {
            new(ClaimTypes.NameIdentifier, response.UserId),
            new(ClaimTypes.Name, response.FullName),
            new(ClaimTypes.Email, response.Email)
        };
        claims.AddRange(response.Roles.Select(r => new Claim(ClaimTypes.Role, r)));
        claims.AddRange(response.Permissions.Select(p => new Claim("permission", p)));

        var principal = new ClaimsPrincipal(new ClaimsIdentity(claims, "cookie"));

        // Equivalente exacto de policy.RequireClaim("permission", ApplicationPolicies.CanX) para
        // cada una de las 4 políticas registradas en Blazor Program.cs.
        Assert.True(principal.HasClaim("permission", ApplicationPolicies.CanAdd));
        Assert.True(principal.HasClaim("permission", ApplicationPolicies.CanModify));
        Assert.True(principal.HasClaim("permission", ApplicationPolicies.CanDelete));
        Assert.True(principal.HasClaim("permission", ApplicationPolicies.CanConsult));
        // Task 8.5: la configuración reservada al Administrador (fechas de registro permitidas).
        Assert.True(principal.HasClaim("permission", ApplicationPolicies.CanAdministrar));
    }

    [Theory]
    [InlineData("supervisor")]
    [InlineData("ejecutor")]
    [InlineData("vendedor")]
    public async Task CanAdministrar_SoloSeEmiteParaElAdministrador(string userName)
    {
        var response = await LoginAsync(userName);

        Assert.DoesNotContain(ApplicationPolicies.CanAdministrar, response.Permissions);
    }

    private async Task<AuthResponse> LoginAsync(string userName)
    {
        var login = await _client.PostAsJsonAsync("/api/auth/login", new LoginRequest
        {
            UserNameOrEmail = userName,
            Password = "Password123"
        });

        login.EnsureSuccessStatusCode();
        var response = await login.Content.ReadFromJsonAsync<AuthResponse>();
        Assert.NotNull(response);
        return response!;
    }

    private static string[] CoarsePermissionsEsperadas(string rol) => rol switch
    {
        ApplicationRoles.Administrator =>
            [ApplicationPolicies.CanAdd, ApplicationPolicies.CanModify, ApplicationPolicies.CanDelete, ApplicationPolicies.CanConsult,
             ApplicationPolicies.CanAdministrar],
        ApplicationRoles.Supervisor => [ApplicationPolicies.CanModify, ApplicationPolicies.CanConsult],
        ApplicationRoles.Executor => [ApplicationPolicies.CanAdd, ApplicationPolicies.CanConsult],
        ApplicationRoles.Salesperson => [ApplicationPolicies.CanConsult],
        _ => []
    };
}
