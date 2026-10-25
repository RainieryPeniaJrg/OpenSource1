using Dapper;
using Microsoft.Extensions.DependencyInjection;
using OpenSource1.Application.Services.Inventario;
using OpenSource1.Application.Services.Registro;
using OpenSource1.Core.Entities;
using OpenSource1.Infrastructure.Services.Registro;

namespace OpenSource1.SmokeTests.TestInfrastructure;

/// <summary>
/// Configuración de las fechas de registro permitidas (Task 8.5) para los tests de posteo: fija el rango general y las
/// excepciones por usuario directamente en la base (sin pasar por Identity) y construye el validador para un usuario concreto.
/// Los tests de una clase comparten base de datos: quien cambie la configuración debe dejarla sin límites al terminar
/// (<see cref="LimpiarAsync"/> en un <c>finally</c>).
/// </summary>
internal sealed class FechasRegistroPrueba(LibroInventarioPrueba prueba)
{
    public async Task GeneralAsync(DateOnly? desde, DateOnly? hasta)
    {
        await using var conexion = prueba.NuevaConexion();
        var filas = await conexion.ExecuteAsync(
            """
            UPDATE "ConfiguracionesRegistro" SET "PermitirRegistroDesde" = @Desde, "PermitirRegistroHasta" = @Hasta
            WHERE "Id" = @Id
            """,
            new { Id = ConfiguracionRegistroIds.General, Desde = desde, Hasta = hasta });
        Assert.Equal(1, filas);
    }

    /// <summary>Excepción propia del usuario (reemplaza la que tuviera).</summary>
    public async Task UsuarioAsync(Guid usuarioId, DateOnly? desde, DateOnly? hasta)
    {
        await using var conexion = prueba.NuevaConexion();
        await conexion.ExecuteAsync("""DELETE FROM "ConfiguracionesRegistroUsuario" WHERE "UsuarioId" = @U""", new { U = usuarioId });
        await conexion.ExecuteAsync(
            """
            INSERT INTO "ConfiguracionesRegistroUsuario"
                ("Id", "UsuarioId", "NombreUsuario", "PermitirRegistroDesde", "PermitirRegistroHasta", "CreatedAtUtc", "CreatedBy", "IsDeleted")
            VALUES (@Id, @U, @Nombre, @Desde, @Hasta, now(), 'test', false)
            """,
            new { Id = Guid.NewGuid(), U = usuarioId, Nombre = $"usuario-{usuarioId:N}"[..20], Desde = desde, Hasta = hasta });
    }

    /// <summary>
    /// Quita la fila general sembrada: borrado lógico (<paramref name="fisico"/> false, la oculta el filtro global) o
    /// físico. <see cref="LimpiarAsync"/> la restaura.
    /// </summary>
    public async Task QuitarGeneralAsync(bool fisico)
    {
        await using var conexion = prueba.NuevaConexion();
        var filas = await conexion.ExecuteAsync(
            fisico
                ? """DELETE FROM "ConfiguracionesRegistro" WHERE "Id" = @Id"""
                : """UPDATE "ConfiguracionesRegistro" SET "IsDeleted" = true WHERE "Id" = @Id""",
            new { Id = ConfiguracionRegistroIds.General });
        Assert.Equal(1, filas);
    }

    public async Task LimpiarAsync()
    {
        await using (var restaurar = prueba.NuevaConexion())
        {
            await restaurar.ExecuteAsync(
                """
                INSERT INTO "ConfiguracionesRegistro" ("Id", "CreatedAtUtc", "CreatedBy", "IsDeleted")
                VALUES (@Id, now(), 'test', false)
                ON CONFLICT ("Id") DO UPDATE SET "IsDeleted" = false
                """,
                new { Id = ConfiguracionRegistroIds.General });
        }

        await GeneralAsync(null, null);
        await using var conexion = prueba.NuevaConexion();
        await conexion.ExecuteAsync("""DELETE FROM "ConfiguracionesRegistroUsuario" """);
    }

    /// <summary>Validador real resuelto en <paramref name="proveedor"/> pero para el usuario indicado (null = proceso del sistema).</summary>
    public static IValidadorFechaRegistro Validador(IServiceProvider proveedor, Guid? usuarioId) =>
        ActivatorUtilities.CreateInstance<ValidadorFechaRegistro>(proveedor, (IUsuarioActual)new Usuario(usuarioId));

    public sealed record Usuario(Guid? Id, bool EsAdministrador = false) : IUsuarioActual
    {
        public string Nombre => Id is null ? "system" : $"usuario-{Id:N}"[..20];
    }
}
