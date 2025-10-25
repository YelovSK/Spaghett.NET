using NetCord.Rest;

namespace Bot.API.Extensions;

public static partial class Extensions
{
    extension(InteractionMessageProperties)
    {
        public static InteractionMessageProperties Create()
        {
            return new();
        }
    }

    public static InteractionMessageProperties WithImage(this InteractionMessageProperties properties, string url)
    {
        return properties.WithEmbeds([new EmbedProperties { Image = url }]);
    }
}