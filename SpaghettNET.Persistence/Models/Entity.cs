using System.ComponentModel.DataAnnotations;

namespace SpaghettNET.Persistence.Models;

public class Entity<T>
{
    [Key]
    public required T Id { get; set; }
}