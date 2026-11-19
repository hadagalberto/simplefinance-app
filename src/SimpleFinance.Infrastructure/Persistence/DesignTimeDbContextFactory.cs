using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Design;

namespace SimpleFinance.Infrastructure.Persistence;

/// <summary>
/// Allows `dotnet ef migrations add` / `dotnet ef database update` to run from the CLI
/// without spinning up the full API host. Reads the connection string from the
/// `ConnectionStrings__Default` environment variable (same env-var-driven convention used
/// by the running API), falling back to a local-dev default so a bare Postgres on localhost
/// works out of the box for `dotnet ef` commands.
/// </summary>
public class DesignTimeDbContextFactory : IDesignTimeDbContextFactory<SimpleFinanceDbContext>
{
    public SimpleFinanceDbContext CreateDbContext(string[] args)
    {
        var connectionString =
            Environment.GetEnvironmentVariable("ConnectionStrings__Default")
            ?? "Host=localhost;Port=5432;Database=simplefinance;Username=simplefinance;Password=changeme";

        var optionsBuilder = new DbContextOptionsBuilder<SimpleFinanceDbContext>();
        // Mirror Program.cs's o.UseVector() so design-time migration generation can map the
        // Phase 16 TransactionEmbedding.Embedding vector(1536) column (matches runtime wiring).
        optionsBuilder.UseNpgsql(connectionString, o => o.UseVector());

        return new SimpleFinanceDbContext(optionsBuilder.Options);
    }
}
