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
    
    public static IEnumerable<string> SplitWords(this string text)
    {
        // Cuz fuck regex
        var punctuation = text
            .Where(char.IsPunctuation)
            .Distinct()
            .ToArray();

        return text
            .Split()
            .Select(x => x.Trim(punctuation));
    }
}