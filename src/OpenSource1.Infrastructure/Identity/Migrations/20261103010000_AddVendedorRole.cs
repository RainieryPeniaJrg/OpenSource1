using Microsoft.EntityFrameworkCore.Infrastructure;
using Microsoft.EntityFrameworkCore.Migrations;

namespace OpenSource1.Infrastructure.Identity.Migrations;

/// <summary>Provision the production sales role independently of optional demo-user seeding.</summary>
[DbContext(typeof(AppIdentityDbContext))]
[Migration("20261103010000_AddVendedorRole")]
public sealed class AddVendedorRole : Migration
{
    protected override void Up(MigrationBuilder migrationBuilder)
    {
        migrationBuilder.Sql("""
            INSERT INTO "AspNetRoles" ("Id", "Name", "NormalizedName", "ConcurrencyStamp")
            SELECT '13e3d9b8-e6ad-4a01-9b18-39e6e48d9ff7', 'Vendedor', 'VENDEDOR', '13e3d9b8-e6ad-4a01-9b18-39e6e48d9ff7'
            WHERE NOT EXISTS (SELECT 1 FROM "AspNetRoles" WHERE "NormalizedName" = 'VENDEDOR')
            ON CONFLICT ("NormalizedName") DO NOTHING;
            """);
    }

    protected override void Down(MigrationBuilder migrationBuilder)
    {
        // The role may predate this migration or already have users assigned.
        // Keep it and its memberships on rollback; never revoke existing access or delete users.
    }
}
