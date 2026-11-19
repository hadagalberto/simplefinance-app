using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimpleFinance.Domain.Entities;

namespace SimpleFinance.Infrastructure.Persistence.Configurations;

public class TransactionEmbeddingConfiguration : IEntityTypeConfiguration<TransactionEmbedding>
{
    public void Configure(EntityTypeBuilder<TransactionEmbedding> builder)
    {
        builder.ToTable("TransactionEmbeddings");

        builder.HasKey(x => x.Id);

        // AI-CAT-01: first real pgvector column. The HNSW cosine index over this column is added
        // via raw SQL in the migration (EF cannot model a vector index).
        builder.Property(e => e.Embedding)
            .HasColumnType("vector(1536)");

        builder.Property(e => e.EmbeddingModel)
            .HasMaxLength(64);

        builder.Property(e => e.SourceTextHash)
            .HasMaxLength(64);

        // One embedding per transaction.
        builder.HasIndex(e => e.TransactionId)
            .IsUnique();

        // Backs the 16-02 cache-hit lookup (same normalized text → reuse the vector).
        builder.HasIndex(e => new { e.UserId, e.SourceTextHash });
    }
}
