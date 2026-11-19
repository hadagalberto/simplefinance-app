using System;
using Microsoft.EntityFrameworkCore.Migrations;
using Pgvector;

#nullable disable

namespace SimpleFinance.Infrastructure.Migrations
{
    /// <inheritdoc />
    public partial class AddTransactionEmbeddingAndCategoryFields : Migration
    {
        /// <inheritdoc />
        protected override void Up(MigrationBuilder migrationBuilder)
        {
            migrationBuilder.AddColumn<double>(
                name: "CategoryConfidence",
                table: "Transactions",
                type: "double precision",
                nullable: true);

            migrationBuilder.AddColumn<string>(
                name: "CategoryModel",
                table: "Transactions",
                type: "character varying(64)",
                maxLength: 64,
                nullable: true);

            migrationBuilder.CreateTable(
                name: "TransactionEmbeddings",
                columns: table => new
                {
                    Id = table.Column<Guid>(type: "uuid", nullable: false),
                    TransactionId = table.Column<Guid>(type: "uuid", nullable: false),
                    Embedding = table.Column<Vector>(type: "vector(1536)", nullable: false),
                    EmbeddingModel = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    SourceTextHash = table.Column<string>(type: "character varying(64)", maxLength: 64, nullable: false),
                    CreatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UpdatedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: false),
                    UserId = table.Column<Guid>(type: "uuid", nullable: false),
                    DeletedAt = table.Column<DateTime>(type: "timestamp with time zone", nullable: true)
                },
                constraints: table =>
                {
                    table.PrimaryKey("PK_TransactionEmbeddings", x => x.Id);
                });

            migrationBuilder.CreateIndex(
                name: "IX_TransactionEmbeddings_TransactionId",
                table: "TransactionEmbeddings",
                column: "TransactionId",
                unique: true);

            migrationBuilder.CreateIndex(
                name: "IX_TransactionEmbeddings_UserId_SourceTextHash",
                table: "TransactionEmbeddings",
                columns: new[] { "UserId", "SourceTextHash" });

            // AI-CAT-01: HNSW cosine index for kNN similarity search — EF cannot model a vector
            // index, so add it via raw SQL AFTER the table exists. Idempotent (IF NOT EXISTS) so
            // re-running the migrate-on-start path is always safe. Defaults (m=16, ef_construction=64)
            // are fine for a single-user, few-thousand-row DB.
            migrationBuilder.Sql(
                @"CREATE INDEX IF NOT EXISTS ix_transaction_embeddings_vec
                  ON ""TransactionEmbeddings"" USING hnsw (""Embedding"" vector_cosine_ops);");
        }

        /// <inheritdoc />
        protected override void Down(MigrationBuilder migrationBuilder)
        {
            // Mirror of the raw HNSW index creation in Up() — drop it before the table.
            migrationBuilder.Sql(
                @"DROP INDEX IF EXISTS ix_transaction_embeddings_vec;");

            migrationBuilder.DropTable(
                name: "TransactionEmbeddings");

            migrationBuilder.DropColumn(
                name: "CategoryConfidence",
                table: "Transactions");

            migrationBuilder.DropColumn(
                name: "CategoryModel",
                table: "Transactions");
        }
    }
}
