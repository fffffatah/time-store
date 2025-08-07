using System.ComponentModel.DataAnnotations;
using System.ComponentModel.DataAnnotations.Schema;

namespace TimeStore.Core.Database.Entities;

/// <summary>
/// Represents the persistent state of a Raft node.
/// </summary>
[Table("RaftState")]
public class RaftStateEntity
{
    /// <summary>
    /// The ID of the node this state belongs to.
    /// </summary>
    [Key]
    public string NodeId { get; set; } = string.Empty;
    
    /// <summary>
    /// The current term this node is in.
    /// </summary>
    public long CurrentTerm { get; set; }
    
    /// <summary>
    /// The ID of the node this node voted for in the current term, if any.
    /// </summary>
    public string? VotedFor { get; set; }
}
