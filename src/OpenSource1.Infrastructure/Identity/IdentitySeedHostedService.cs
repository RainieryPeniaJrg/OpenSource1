using Microsoft.AspNetCore.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenSource1.Application.Security;

namespace OpenSource1.Infrastructure.Identity;

public sealed class IdentitySeedHostedService(
    IServiceScopeFactory scopeFactory,
    IOptions<UserSeedOptions> seedOptions,
    ILogger<IdentitySeedHostedService> logger) : IHostedService
{
    public async Task StartAsync(CancellationToken cancellationToken)
    {
        if (!seedOptions.Value.Enabled)
        {
            return;
        }

        if (string.IsNullOrWhiteSpace(seedOptions.Value.DefaultPassword))
        {
            throw new InvalidOperationException("UserSeed:DefaultPassword must be configured with a secure environment variable when user seeding is enabled.");
        }

        await using var scope = scopeFactory.CreateAsyncScope();
        var roleManager = scope.ServiceProvider.GetRequiredService<RoleManager<IdentityRole>>();
        var userManager = scope.ServiceProvider.GetRequiredService<UserManager<Usuario>>();

        foreach (var role in ApplicationRoles.All)
        {
            if (!await roleManager.RoleExistsAsync(role))
            {
                await roleManager.CreateAsync(new IdentityRole(role));
            }
        }

        // Usuarios base del sistema
        await EnsureUserAsync(userManager, "admin",      "admin@opensource1.local",      "Administrador",    ApplicationRoles.Administrator, seedOptions.Value.DefaultPassword);
        await EnsureUserAsync(userManager, "supervisor", "supervisor@opensource1.local",  "Supervisor",       ApplicationRoles.Supervisor,    seedOptions.Value.DefaultPassword);
        await EnsureUserAsync(userManager, "ejecutor",   "ejecutor@opensource1.local",    "Ejecutor",         ApplicationRoles.Executor,      seedOptions.Value.DefaultPassword);

        // Equipo realista adicional
        await EnsureUserAsync(userManager, "cmendes",    "carlos.mendes@opensource1.local",  "Carlos Méndez",       ApplicationRoles.Administrator, seedOptions.Value.DefaultPassword);
        await EnsureUserAsync(userManager, "agarcia",    "ana.garcia@opensource1.local",      "Ana García",          ApplicationRoles.Supervisor,    seedOptions.Value.DefaultPassword);
        await EnsureUserAsync(userManager, "ltorres",    "luis.torres@opensource1.local",     "Luis Torres",         ApplicationRoles.Supervisor,    seedOptions.Value.DefaultPassword);
        await EnsureUserAsync(userManager, "mrodriguez", "maria.rodriguez@opensource1.local", "María Rodríguez",     ApplicationRoles.Executor,      seedOptions.Value.DefaultPassword);
        await EnsureUserAsync(userManager, "jperez",     "juan.perez@opensource1.local",      "Juan Pérez",          ApplicationRoles.Executor,      seedOptions.Value.DefaultPassword);
        await EnsureUserAsync(userManager, "svargas",    "sofia.vargas@opensource1.local",    "Sofía Vargas",        ApplicationRoles.Executor,      seedOptions.Value.DefaultPassword, isActive: false);
    }

    public Task StopAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    private async Task EnsureUserAsync(
        UserManager<Usuario> userManager,
        string userName,
        string email,
        string fullName,
        string role,
        string password,
        bool isActive = true)
    {
        var user = await userManager.FindByNameAsync(userName);

        if (user is null)
        {
            user = new Usuario
            {
                UserName = userName,
                Email = email,
                FullName = fullName,
                EmailConfirmed = true,
                IsActive = isActive
            };

            var result = await userManager.CreateAsync(user, password);
            if (!result.Succeeded)
            {
                throw new InvalidOperationException($"Failed to seed user '{userName}': {string.Join(", ", result.Errors.Select(error => error.Description))}");
            }

            logger.LogInformation("Seeded user {UserName} (active={IsActive}).", userName, isActive);
        }
        else if (seedOptions.Value.ResetPasswords && await userManager.IsLockedOutAsync(user))
        {
            await userManager.SetLockoutEndDateAsync(user, null);
            await userManager.ResetAccessFailedCountAsync(user);
            logger.LogInformation("Unlocked seed user {UserName}.", userName);
        }

        if (!await userManager.CheckPasswordAsync(user, password))
        {
            if (seedOptions.Value.ResetPasswords)
            {
                var token = await userManager.GeneratePasswordResetTokenAsync(user);
                var reset = await userManager.ResetPasswordAsync(user, token, password);
                if (!reset.Succeeded)
                {
                    throw new InvalidOperationException($"Failed to reset the password of seed user '{userName}': {string.Join(", ", reset.Errors.Select(error => error.Description))}");
                }

                logger.LogInformation("Reset the password of seed user {UserName} to UserSeed:DefaultPassword.", userName);
            }
            else
            {
                logger.LogWarning(
                    "Seed user {UserName} already exists with a password different from UserSeed:DefaultPassword; it was not changed. " +
                    "Set UserSeed:ResetPasswords=true (AUTH_SEED_RESET_PASSWORDS=true with docker compose) to reset it on startup.",
                    userName);
            }
        }

        if (!await userManager.IsInRoleAsync(user, role))
        {
            await userManager.AddToRoleAsync(user, role);
        }
    }
}
