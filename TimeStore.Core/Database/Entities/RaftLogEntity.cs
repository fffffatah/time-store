using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TimeStore.Core.Database.Entities;

/// <summary>
/// Represents a log entry in the Raft consensus algorithm.
/// </summary>
[Table("RaftLog")]
public class RaftLogEntity
{
    /// <summary>
    /// The unique identifier for the log entry.
    /// </summary>
    [Key]
    [DatabaseGenerated(DatabaseGeneratedOption.Identity)]
    public long Id { get; set; }
    
    /// <summary>
    /// The term in which this log entry was created.
    /// </summary>
    public long Term { get; set; }
    
    /// <summary>
    /// The command stored in this log entry (serialized as a string).
    /// </summary>
    public string Command { get; set; } = string.Empty;
    
    /// <summary>
    /// Whether this log entry has been applied to the state machine.
    /// </summary>
    public bool Applied { get; set; }
}
