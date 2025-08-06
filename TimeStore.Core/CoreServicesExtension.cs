using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using TimeStore.Core.Database;
using TimeStore.Core.Raft;

namespace TimeStore.Core;

public static class CoreServicesExtension
{
    public static void AddCoreServices(this IServiceCollection services, IConfiguration configuration)
    {
        // Get node configuration from environment variables or configuration
        var nodeId = configuration["Raft:NodeId"] ?? "node1";
        var sqlitePath = configuration["Database:Path"] ?? $"timestore_{nodeId}.db";
        var peerNodes = (configuration["Raft:Peers"] ?? "")
            .Split(',', StringSplitOptions.RemoveEmptyEntries)
            .ToList();
            
        // Add SQLite with the database factory for concurrent access
        services.AddDbContextFactory<SqliteContext>(options =>
        {
            options.UseSqlite($"Data Source={sqlitePath}");
        });
        
        // Register the persistence service as a singleton
        services.AddSingleton<SqlitePersistenceService>(sp => 
        {
            var dbContextFactory = sp.GetRequiredService<IDbContextFactory<SqliteContext>>();
            var logger = sp.GetRequiredService<ILogger<SqlitePersistenceService>>();
            return new SqlitePersistenceService(dbContextFactory, logger, nodeId);
        });
        
        // Configure and register the HTTP client for node communication
        services.AddHttpClient("RaftClient", client =>
        {
            client.Timeout = TimeSpan.FromSeconds(5); // Set reasonable timeout
        }).ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler
        {
            ServerCertificateCustomValidationCallback = (_, _, _, _) => true, // For dev environment
        });
        
        // Register the Raft service as a singleton
        services.AddSingleton<IRaftService>(sp => 
        {
            var persistence = sp.GetRequiredService<SqlitePersistenceService>();
            var httpClientFactory = sp.GetRequiredService<IHttpClientFactory>();
            var logger = sp.GetRequiredService<ILogger<RaftService>>();
            
            return new RaftService(
                nodeId,
                peerNodes,
                persistence,
                httpClientFactory.CreateClient("RaftClient"),
                logger);
        });
        
        // Ensure database exists
        var dbContextFactory = services.BuildServiceProvider().GetRequiredService<IDbContextFactory<SqliteContext>>();
        using var context = dbContextFactory.CreateDbContext();
        context.Database.EnsureCreated();
    }
}