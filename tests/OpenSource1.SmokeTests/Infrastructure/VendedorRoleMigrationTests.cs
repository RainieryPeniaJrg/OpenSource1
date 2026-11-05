using Dapper;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Moq;
using Npgsql;
using OpenSource1.Application.Security;
using OpenSource1.Infrastructure.Identity;
using OpenSource1.SmokeTests.TestInfrastructure;

namespace OpenSource1.SmokeTests.Infrastructure;

[Collection(PostgresCollection.Name)]
public sealed class VendedorRoleMigrationTests(PostgresTestFixture fixture) : IClassFixture<PostgresTestFixture>
{
    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Migracion_SinSemilla_AgregaVendedorSinReemplazarRolesUsuariosOAsignaciones(bool vendedorExistente)
    {
        const string anterior = "20260707013455_AddProfileImagePath";
        var cadena = new NpgsqlConnectionStringBuilder(fixture.IdentityConnectionString)
        {
            Database = "roles_migration_" + Guid.NewGuid().ToString("N")
        }.ConnectionString;
        await using var contexto = new AppIdentityDbContext(new DbContextOptionsBuilder<AppIdentityDbContext>().UseNpgsql(cadena).Options);
        try
        {
            var migrador = contexto.GetService<IMigrator>();
            await migrador.MigrateAsync(anterior);
            await contexto.Database.OpenConnectionAsync();
            var conexion = contexto.Database.GetDbConnection();
            await conexion.ExecuteAsync("""
                INSERT INTO "AspNetRoles" ("Id","Name","NormalizedName","ConcurrencyStamp") VALUES
                    ('ejecutor-existente','Ejecutor','EJECUTOR','no-cambiar');
                INSERT INTO "AspNetUsers" ("Id","UserName","NormalizedUserName","FullName","PasswordHash",
                    "EmailConfirmed","PhoneNumberConfirmed","TwoFactorEnabled","LockoutEnabled","AccessFailedCount") VALUES
                    ('usuario-existente','previo','PREVIO','Usuario previo','hash-previo',false,false,false,false,0);
                INSERT INTO "AspNetUserRoles" ("UserId","RoleId") VALUES ('usuario-existente','ejecutor-existente');
                """);
            if (vendedorExistente)
                await conexion.ExecuteAsync("""
                    INSERT INTO "AspNetRoles" ("Id","Name","NormalizedName","ConcurrencyStamp") VALUES
                        ('vendedor-propio','Vendedor','VENDEDOR','conservar-sello');
                    """);

            var semilla = new IdentitySeedHostedService(Mock.Of<IServiceScopeFactory>(),
                Options.Create(new UserSeedOptions { Enabled = false }), NullLogger<IdentitySeedHostedService>.Instance);
            await semilla.StartAsync(CancellationToken.None);
            await migrador.MigrateAsync();

            var id = await conexion.QuerySingleOrDefaultAsync<string>("""SELECT "Id" FROM "AspNetRoles" WHERE "NormalizedName"='VENDEDOR'""");
            Assert.NotNull(id);
            Assert.Equal(vendedorExistente ? "vendedor-propio" : "13e3d9b8-e6ad-4a01-9b18-39e6e48d9ff7", id);
            Assert.Equal(1, await conexion.ExecuteScalarAsync<int>("""SELECT COUNT(*) FROM "AspNetUsers" """));
            Assert.Equal("hash-previo", await conexion.ExecuteScalarAsync<string>("""SELECT "PasswordHash" FROM "AspNetUsers" WHERE "Id"='usuario-existente'"""));
            Assert.Equal("no-cambiar", await conexion.ExecuteScalarAsync<string>("""SELECT "ConcurrencyStamp" FROM "AspNetRoles" WHERE "Id"='ejecutor-existente'"""));
            if (vendedorExistente)
                Assert.Equal("conservar-sello", await conexion.ExecuteScalarAsync<string>("""SELECT "ConcurrencyStamp" FROM "AspNetRoles" WHERE "Id"='vendedor-propio'"""));

            await conexion.ExecuteAsync("""INSERT INTO "AspNetUserRoles" ("UserId","RoleId") VALUES ('usuario-existente',@Id)""", new { Id = id });
            await migrador.MigrateAsync(anterior);
            Assert.Equal(2, await conexion.ExecuteScalarAsync<int>("""SELECT COUNT(*) FROM "AspNetUserRoles" WHERE "UserId"='usuario-existente'"""));
            Assert.Equal(id, await conexion.ExecuteScalarAsync<string>("""SELECT "Id" FROM "AspNetRoles" WHERE "NormalizedName"='VENDEDOR'"""));
            await migrador.MigrateAsync();
            Assert.Equal(1, await conexion.ExecuteScalarAsync<int>("""SELECT COUNT(*) FROM "AspNetRoles" WHERE "NormalizedName"='VENDEDOR'"""));
        }
        finally
        {
            await contexto.Database.CloseConnectionAsync();
            await contexto.Database.EnsureDeletedAsync();
        }
    }
}
