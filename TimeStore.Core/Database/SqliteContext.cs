using Microsoft.EntityFrameworkCore;
using TimeStore.Core.Database.Entities;

namespace TimeStore.Core.Database;

/// <summary>
/// Represents the SQLite database context for TimeStore.
/// </summary>
/// <param name="options">Database options for configuring the context.</param>
public class SqliteContext(DbContextOptions<SqliteContext> options) : DbContext(options)
{
    public DbSet<Data> Data { get; set; } = null!;
    public DbSet<RaftStateEntity> RaftStates { get; set; } = null!;
    public DbSet<RaftLogEntity> RaftLogs { get; set; } = null!;

    /// <summary>
    /// Configures the model for the database context, applying entity configurations and setting primary keys.
    /// </summary>
    /// <param name="modelBuilder">The model builder used to configure the model.</param>
    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        modelBuilder.ApplyConfiguration(new DataEntityTypeConfiguration());
        
        modelBuilder.Entity<RaftStateEntity>()
            .HasKey(e => e.NodeId);
            
        modelBuilder.Entity<RaftLogEntity>()
            .HasKey(e => e.Id);
    }
}