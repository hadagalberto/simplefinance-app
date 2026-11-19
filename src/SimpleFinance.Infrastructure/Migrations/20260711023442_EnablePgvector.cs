using Microsoft.EntityFrameworkCore.Migrations;

#nullable disable

namespace SimpleFinance.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class EnablePgvector : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            // EF/Npgsql annotation-driven extension enable (from HasPostgresExtension("vector")).
            migrationBuilder.AlterDatabase()
                .Annotation("Npgsql:PostgresExtension:vector", ",,");

            // AI-FND-03: explicit idempotent enable — belt-and-suspenders so re-running the
            // migrate-on-start path is always safe. NO vector table/column this phase (Phase 16).
            migrationBuilder.Sql("CREATE EXTENSION IF NOT EXISTS vector;");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.Sql("DROP EXTENSION IF EXISTS vector;");

            migrationBuilder.AlterDatabase()
                .OldAnnotation("Npgsql:PostgresExtension:vector", ",,");
        }
    }
}
