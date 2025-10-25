using System.ComponentModel.DataAnnotations;

namespace Bot.Persistence.Models;

public class Entity<T>
{
    [Key]
    public required T Id { get; set; }
}