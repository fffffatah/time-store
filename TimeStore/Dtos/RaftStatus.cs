namespace TimeStore.Dtos;

/// <summary>
/// Represents the status of a Raft node.
/// </summary>
public class RaftStatus
{
    /// <summary>
    /// The unique identifier for the Raft node.
    /// </summary>
    public string NodeId { get; set; } = string.Empty;
    
    /// <summary>
    /// The current term of the Raft node.
    /// </summary>
    public string State { get; set; } = string.Empty;
    
    /// <summary>
    /// The current term of the Raft node.
    /// </summary>
    public string? LeaderId { get; set; }
}