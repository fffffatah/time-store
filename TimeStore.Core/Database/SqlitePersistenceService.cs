using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;
using TimeStore.Core.Database.Entities;
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

    /// <summary>
    /// Initializes a new instance of the <see cref="SqlitePersistenceService"/> class.
    /// </summary>
    /// <param name="dbContextFactory"> Factory for creating database contexts.</param>
    /// <param name="logger"> Logger for logging messages.</param>
    /// <param name="nodeId"> The unique identifier for this Raft node.</param>
    public SqlitePersistenceService(
        IDbContextFactory<SqliteContext> dbContextFactory, 
        ILogger<SqlitePersistenceService> logger,
        string nodeId)
    {
        _dbContextFactory = dbContextFactory;
        _logger = logger;
        _nodeId = nodeId;
        
        EnsureRaftStateInitialized();
    }

    /// <summary>
    /// Gets the current Raft state from the database.
    /// </summary>
    public (long CurrentTerm, string? VotedFor) GetRaftState()
    {
        using var context = _dbContextFactory.CreateDbContext();
        var state = context.RaftStates
            .FirstOrDefault(s => s.NodeId == _nodeId);

        if (state != null)
        {
            return (state.CurrentTerm, state.VotedFor);
        }

        _logger.LogWarning("Raft state not found for node {NodeId}. Initializing with default values.", _nodeId);
        
        return (0, null);
    }

    /// <summary>
    /// Updates the Raft state in the database.
    /// </summary>
    public async Task UpdateRaftState(long currentTerm, string? votedFor)
    {
        using var context = _dbContextFactory.CreateDbContext();
        var state = await context.RaftStates
            .FirstOrDefaultAsync(s => s.NodeId == _nodeId);
        
        if (state == null)
        {
            state = new RaftStateEntity
            {
                NodeId = _nodeId,
                CurrentTerm = currentTerm,
                VotedFor = votedFor
            };
            context.RaftStates.Add(state);
        }
        else
        {
            state.CurrentTerm = currentTerm;
            state.VotedFor = votedFor;
            context.RaftStates.Update(state);
        }
        
        await context.SaveChangesAsync();
    }

    /// <summary>
    /// Gets all log entries from the database.
    /// </summary>
    public List<RaftLogEntry> GetLogEntries()
    {
        using var context = _dbContextFactory.CreateDbContext();
        var logs = context.RaftLogs
            .OrderBy(l => l.Id)
            .ToList();
        
        return logs.Select(log => new RaftLogEntry(log.Term, log.Command)).ToList();
    }

    /// <summary>
    /// Gets the last log entry from the database.
    /// </summary>
    public RaftLogEntry GetLastLogEntry()
    {
        using var context = _dbContextFactory.CreateDbContext();
        var lastLog = context.RaftLogs
            .OrderByDescending(l => l.Id)
            .FirstOrDefault();
        
        return lastLog != null
            ? new RaftLogEntry(lastLog.Term, lastLog.Command)
            : new RaftLogEntry(0, "");
    }

    /// <summary>
    /// Appends a log entry to the database.
    /// </summary>
    public void AppendLogEntry(RaftLogEntry entry)
    {
        using var context = _dbContextFactory.CreateDbContext();
        var logEntity = new RaftLogEntity
        {
            Term = entry.Term,
            Command = entry.Command,
            Applied = false
        };
        
        context.RaftLogs.Add(logEntity);
        context.SaveChanges();
    }

    /// <summary>
    /// Deletes log entries starting from the specified index.
    /// </summary>
    public void DeleteLogsFromIndex(long index)
    {
        using var context = _dbContextFactory.CreateDbContext();
        var logsToDelete = context.RaftLogs
            .Where(l => l.Id >= index)
            .ToList();
        
        context.RaftLogs.RemoveRange(logsToDelete);
        context.SaveChanges();
    }

    /// <summary>
    /// Marks a log entry as applied to the state machine.
    /// </summary>
    public void MarkLogApplied(long index)
    {
        using var context = _dbContextFactory.CreateDbContext();
        var logEntry = context.RaftLogs.FirstOrDefault(l => l.Id == index);

        if (logEntry == null)
        {
            return;
        }
        
        logEntry.Applied = true;
        context.RaftLogs.Update(logEntry);
        context.SaveChanges();
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
    
    /// <summary>
    /// Ensures that Raft state is initialized for this node.
    /// </summary>
    private void EnsureRaftStateInitialized()
    {
        using var context = _dbContextFactory.CreateDbContext();
        
        var stateExists = context.RaftStates.Any(s => s.NodeId == _nodeId);

        if (stateExists)
        {
            return;
        }
        
        var initialState = new RaftStateEntity
        {
            NodeId = _nodeId,
            CurrentTerm = 0,
            VotedFor = null
        };
            
        context.RaftStates.Add(initialState);
        context.SaveChanges();
            
        _logger.LogInformation("Initialized Raft state for node {NodeId}", _nodeId);
    }
}
