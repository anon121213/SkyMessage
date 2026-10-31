namespace SkyMessage.Client.UI.Kit;

internal static partial class IconData
{
    /// <summary>Inner SVG markup (shape elements only) of an embedded Lucide icon.</summary>
    public static bool TryGet(string? name, out string markup)
    {
        if (name is not null && Shapes.TryGetValue(name, out var found))
        {
            markup = found;
            return true;
        }
        markup = "";
        return false;
    }

    public static IEnumerable<string> Names => Shapes.Keys;
}
