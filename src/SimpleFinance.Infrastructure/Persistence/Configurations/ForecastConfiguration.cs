using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;
using SimpleFinance.Domain.Entities;

namespace SimpleFinance.Infrastructure.Persistence.Configurations;

/// <summary>
/// EF config for <see cref="Forecast"/> (Phase 19, AI-FCT-01). The <see cref="Forecast.ByCategoryJson"/>
/// is stored as a native Postgres <c>jsonb</c> column (the deterministic per-category drivers that ground
/// the displayed projection). The five money columns use <c>HasPrecision(18, 2)</c> to match the rest of
/// the schema. The UNIQUE <c>(UserId, Month)</c> index is the replace-per-month spine: it scopes the
/// daily job's overwrite to exactly one row per user per month. Auto-discovered via
/// <c>ApplyConfigurationsFromAssembly</c> — no manual registration.
/// </summary>
public class ForecastConfiguration : IEntityTypeConfiguration<Forecast>
{
    public void Configure(EntityTypeBuilder<Forecast> builder)
    {
        builder.ToTable("Forecasts");

        builder.HasKey(x => x.Id);

        builder.Property(x => x.ProjectedIncome).HasPrecision(18, 2);
        builder.Property(x => x.ProjectedExpense).HasPrecision(18, 2);
        builder.Property(x => x.ProjectedBalance).HasPrecision(18, 2);
        builder.Property(x => x.BalanceLow).HasPrecision(18, 2);
        builder.Property(x => x.BalanceHigh).HasPrecision(18, 2);

        builder.Property(x => x.ByCategoryJson)
            .HasColumnType("jsonb");

        builder.Property(x => x.Method)
            .HasMaxLength(50);

        // Replace-per-month spine: one forecast per (UserId, Month); the daily job overwrites it in place.
        builder.HasIndex(x => new { x.UserId, x.Month })
            .IsUnique();
    }
}
