using System.Text.RegularExpressions;

namespace Neuterradise.App.Presentation;

/// <summary>
/// Semantic icon keys (document 01 §18) and the built-in line geometry every icon pack falls back to.
/// Icons never change action semantics; navigation keeps its text labels.
/// </summary>
public static partial class IconCatalog
{
    private static readonly Dictionary<string, string> BuiltIn = new(StringComparer.Ordinal)
    {
        ["icon.brand"] = "M12 1.6 A10.4 10.4 0 1 0 12 22.4 A10.4 10.4 0 1 0 12 1.6 Z M12 5.2 A6.8 6.8 0 1 1 12 18.8 A6.8 6.8 0 1 1 12 5.2 Z M12 8.4 L15.6 12 L12 15.6 L8.4 12 Z",
        ["icon.navigation.home"] = "M4 11 L12 4 L20 11 M6 9.5 V20 H18 V9.5 M10 20 V14 H14 V20",
        ["icon.navigation.gallery"] = "M3.5 4.5 H20.5 V19.5 H3.5 Z M3.5 15 L9 10 L13 14 L16 11.5 L20.5 16 M14.75 8.5 A1.25 1.25 0 1 1 17.25 8.5 A1.25 1.25 0 1 1 14.75 8.5",
        ["icon.navigation.import"] = "M12 3.5 V14.5 M8 10.5 L12 14.5 L16 10.5 M4.5 16.5 V19 A1.5 1.5 0 0 0 6 20.5 H18 A1.5 1.5 0 0 0 19.5 19 V16.5",
        ["icon.navigation.settings"] = "M12 8.75 A3.25 3.25 0 1 0 12 15.25 A3.25 3.25 0 1 0 12 8.75 M12 2.5 V5 M12 19 V21.5 M2.5 12 H5 M19 12 H21.5 M5.28 5.28 L7.05 7.05 M16.95 16.95 L18.72 18.72 M18.72 5.28 L16.95 7.05 M7.05 16.95 L5.28 18.72",
        ["icon.navigation.customize"] = "M12 3 L13.7 9.3 L20 11 L13.7 12.7 L12 19 L10.3 12.7 L4 11 L10.3 9.3 Z M18.5 16.5 L19.3 18.7 L21.5 19.5 L19.3 20.3 L18.5 22.5 L17.7 20.3 L15.5 19.5 L17.7 18.7 Z",
        ["icon.action.back"] = "M20 12 H4.5 M11 5.5 L4.5 12 L11 18.5",
        ["icon.action.edit"] = "M4.5 19.5 V15.5 L15.5 4.5 L19.5 8.5 L8.5 19.5 Z M13.5 6.5 L17.5 10.5",
        ["icon.action.delete"] = "M4.5 6.5 H19.5 M9.5 6.5 V4.5 H14.5 V6.5 M6.5 6.5 V19 A1.5 1.5 0 0 0 8 20.5 H16 A1.5 1.5 0 0 0 17.5 19 V6.5 M10 10 V17 M14 10 V17",
        ["icon.action.restore"] = "M6.34 6.34 A8 8 0 1 1 4.6 15.2 M6.34 6.34 V10.6 M6.34 6.34 H10.6",
        ["icon.action.favorite"] = "M12 20 C12 20 3.5 14.8 3.5 9.6 C3.5 6.9 5.7 4.8 8.3 4.8 C10 4.8 11.3 5.7 12 6.9 C12.7 5.7 14 4.8 15.7 4.8 C18.3 4.8 20.5 6.9 20.5 9.6 C20.5 14.8 12 20 12 20 Z",
        ["icon.action.search"] = "M10.5 4 A6.5 6.5 0 1 0 10.5 17 A6.5 6.5 0 1 0 10.5 4 M15.5 15.5 L20.5 20.5",
        ["icon.action.more"] = "M5 12 A1.25 1.25 0 1 0 7.5 12 A1.25 1.25 0 1 0 5 12 M10.75 12 A1.25 1.25 0 1 0 13.25 12 A1.25 1.25 0 1 0 10.75 12 M16.5 12 A1.25 1.25 0 1 0 19 12 A1.25 1.25 0 1 0 16.5 12",
        ["icon.action.add"] = "M12 4.5 V19.5 M4.5 12 H19.5",
        ["icon.action.open"] = "M14 4.5 H19.5 V10 M19.5 4.5 L11 13 M17 14.5 V18.5 A1.5 1.5 0 0 1 15.5 20 H6 A1.5 1.5 0 0 1 4.5 18.5 V9 A1.5 1.5 0 0 1 6 7.5 H10",
        ["icon.action.open-folder"] = "M3.5 6.5 A1.5 1.5 0 0 1 5 5 H9.5 L11.5 7.5 H19 A1.5 1.5 0 0 1 20.5 9 V17.5 A1.5 1.5 0 0 1 19 19 H5 A1.5 1.5 0 0 1 3.5 17.5 Z M9 13.5 H15.5 M13 11 L15.5 13.5 L13 16",
        ["icon.action.play"] = "M8 5.5 L18.5 12 L8 18.5 Z",
        ["icon.action.pause"] = "M9 5.5 V18.5 M15 5.5 V18.5",
        ["icon.action.chevron"] = "M6.5 9.5 L12 15 L17.5 9.5",
        ["icon.action.close"] = "M6 6 L18 18 M18 6 L6 18",
        ["icon.action.crop"] = "M6.5 2.8 V17.5 H21.2 M2.8 6.5 H17.5 V21.2",
        ["icon.action.layout"] = "M3.5 4.5 H20.5 V19.5 H3.5 Z M3.5 9.5 H20.5 M9.5 9.5 V19.5",
        ["icon.action.card"] = "M4.5 3.5 H19.5 V20.5 H4.5 Z M4.5 14.5 H19.5 M7.5 17.2 H14",
        ["icon.media.image"] = "M3.5 5 H20.5 V19 H3.5 Z M3.5 15.5 L9 10.5 L12.5 14 L15.5 11.5 L20.5 16 M14.6 8.6 A1.3 1.3 0 1 1 17.2 8.6 A1.3 1.3 0 1 1 14.6 8.6",
        ["icon.media.video"] = "M3.5 6 H14.5 V18 H3.5 Z M14.5 10.5 L20.5 7 V17 L14.5 13.5 Z",
        ["icon.media.model"] = "M12 3.2 L20 7.6 V16.4 L12 20.8 L4 16.4 V7.6 Z M4 7.6 L12 12 L20 7.6 M12 12 V20.8",
        ["icon.profile.person"] = "M12 4.2 A3.6 3.6 0 1 1 12 11.4 A3.6 3.6 0 1 1 12 4.2 M4.8 20.2 C4.8 16.4 8 14 12 14 C16 14 19.2 16.4 19.2 20.2",
        ["icon.profile.favorite"] = "M12 20.2 C12 20.2 3.6 15.2 3.6 9.4 C3.6 6.7 5.7 4.8 8.1 4.8 C9.8 4.8 11.2 5.8 12 7.1 C12.8 5.8 14.2 4.8 15.9 4.8 C18.3 4.8 20.4 6.7 20.4 9.4 C20.4 15.2 12 20.2 12 20.2 Z",
        ["icon.profile.related"] = "M8.5 7.5 A4.5 4.5 0 1 0 8.5 16.5 A4.5 4.5 0 1 0 8.5 7.5 M15.5 7.5 A4.5 4.5 0 1 1 15.5 16.5 A4.5 4.5 0 1 1 15.5 7.5",
        ["icon.profile.rating"] = "M12 3.5 L14.6 9.2 L20.8 9.9 L16.2 14.1 L17.5 20.2 L12 17.1 L6.5 20.2 L7.8 14.1 L3.2 9.9 L9.4 9.2 Z",
        ["icon.profile.category"] = "M12 3.5 L21 8 L12 12.5 L3 8 Z M3 12 L12 16.5 L21 12 M3 16 L12 20.5 L21 16",
        ["icon.profile.tag"] = "M3.5 11.5 V4.5 A1 1 0 0 1 4.5 3.5 H11.5 L20.5 12.5 A1.5 1.5 0 0 1 20.5 14.6 L14.6 20.5 A1.5 1.5 0 0 1 12.5 20.5 Z M6.85 7.75 A0.9 0.9 0 1 1 8.65 7.75 A0.9 0.9 0 1 1 6.85 7.75",
        ["icon.profile.face"] = "M12 3.5 A8.5 8.5 0 1 0 12 20.5 A8.5 8.5 0 1 0 12 3.5 M8.75 10 V11.5 M15.25 10 V11.5 M8.5 14.75 A4.5 4.5 0 0 0 15.5 14.75",
        ["icon.status.activity"] = "M3 12 H7 L9.5 5.5 L14 18.5 L16.5 12 H21",
        ["icon.status.storage"] = "M4 6.5 A8 3 0 1 0 20 6.5 A8 3 0 1 0 4 6.5 M4 6.5 V17.5 A8 3 0 0 0 20 17.5 V6.5 M4 12 A8 3 0 0 0 20 12",
        ["icon.status.health"] = "M12 3 L20 6 V11.5 C20 16.5 16.5 20 12 21.5 C7.5 20 4 16.5 4 11.5 V6 Z M8.75 12 L11 14.25 L15.25 9.5",
        ["icon.status.warning"] = "M12 4 L21.5 20 H2.5 Z M12 10 V14.5 M12 17.1 V17.4",
        ["icon.status.success"] = "M12 3.5 A8.5 8.5 0 1 0 12 20.5 A8.5 8.5 0 1 0 12 3.5 M8 12.2 L11 15.2 L16.2 9.4",
        ["icon.status.error"] = "M12 3.5 A8.5 8.5 0 1 0 12 20.5 A8.5 8.5 0 1 0 12 3.5 M9.2 9.2 L14.8 14.8 M14.8 9.2 L9.2 14.8",
        ["icon.status.info"] = "M12 3.5 A8.5 8.5 0 1 0 12 20.5 A8.5 8.5 0 1 0 12 3.5 M12 11 V16.5 M12 7.6 V8",
        ["icon.window.minimize"] = "M5 12 H19",
        ["icon.window.maximize"] = "M5.5 5.5 H18.5 V18.5 H5.5 Z",
        ["icon.window.restore"] = "M8.5 5.5 H18.5 V15.5 M5.5 8.5 H15.5 V18.5 H5.5 Z",
        ["icon.window.close"] = "M6 6 L18 18 M18 6 L6 18",
    };

    public static IReadOnlyCollection<string> Keys => BuiltIn.Keys;

    public static bool IsKnownKey(string key) => BuiltIn.ContainsKey(key);

    public static string PathFor(string key) => BuiltIn.TryGetValue(key, out var path) ? path : BuiltIn["icon.action.more"];

    [GeneratedRegex(@"^[MmLlHhVvCcSsQqTtAaZz0-9eE\s,.\-]+$", RegexOptions.CultureInvariant)]
    private static partial Regex PathDataPattern();

    public static bool IsSafePathData(string data) => PathDataPattern().IsMatch(data);
}
