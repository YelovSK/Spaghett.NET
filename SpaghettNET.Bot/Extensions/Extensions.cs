namespace SpaghettNET.Bot.Extensions;

public static partial class Extensions
{
    public static string Join(this IEnumerable<string> enumerable, string separator)
    {
        return string.Join(separator, enumerable);
    }
    
    public static string Join(this IEnumerable<char> enumerable, string separator)
    {
        return string.Join(separator, enumerable);
    }
}