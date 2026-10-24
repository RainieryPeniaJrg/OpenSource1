using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;
using OpenSource1.Application.Security;
using OpenSource1.Infrastructure.Data;
using OpenSource1.Infrastructure.Identity;
using OpenSource1.SmokeTests.TestInfrastructure;

namespace OpenSource1.SmokeTests.Infrastructure;

/// <summary>
/// Arranque en una máquina nueva: la contraseña de los usuarios semilla y la de PostgreSQL solo se aplican la primera vez
/// (al crear el usuario o el volumen). Estos tests fijan que un cambio posterior en <c>.env</c> no falle en silencio.
/// </summary>
[Collection(PostgresCollection.Name)]
public sealed class CredencialesArranqueTests(PostgresTestFixture fixture) : IClassFixture<PostgresTestFixture>
{
    private const string ContrasenaNueva = "OtraClave2026x";
    private const string ContrasenaSinAplicar = "SinAplicar2026x";

    [Fact]
    public async Task Semilla_UsuarioExistenteConOtraContrasena_SinReset_NoLaCambia_YAvisa()
    {
        using var factory = fixture.CreateFactory();
        _ = factory.Services; // arranca el host: migra y siembra con la contraseña de la configuración de pruebas
        var logger = new ListaLogger();

        await CrearSemilla(factory.Services, logger, resetear: false, ContrasenaSinAplicar).StartAsync(CancellationToken.None);

        await using var scope = factory.Services.CreateAsyncScope();
        var usuarios = scope.ServiceProvider.GetRequiredService<UserManager<Usuario>>();
        var admin = await usuarios.FindByNameAsync("admin");
        Assert.False(await usuarios.CheckPasswordAsync(admin!, ContrasenaSinAplicar));
        Assert.Contains(logger.Mensajes, m => m.Nivel == LogLevel.Warning
            && m.Texto.Contains("admin") && m.Texto.Contains("UserSeed:ResetPasswords"));
        Assert.DoesNotContain(logger.Mensajes, m => m.Texto.Contains(ContrasenaSinAplicar));
    }

    [Fact]
    public async Task Semilla_ConReset_PoneLaContrasenaConfigurada_YDesbloquea_LosUsuariosSemilla()
    {
        using var factory = fixture.CreateFactory();
        _ = factory.Services;
        var logger = new ListaLogger();
        await using (var previo = factory.Services.CreateAsyncScope())
        {
            // Tres intentos fallidos con la contraseña que no era bloquean la cuenta 5 minutos.
            var gestor = previo.ServiceProvider.GetRequiredService<UserManager<Usuario>>();
            await gestor.SetLockoutEndDateAsync((await gestor.FindByNameAsync("admin"))!, DateTimeOffset.UtcNow.AddMinutes(5));
        }

        await CrearSemilla(factory.Services, logger, resetear: true).StartAsync(CancellationToken.None);

        await using var scope = factory.Services.CreateAsyncScope();
        var usuarios = scope.ServiceProvider.GetRequiredService<UserManager<Usuario>>();
        foreach (var nombre in new[] { "admin", "supervisor", "ejecutor" })
        {
            var usuario = await usuarios.FindByNameAsync(nombre);
            Assert.True(await usuarios.CheckPasswordAsync(usuario!, ContrasenaNueva), nombre);
            Assert.False(await usuarios.IsLockedOutAsync(usuario!), nombre);
        }

        Assert.DoesNotContain(logger.Mensajes, m => m.Nivel >= LogLevel.Warning);
        Assert.DoesNotContain(logger.Mensajes, m => m.Texto.Contains(ContrasenaNueva));
    }

    [Fact]
    public async Task Migracion_ContrasenaDePostgresIncorrecta_ExplicaComoRecuperarse_SinMostrarLaContrasena()
    {
        const string contrasenaMala = "NoEsLaDelVolumen2026";
        var cadena = new Npgsql.NpgsqlConnectionStringBuilder(fixture.AppConnectionString) { Password = contrasenaMala }.ConnectionString;
        await using var contexto = new DbContext(new DbContextOptionsBuilder().UseNpgsql(cadena).Options);

        var ex = await Assert.ThrowsAsync<InvalidOperationException>(() =>
            DatabaseMigrationExecutor.MigrateWithRecoveryAsync(contexto.Database, false, new ListaLogger(), CancellationToken.None));

        Assert.Contains("POSTGRES_PASSWORD", ex.Message);
        Assert.Contains("docker compose down -v", ex.Message);
        Assert.DoesNotContain(contrasenaMala, ex.Message);
        Assert.Equal("28P01", Assert.IsType<Npgsql.PostgresException>(ex.InnerException).SqlState);
    }

    private static IdentitySeedHostedService CrearSemilla(
        IServiceProvider servicios, ListaLogger logger, bool resetear, string contrasena = ContrasenaNueva) =>
        new(
            servicios.GetRequiredService<IServiceScopeFactory>(),
            Options.Create(new UserSeedOptions { Enabled = true, DefaultPassword = contrasena, ResetPasswords = resetear }),
            logger);

    private sealed record Mensaje(LogLevel Nivel, string Texto);

    private sealed class ListaLogger : ILogger<IdentitySeedHostedService>
    {
        public List<Mensaje> Mensajes { get; } = [];

        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;

        public bool IsEnabled(LogLevel logLevel) => true;

        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception, Func<TState, Exception?, string> formatter) =>
            Mensajes.Add(new Mensaje(logLevel, formatter(state, exception)));
    }
}
