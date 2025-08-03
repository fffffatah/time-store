using Microsoft.EntityFrameworkCore;

namespace TimeStore.Core.Database;

/// <summary>
/// Represents the SQLite database context for TimeStore.
/// </summary>
/// <param name="options">Database options for configuring the context.</param>
public class SqliteContext(DbContextOptions<SqliteContext> options) : DbContext(options)
{
    public DbSet<Data> Data { get; set; } = null!;

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.ApplyConfiguration(new DataEntityTypeConfiguration());
    }
}