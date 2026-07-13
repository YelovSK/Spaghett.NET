using System.Diagnostics;
using SpaghettNET.Bot.Extensions;
using SpaghettNET.Platform.Services;
using SpaghettNET.Persistence;
using SpaghettNET.Persistence.Models;
using Microsoft.EntityFrameworkCore;
using NetCord.Rest;
using NetCord.Services.ApplicationCommands;

namespace SpaghettNET.Bot.Modules;

public class ImageModule(BotContext dbContext, ImageService imageService) : ApplicationCommandModule<ApplicationCommandContext>
{
    [SlashCommand("color", "Generates a specific color")]
    public InteractionMessageProperties Color(int r, int g, int b)
    {
        if (!imageService.IsColorValid(r, g, b))
        {
            return "Invalid color";
        }
        
        var stream = imageService.GetSolidColorImage(r, g, b, 300, 300, eImageFormat.Png);

        return InteractionMessageProperties
            .Create()
            .WithAttachments([new AttachmentProperties("color.png", stream)]);
    }
    
    [SlashCommand("colorrandom", "Generates a random color")]
    public InteractionMessageProperties RandomColor()
    {
        var r = Random.Shared.Next(255 + 1);
        var g = Random.Shared.Next(255 + 1);
        var b = Random.Shared.Next(255 + 1);
        
        var stream = imageService.GetSolidColorImage(r, g, b, 300, 300, eImageFormat.Png);

        return InteractionMessageProperties
            .Create()
            .WithContent($"RGB:  {r}, {g}, {b}")
            .WithAttachments([new AttachmentProperties("color.png", stream)]);
    }
    
    [SlashCommand("namecolor", "Names a color")]
    public async Task<string> NameColor(int r, int g, int b, string name)
    {
        if (!imageService.IsColorValid(r, g, b))
        {
            return "Invalid color";
        }
        
        if (await dbContext.Colors.AnyAsync(c => c.Name == name || (c.R == r && c.G == g && c.B == b)))
        {
            return "Color already exists";
        }

        dbContext.Colors.Add(new Color
        {
            Id = Guid.NewGuid(),
            Name = name,
            R = r,
            G = g,
            B = b,
            CreatedByUserId = Context.User.Id,
            CreatedOn = DateTime.UtcNow,
        });
        await dbContext.SaveChangesAsync();

        return "Color was saved";
    }

    [SlashCommand("colors", "Returns all named colors")]
    public async Task<string> Colors()
    {
        var colors = await dbContext.Colors
            .Select(c => $"{c.Name} - created by {c.CreatedByUser.Username}")
            .ToListAsync();

        return colors.Count == 0
            ? "No colors"
            : string.Join(Environment.NewLine, colors);
    }
    
    [SlashCommand("colorbyname", "Displays a named color")]
    public async Task<InteractionMessageProperties> ColorByName(string name)
    {
        var color = await dbContext.Colors
            .Where(c => c.Name == name)
            .SingleOrDefaultAsync();
        
        if (color is null)
        {
            return "Color does not exist";
        }

        var stream = imageService.GetSolidColorImage(color.R, color.G, color.B, 300, 300, eImageFormat.Png);

        return InteractionMessageProperties
            .Create()
            .WithAttachments([new AttachmentProperties("color.png", stream)]);
    }
    
    [SlashCommand("mandelbrot", "It's the funny shape thing")]
    public InteractionMessageProperties Mandelbrot(double zoom, double centerX, double centerY)
    {
        var start = Stopwatch.GetTimestamp();
        var ms = imageService.GenerateMandelbrot(zoom, centerX, centerY, 1000, 600, 600, eImageFormat.Png);
        var time = Stopwatch.GetElapsedTime(start);
        
        return InteractionMessageProperties
            .Create()
            .WithContent($"Generated in {time.Milliseconds}ms")
            .WithAttachments([new AttachmentProperties("mandelbrot.png", ms)]);
    }
}
