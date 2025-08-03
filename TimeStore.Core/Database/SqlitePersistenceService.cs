using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TimeStore.Core.Raft;

namespace TimeStore.Core.Database;

/// <summary>
/// Provides persistence services for Raft state and log entries using SQLite.
/// </summary>
public class SqlitePersistenceService
{
    private readonly IDbContextFactory<SqliteContext> _dbContextFactory;
    private readonly ILogger<SqlitePersistenceService> _logger;
    private readonly string _nodeId;

    public SqlitePersistenceService(
        IDbContextFactory<SqliteContext> dbContextFactory, 
        ILogger<SqlitePersistenceService> logger,
        string nodeId)
    {
        _dbContextFactory = dbContextFactory;
        _logger = logger;
        _nodeId = nodeId;
        
        // Ensure Raft tables exist
        EnsureRaftTablesExist();
    }

    private void EnsureRaftTablesExist()
    {
        using var context = _dbContextFactory.CreateDbContext();
        
        // Execute raw SQL to create Raft tables if they don't exist
        context.Database.ExecuteSqlRaw(@"
            CREATE TABLE IF NOT EXISTS RaftState (
                NodeId TEXT PRIMARY KEY,
                CurrentTerm INTEGER NOT NULL,
                VotedFor TEXT NULL
            );
            
            CREATE TABLE IF NOT EXISTS RaftLog (
                Id INTEGER PRIMARY KEY AUTOINCREMENT,
                Term INTEGER NOT NULL,
                Command TEXT NOT NULL,
                Applied INTEGER NOT NULL DEFAULT 0
            );
        ");
        
        // Initialize RaftState for this node if it doesn't exist
        var stateExists = context.Database
            .SqlQueryRaw<RaftStateRecord>("SELECT * FROM RaftState")
            .Count(state => state.NodeId == _nodeId) > 0;
        
        if (!stateExists)
        {
            context.Database.ExecuteSqlRaw(
                "INSERT INTO RaftState (NodeId, CurrentTerm, VotedFor) VALUES ({0}, 0, NULL)",
                _nodeId);
        }
    }

    /// <summary>
    /// Gets the current Raft state from the database.
    /// </summary>
    public (long CurrentTerm, string? VotedFor) GetRaftState()
    {
        using var context = _dbContextFactory.CreateDbContext();
        var result = context.Database.SqlQueryRaw<RaftStateRecord>("SELECT * FROM RaftState")
            .FirstOrDefault(state => state.NodeId == _nodeId);
        
        if (result == null)
        {
            _logger.LogWarning("Raft state not found for node {NodeId}. Initializing with default values.", _nodeId);
            return (0, null);
        }
        
        return (result.CurrentTerm, result.VotedFor);
    }

    /// <summary>
    /// Updates the Raft state in the database.
    /// </summary>
    public async Task UpdateRaftState(long currentTerm, string? votedFor)
    {
        using var context = _dbContextFactory.CreateDbContext();
        await context.Database.ExecuteSqlRawAsync(
            "UPDATE RaftState SET CurrentTerm = {0}, VotedFor = {1} WHERE NodeId = {2}",
            currentTerm, votedFor, _nodeId);
    }

    /// <summary>
    /// Gets all log entries from the database.
    /// </summary>
    public List<RaftLogEntry> GetLogEntries()
    {
        using var context = _dbContextFactory.CreateDbContext();
        var records = context.Database.SqlQueryRaw<RaftLogRecord>("SELECT * FROM RaftLog ORDER BY Id")
            .ToList();
        
        return records.Select(r => new RaftLogEntry(r.Term, r.Command)).ToList();
    }

    /// <summary>
    /// Gets the last log entry from the database.
    /// </summary>
    public RaftLogEntry GetLastLogEntry()
    {
        using var context = _dbContextFactory.CreateDbContext();
        var record = context.Database
            .SqlQueryRaw<RaftLogRecord>($"SELECT * FROM RaftLog ORDER BY Id DESC LIMIT 1")
            .FirstOrDefault();
        
        return record != null
            ? new RaftLogEntry(record.Term, record.Command)
            : new RaftLogEntry(0, "");
    }

    /// <summary>
    /// Appends a log entry to the database.
    /// </summary>
    public void AppendLogEntry(RaftLogEntry entry)
    {
        using var context = _dbContextFactory.CreateDbContext();
        context.Database.ExecuteSqlRaw(
            "INSERT INTO RaftLog (Term, Command, Applied) VALUES ({0}, {1}, 0)",
            entry.Term, entry.Command);
    }

    /// <summary>
    /// Deletes log entries starting from the specified index.
    /// </summary>
    public void DeleteLogsFromIndex(long index)
    {
        using var context = _dbContextFactory.CreateDbContext();
        context.Database.ExecuteSqlRaw("DELETE FROM RaftLog WHERE Id >= {0}", index);
    }

    /// <summary>
    /// Marks a log entry as applied to the state machine.
    /// </summary>
    public void MarkLogApplied(long index)
    {
        using var context = _dbContextFactory.CreateDbContext();
        context.Database.ExecuteSqlRaw("UPDATE RaftLog SET Applied = 1 WHERE Id = {0}", index);
    }

    /// <summary>
    /// Adds a time-series data point to the database.
    /// </summary>
    public void AppendTimeSeriesData(Data data)
    {
        using var context = _dbContextFactory.CreateDbContext();
        context.Data.Add(data);
        context.SaveChanges();
    }

    /// <summary>
    /// Gets all time-series data from the database.
    /// </summary>
    public List<Data> GetAllTimeSeriesData()
    {
        using var context = _dbContextFactory.CreateDbContext();
        return context.Data.ToList();
    }
    
    // Private record classes to help with SQL queries
    private record RaftStateRecord(string NodeId, long CurrentTerm, string? VotedFor);
    private record RaftLogRecord(long Id, long Term, string Command);
}
