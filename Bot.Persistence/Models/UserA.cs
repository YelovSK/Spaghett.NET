using System.ComponentModel.DataAnnotations.Schema;

namespace Bot.Persistence.Models;

public class UserA : Entity<Guid>
{
    public ulong UserId { get; set; }
    
    /// <summary>
    /// Number of As that was sent
    /// </summary>
    public int Length { get; set; }
    
    public DateTime CreatedOn { get; set; }

    [ForeignKey(nameof(UserId))]
    public virtual User User { get; set; }
}