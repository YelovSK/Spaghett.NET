using System.ComponentModel.DataAnnotations.Schema;

namespace SpaghettNET.Persistence.Models;

public class Color : Entity<Guid>
{
    public string Name { get; set; }
    
    public int R { get; set; }
    
    public int G { get; set; }
    
    public int B { get; set; }
    
    public ulong CreatedByUserId { get; set; }
    
    public DateTime CreatedOn { get; set; }
    
    [ForeignKey(nameof(CreatedByUserId))]
    public virtual User CreatedByUser { get; set; }
}