using System.Text;
using Microsoft.AspNetCore.Authentication.JwtBearer;
using Microsoft.AspNetCore.Authorization;
using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Options;
using Microsoft.IdentityModel.Tokens;
using OpenSource1.Application.Security;

namespace OpenSource1.Infrastructure.Identity;

public static class DependencyInjection
{
    public static IServiceCollection AddApplicationIdentity(this IServiceCollection services, IConfiguration configuration)
    {
        // Validación diferida a ValidateOnStart(): corre cuando el host arranca, no durante el
        // registro de servicios. Esto importa para WebApplicationFactory (tests de integración):
        // la configuración de prueba (incluida una SigningKey válida) se fusiona en el builder
        // recién al construir el host, después de que este método ya se ejecutó. Validar aquí de
        // forma síncrona leería siempre los appsettings*.json reales (placeholder de 21
        // caracteres) y fallaría incluso cuando la configuración de test es válida.
        services.AddOptions<JwtOptions>()
            .Bind(configuration.GetSection(JwtOptions.SectionName))
            .Validate(o => !string.IsNullOrWhiteSpace(o.SigningKey) && o.SigningKey.Length >= 32,
                "JWT signing key must contain at least 32 characters.")
            .ValidateOnStart();

        services.Configure<UserSeedOptions>(configuration.GetSection(UserSeedOptions.SectionName));

        services
            .AddIdentity<Usuario, IdentityRole>(options =>
            {
                options.Password.RequiredLength = 8;
                options.Password.RequireDigit = true;
                options.Password.RequireLowercase = true;
                options.Password.RequireUppercase = true;
                options.Password.RequireNonAlphanumeric = false;

                options.User.RequireUniqueEmail = true;
                options.SignIn.RequireConfirmedAccount = false;
                options.Lockout.AllowedForNewUsers = true;
                options.Lockout.MaxFailedAccessAttempts = 3;
                options.Lockout.DefaultLockoutTimeSpan = TimeSpan.FromMinutes(5);
            })
            .AddEntityFrameworkStores<AppIdentityDbContext>()
            .AddDefaultTokenProviders();

        services
            .AddAuthentication(options =>
            {
                options.DefaultAuthenticateScheme = JwtBearerDefaults.AuthenticationScheme;
                options.DefaultChallengeScheme = JwtBearerDefaults.AuthenticationScheme;
                options.DefaultScheme = JwtBearerDefaults.AuthenticationScheme;
            })
            .AddJwtBearer(options =>
            {
                options.RequireHttpsMetadata = false;
                options.SaveToken = true;
            });

        // Resuelve JwtOptions vía DI (IOptions<JwtOptions>) en vez de cerrar sobre el valor leído
        // de forma eager arriba: para cuando esto se evalúa (primera vez que se pide
        // JwtBearerOptions, en el arranque del host o en la primera request autenticada), la
        // configuración de test ya está fusionada, así que la SigningKey usada aquí es la misma
        // que valida ValidateOnStart().
        services.AddOptions<JwtBearerOptions>(JwtBearerDefaults.AuthenticationScheme)
            .Configure<IOptions<JwtOptions>>((options, jwtOptions) =>
            {
                var jwt = jwtOptions.Value;
                options.TokenValidationParameters = new TokenValidationParameters
                {
                    ValidateIssuer = true,
                    ValidateAudience = true,
                    ValidateIssuerSigningKey = true,
                    ValidateLifetime = true,
                    ValidIssuer = jwt.Issuer,
                    ValidAudience = jwt.Audience,
                    IssuerSigningKey = new SymmetricSecurityKey(Encoding.UTF8.GetBytes(jwt.SigningKey)),
                    ClockSkew = TimeSpan.FromMinutes(1)
                };
            });

        services.AddAuthorization(options =>
        {
            options.AddPolicy(ApplicationPolicies.CanConsult, policy =>
                // Legacy: policy.RequireRole(ApplicationRoles.Administrator, ApplicationRoles.Supervisor, ApplicationRoles.Executor));
                policy.RequireRole(ApplicationRoles.Administrator, ApplicationRoles.Supervisor, ApplicationRoles.Executor, ApplicationRoles.Salesperson));
            options.AddPolicy(ApplicationPolicies.CanAdd, policy =>
                policy.RequireRole(ApplicationRoles.Administrator, ApplicationRoles.Executor));
            options.AddPolicy(ApplicationPolicies.CanModify, policy =>
                policy.RequireRole(ApplicationRoles.Administrator, ApplicationRoles.Supervisor));
            options.AddPolicy(ApplicationPolicies.CanDelete, policy =>
                policy.RequireRole(ApplicationRoles.Administrator));
            options.AddPolicy(ApplicationPolicies.CanAdministrar, policy =>
                policy.RequireRole(ApplicationRoles.Administrator));
            options.AddPolicy(ApplicationPolicies.CanCapturarFactura, policy => policy
                .RequireAuthenticatedUser()
                .RequireAssertion(context => context.User.IsInRole(ApplicationRoles.Administrator)
                    || context.User.IsInRole(ApplicationRoles.Executor)
                    || context.User.HasClaim("permission", Permisos.Facturacion.Factura.Capturar)));
            options.AddPolicy(ApplicationPolicies.CanEmitirFactura, policy => policy
                .RequireAuthenticatedUser()
                .RequireAssertion(context => context.User.IsInRole(ApplicationRoles.Administrator)
                    || context.User.IsInRole(ApplicationRoles.Supervisor)
                    || context.User.HasClaim("permission", Permisos.Facturacion.Factura.Emitir)));
        });

        // Catálogo de permisos finos por recurso (Permisos.*), resuelto al vuelo para nombres de
        // política "permiso:<permiso>". El registro va DESPUÉS de AddAuthorization(...) arriba:
        // AddAuthorization ya registró (vía TryAddSingleton) un DefaultAuthorizationPolicyProvider;
        // este AddSingleton posterior lo sustituye como IAuthorizationPolicyProvider activo
        // ("último registro gana" al resolver un servicio único), y PermissionPolicyProvider a su
        // vez delega en un DefaultAuthorizationPolicyProvider propio para no romper la resolución
        // de las 4 políticas coarse ya registradas arriba (ver comentario en PermissionPolicyProvider).
        services.AddSingleton<IAuthorizationPolicyProvider, PermissionPolicyProvider>();
        services.AddSingleton<IAuthorizationHandler, PermissionAuthorizationHandler>();

        services.AddHostedService<IdentitySeedHostedService>();

        return services;
    }
}
