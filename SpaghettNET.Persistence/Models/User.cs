namespace SpaghettNET.Persistence.Models;

public class User : Entity<ulong>
{
    /// <summary>
    /// The user's name can change, so...
    /// </summary>
    public required string Username { get; set; }
    
    public required int MessagesSent { get; set; }
    
    public virtual ICollection<UserA> UserAs { get; set; }
    public virtual ICollection<Color> Colors { get; set; }
}
