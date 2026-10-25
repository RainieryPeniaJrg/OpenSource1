using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace OpenSource1.Infrastructure.Data;

public static class DatabaseMigrationExecutor
{
    public static async Task MigrateWithRecoveryAsync(
        DatabaseFacade database,
        bool recreateOnPendingModelChanges,
        ILogger logger,
        CancellationToken cancellationToken)
    {
        try
        {
            await database.MigrateAsync(cancellationToken);
        }
        catch (PostgresException ex) when (ex.SqlState == PostgresErrorCodes.InvalidPassword)
        {
            throw new InvalidOperationException(
                "PostgreSQL rejected the password of the connection string (28P01). With docker compose, POSTGRES_PASSWORD is only " +
                "applied when the postgres-data volume is created for the first time: put back in .env the value used then, or " +
                "recreate the volume with 'docker compose down -v' (this deletes all the data).",
                ex);
        }
        catch (PostgresException ex) when (ex.SqlState == "42P04")
        {
            logger.LogWarning(ex, "PostgreSQL database already exists. Retrying migrations.");
            await database.MigrateAsync(cancellationToken);
        }
        catch (InvalidOperationException ex) when (recreateOnPendingModelChanges && IsPendingModelChangesException(ex))
        {
            logger.LogWarning(ex,
                "Pending EF model changes detected during startup. Recreating database in development to match current model.");

            await database.EnsureDeletedAsync(cancellationToken);
            await database.EnsureCreatedAsync(cancellationToken);
        }
    }

    public static bool IsPendingModelChangesException(Exception exception) =>
        exception is InvalidOperationException invalidOperationException
        && invalidOperationException.Message.Contains("PendingModelChangesWarning", StringComparison.OrdinalIgnoreCase);
}
