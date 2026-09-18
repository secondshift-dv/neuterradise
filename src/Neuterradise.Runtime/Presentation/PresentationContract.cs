using System.Diagnostics.CodeAnalysis;

namespace Neuterradise.App.Presentation;

/// <summary>
/// Versioned Presentation Contract (document 01). Built-in and user presentation travel the same path:
/// pack → registry → bindings → resolver → compiler → immutable render plan → renderer.
/// </summary>
public static class PresentationContract
{
    /// <summary>The contract version this renderer implements.</summary>
    public const int Version = 1;

    /// <summary>The Presentation Pack manifest schema this build reads.</summary>
    public const int PackSchemaVersion = 1;

    /// <summary>Binding state payload schema.</summary>
    public const int BindingSchemaVersion = 1;

    /// <summary>The application-owned pack. It is an ordinary pack: same reader, validator and compiler.</summary>
    public const string BuiltInPackId = "builtin.neuterradise";
}

/// <summary>The family a definition kind belongs to (document 01 §4).</summary>
public enum DefinitionFamily
{
    Structural,
    Visual,
    MotionEnvironment,
    AppearanceAsset,
}

/// <summary>Registered definition kinds. New kinds are added here plus a compiler; no schema change is needed.</summary>
public static class DefinitionKinds
{
    public const string Theme = "theme";
    public const string Typography = "typography";
    public const string IconPack = "icon-pack";
    public const string MotionPreset = "motion-preset";
    public const string HomeLayout = "home-layout";
    public const string Backdrop = "backdrop";
    public const string SpotlightStyle = "spotlight-style";
    public const string GalleryLayout = "gallery-layout";
    public const string ProfileCard = "profile-card";
    public const string ProfileLayout = "profile-layout";
    public const string CoverFrame = "cover-frame";
    public const string ProfileEnvironment = "profile-environment";
    public const string MediaTile = "media-tile";
    public const string MediaBorder = "media-border";
    public const string MediaInfoLayout = "media-info-layout";

    public static IReadOnlyDictionary<string, DefinitionFamily> Families { get; } = new Dictionary<string, DefinitionFamily>(StringComparer.Ordinal)
    {
        [Theme] = DefinitionFamily.Visual,
        [Typography] = DefinitionFamily.AppearanceAsset,
        [IconPack] = DefinitionFamily.AppearanceAsset,
        [MotionPreset] = DefinitionFamily.MotionEnvironment,
        [HomeLayout] = DefinitionFamily.Structural,
        [Backdrop] = DefinitionFamily.MotionEnvironment,
        [SpotlightStyle] = DefinitionFamily.Visual,
        [GalleryLayout] = DefinitionFamily.Structural,
        [ProfileCard] = DefinitionFamily.Visual,
        [ProfileLayout] = DefinitionFamily.Structural,
        [CoverFrame] = DefinitionFamily.Visual,
        [ProfileEnvironment] = DefinitionFamily.MotionEnvironment,
        [MediaTile] = DefinitionFamily.Visual,
        [MediaBorder] = DefinitionFamily.Visual,
        [MediaInfoLayout] = DefinitionFamily.Structural,
    };

    public static IReadOnlyCollection<string> All => (IReadOnlyCollection<string>)Families.Keys;

    public static bool IsKnown(string? kind) => kind is not null && Families.ContainsKey(kind);
}

/// <summary>Where a binding is persisted. Built-in defaults are never persisted.</summary>
public enum ScopeKind
{
    Global,
    Surface,
    Profile,
    Item,
}

/// <summary>Which scope actually supplied a resolved value; the UI shows this as inherited/overridden.</summary>
public enum ResolutionSource
{
    BuiltInDefault,
    Global,
    Surface,
    Profile,
    Item,
}

/// <summary>Stable identity of a definition: pack + definition id. Display names are never identity.</summary>
public readonly record struct DefinitionRef(string PackId, string DefinitionId)
{
    public override string ToString() => $"{PackId}/{DefinitionId}";

    public static bool TryParse(string? text, [NotNullWhen(true)] out DefinitionRef? value)
    {
        value = null;
        if (string.IsNullOrWhiteSpace(text))
        {
            return false;
        }

        var slash = text.IndexOf('/');
        if (slash <= 0 || slash == text.Length - 1)
        {
            return false;
        }

        value = new DefinitionRef(text[..slash], text[(slash + 1)..]);
        return true;
    }

    public static DefinitionRef BuiltIn(string definitionId) => new(PresentationContract.BuiltInPackId, definitionId);
}

/// <summary>How the Customization Center edits a slot. Complex kinds bind to specialised editors.</summary>
public enum EditorKind
{
    DefinitionChooser,
    CoverEditor,
    BannerEditor,
    StateEditor,
}

/// <summary>Customization Center sections (document 01 §21).</summary>
public static class CustomizationCategories
{
    public const string Appearance = "appearance";
    public const string Home = "home";
    public const string Gallery = "gallery";
    public const string Profile = "profile";
    public const string Media = "media";
    public const string Library = "library";

    public static IReadOnlyList<string> Ordered { get; } = [Appearance, Home, Gallery, Profile, Media, Library];
}

/// <summary>
/// One customizable slot with its editor discovery metadata. The Customization Center renders sections
/// from this table, so a new definition kind appears without editing unrelated pages.
/// </summary>
public sealed record SlotDescriptor(
    string Id,
    string? Kind,
    string Category,
    string LabelKey,
    string LabelFallback,
    string DescriptionFallback,
    IReadOnlyList<ScopeKind> Scopes,
    string? Surface,
    DefinitionRef? Default,
    EditorKind Editor,
    bool SupportsLivePreview = true,
    string? StateSchema = null)
{
    public bool IsStateOnly => Kind is null;

    public bool AllowsScope(ScopeKind scope) => Scopes.Contains(scope);
}

/// <summary>Customization slots. Slot ids are stable persisted identifiers.</summary>
public static class PresentationSlots
{
    public const string Theme = "appearance.theme";
    public const string Typography = "appearance.typography";
    public const string Icons = "appearance.icons";
    public const string Motion = "appearance.motion";
    public const string HomeLayout = "home.layout";
    public const string HomeBackdrop = "home.backdrop";
    public const string HomeSpotlight = "home.spotlight";
    public const string GalleryLayout = "gallery.layout";
    public const string GalleryCard = "gallery.card";
    public const string GalleryDensity = "gallery.density";
    public const string ProfileLayout = "profile.layout";
    public const string ProfileCover = "profile.cover";
    public const string ProfileBanner = "profile.banner";
    public const string ProfileFrame = "profile.frame";
    public const string ProfileEnvironment = "profile.environment";
    public const string MediaTile = "media.tile";
    public const string MediaBorder = "media.border";
    public const string MediaInfo = "media.info";

    /// <summary>Per-surface media presentation deltas over the canonical Profile Cover/Banner state.</summary>
    public const string CardCover = "surface.card.cover";
    public const string CardBanner = "surface.card.banner";
    public const string SpotlightCover = "surface.spotlight.cover";
    public const string SpotlightBanner = "surface.spotlight.banner";

    private static readonly ScopeKind[] GlobalOnly = [ScopeKind.Global];
    private static readonly ScopeKind[] GlobalSurface = [ScopeKind.Global, ScopeKind.Surface];
    private static readonly ScopeKind[] GlobalProfile = [ScopeKind.Global, ScopeKind.Profile];
    private static readonly ScopeKind[] ProfileOnly = [ScopeKind.Profile];
    private static readonly ScopeKind[] MediaScopes = [ScopeKind.Global, ScopeKind.Profile, ScopeKind.Item];

    public static IReadOnlyList<SlotDescriptor> All { get; } =
    [
        new(Theme, DefinitionKinds.Theme, CustomizationCategories.Appearance, "Customize.Theme", "Theme",
            "Colour, material and atmosphere personality for the whole application.", GlobalOnly, null,
            DefinitionRef.BuiltIn("builtin.neuterradise.theme.abyss"), EditorKind.DefinitionChooser),
        new(Typography, DefinitionKinds.Typography, CustomizationCategories.Appearance, "Customize.Typography", "Typography",
            "Font families and scale for display, headings, body and metadata.", GlobalOnly, null,
            DefinitionRef.BuiltIn("builtin.neuterradise.type.standard"), EditorKind.DefinitionChooser),
        new(Icons, DefinitionKinds.IconPack, CustomizationCategories.Appearance, "Customize.Icons", "Icons",
            "The icon set used by navigation, actions and media badges.", GlobalOnly, null,
            DefinitionRef.BuiltIn("builtin.neuterradise.icons.line"), EditorKind.DefinitionChooser),
        new(Motion, DefinitionKinds.MotionPreset, CustomizationCategories.Appearance, "Customize.Motion", "Motion",
            "How lively transitions, hover and ambient motion feel. Reduced Motion always wins.", GlobalOnly, null,
            DefinitionRef.BuiltIn("builtin.neuterradise.motion.cinematic"), EditorKind.DefinitionChooser),

        new(HomeLayout, DefinitionKinds.HomeLayout, CustomizationCategories.Home, "Customize.Home.Layout", "Layout",
            "How the Spotlight and the rails below it are arranged.", GlobalSurface, "home",
            DefinitionRef.BuiltIn("builtin.neuterradise.home.lobby"), EditorKind.DefinitionChooser),
        new(HomeBackdrop, DefinitionKinds.Backdrop, CustomizationCategories.Home, "Customize.Home.Backdrop", "Backdrop",
            "The living background behind Home.", GlobalSurface, "home",
            DefinitionRef.BuiltIn("builtin.neuterradise.backdrop.nebula"), EditorKind.DefinitionChooser),
        new(HomeSpotlight, DefinitionKinds.SpotlightStyle, CustomizationCategories.Home, "Customize.Home.Spotlight", "Spotlight",
            "Composition and motion of the featured Profile.", GlobalSurface, "home",
            DefinitionRef.BuiltIn("builtin.neuterradise.spotlight.cinematic"), EditorKind.DefinitionChooser),

        new(GalleryLayout, DefinitionKinds.GalleryLayout, CustomizationCategories.Gallery, "Customize.Gallery.Layout", "Layout",
            "How Profiles are arranged: grid, poster wall, list or carousel. Always virtualized.", GlobalSurface, "gallery",
            DefinitionRef.BuiltIn("builtin.neuterradise.gallery.grid"), EditorKind.DefinitionChooser),
        new(GalleryCard, DefinitionKinds.ProfileCard, CustomizationCategories.Gallery, "Customize.Gallery.Card", "Card",
            "How one Profile is represented inside the collection.", [ScopeKind.Global, ScopeKind.Surface, ScopeKind.Profile], "gallery",
            DefinitionRef.BuiltIn("builtin.neuterradise.card.cinematic"), EditorKind.DefinitionChooser),
        new(GalleryDensity, null, CustomizationCategories.Gallery, "Customize.Gallery.Density", "Density & details",
            "Card size, information density and which details appear.", GlobalSurface, "gallery",
            null, EditorKind.StateEditor, StateSchema: "gallery-density"),

        new(ProfileLayout, DefinitionKinds.ProfileLayout, CustomizationCategories.Profile, "Customize.Profile.Layout", "Layout",
            "Arrangement of the environment, Cover, identity and sections.", GlobalProfile, "profile",
            DefinitionRef.BuiltIn("builtin.neuterradise.profile.cinematic"), EditorKind.DefinitionChooser),
        new(ProfileCover, null, CustomizationCategories.Profile, "Customize.Profile.Cover", "Cover",
            "Cover image, focal point, zoom and framing.", ProfileOnly, "profile",
            null, EditorKind.CoverEditor, StateSchema: "media-transform"),
        new(ProfileBanner, null, CustomizationCategories.Profile, "Customize.Profile.Banner", "Banner",
            "Banner media, framing and the playback window.", ProfileOnly, "profile",
            null, EditorKind.BannerEditor, StateSchema: "media-transform+playback"),
        new(ProfileFrame, DefinitionKinds.CoverFrame, CustomizationCategories.Profile, "Customize.Profile.Frame", "Frame",
            "The collectible frame around the Cover.", GlobalProfile, "profile",
            DefinitionRef.BuiltIn("builtin.neuterradise.frame.none"), EditorKind.DefinitionChooser),
        new(ProfileEnvironment, DefinitionKinds.ProfileEnvironment, CustomizationCategories.Profile, "Customize.Profile.Environment", "Environment",
            "Backdrop and atmosphere behind the Profile.", GlobalProfile, "profile",
            DefinitionRef.BuiltIn("builtin.neuterradise.environment.living-banner"), EditorKind.DefinitionChooser),

        new(MediaTile, DefinitionKinds.MediaTile, CustomizationCategories.Media, "Customize.Media.Tile", "Tile",
            "Composition of each media tile.", MediaScopes, "media",
            DefinitionRef.BuiltIn("builtin.neuterradise.tile.immersive"), EditorKind.DefinitionChooser),
        new(MediaBorder, DefinitionKinds.MediaBorder, CustomizationCategories.Media, "Customize.Media.Border", "Border",
            "Edge and selection treatment of media tiles.", MediaScopes, "media",
            DefinitionRef.BuiltIn("builtin.neuterradise.border.hairline"), EditorKind.DefinitionChooser),
        new(MediaInfo, DefinitionKinds.MediaInfoLayout, CustomizationCategories.Media, "Customize.Media.Info", "Info layout",
            "Where and how much media information appears.", MediaScopes, "media",
            DefinitionRef.BuiltIn("builtin.neuterradise.info.overlay"), EditorKind.DefinitionChooser),

        new(CardCover, null, CustomizationCategories.Gallery, "Customize.Surface.CardCover", "Card Cover framing",
            "A card-only adjustment over the Profile Cover. Unset means the card inherits the Profile Cover.", ProfileOnly, "gallery",
            null, EditorKind.CoverEditor, StateSchema: "media-transform-delta"),
        new(CardBanner, null, CustomizationCategories.Gallery, "Customize.Surface.CardBanner", "Card Banner framing",
            "A card-only adjustment over the Profile Banner.", ProfileOnly, "gallery",
            null, EditorKind.BannerEditor, StateSchema: "media-transform-delta"),
        new(SpotlightCover, null, CustomizationCategories.Home, "Customize.Surface.SpotlightCover", "Spotlight Cover framing",
            "A Spotlight-only adjustment over the Profile Cover.", ProfileOnly, "home",
            null, EditorKind.CoverEditor, StateSchema: "media-transform-delta"),
        new(SpotlightBanner, null, CustomizationCategories.Home, "Customize.Surface.SpotlightBanner", "Spotlight Banner framing",
            "A Spotlight-only adjustment over the Profile Banner.", ProfileOnly, "home",
            null, EditorKind.BannerEditor, StateSchema: "media-transform-delta"),
    ];

    private static readonly Dictionary<string, SlotDescriptor> ById = All.ToDictionary(slot => slot.Id, StringComparer.Ordinal);

    public static bool TryGet(string? id, [NotNullWhen(true)] out SlotDescriptor? descriptor)
    {
        descriptor = null;
        return id is not null && ById.TryGetValue(id, out descriptor);
    }

    public static SlotDescriptor Get(string id) =>
        ById.TryGetValue(id, out var descriptor)
            ? descriptor
            : throw new ArgumentException($"Unknown presentation slot '{id}'.", nameof(id));

    public static IEnumerable<SlotDescriptor> ForKind(string kind) => All.Where(slot => slot.Kind == kind);
}

/// <summary>
/// Semantic data slots: the stable bridge between domain data and presentation definitions
/// (document 01 §7). Definitions name these, never tables, columns or view-model members.
/// </summary>
public static class SemanticSlots
{
    public const string ProfileId = "profile.identity.id";
    public const string ProfileName = "profile.identity.name";
    public const string ProfileCover = "profile.identity.cover";
    public const string ProfileBanner = "profile.identity.banner";
    public const string ProfileFrame = "profile.identity.frame";
    public const string ProfileRating = "profile.identity.rating";
    public const string ProfileTier = "profile.identity.tier";
    public const string ProfileFavorite = "profile.identity.favorite";
    public const string ProfileCategory = "profile.identity.category";
    public const string ProfileTags = "profile.identity.tags";
    public const string ProfileOverview = "profile.overview";
    public const string ProfileNotes = "profile.notes";
    public const string ProfileMedia = "profile.media.collection";
    public const string ProfileRelated = "profile.related.collection";
    public const string ProfileStatistics = "profile.statistics";
    public const string ProfileMediaCount = "profile.statistics.media-count";
    public const string ProfileRelatedCount = "profile.statistics.related-count";
    public const string ProfileOpenDirectory = "profile.location.open-directory-action";

    public const string MediaId = "media.id";
    public const string MediaPreview = "media.preview";
    public const string MediaPoster = "media.poster";
    public const string MediaName = "media.name";
    public const string MediaType = "media.type";
    public const string MediaDuration = "media.duration";
    public const string MediaDimensions = "media.dimensions";
    public const string MediaFavorite = "media.favorite";
    public const string MediaRating = "media.rating";
    public const string MediaExif = "media.exif";
    public const string MediaExifCamera = "media.exif.camera";
    public const string MediaExifLens = "media.exif.lens";
    public const string MediaExifCaptureTime = "media.exif.capture-time";
    public const string MediaRelations = "media.relations";
    public const string MediaOpenDefault = "media.file.open-default-action";
    public const string MediaOpenDirectory = "media.file.open-directory-action";

    public const string HomeSpotlight = "home.spotlight";
    public const string HomeRecent = "home.recent";
    public const string HomeFavorites = "home.favorites";
    public const string HomeProfiles = "home.profiles";
    public const string HomeCollections = "home.collections";
    public const string HomeStatistics = "home.statistics";
    public const string HomeActivity = "home.activity-summary";
    public const string HomeAmbientSource = "home.ambient-source";

    public const string GalleryItems = "gallery.items";
    public const string GallerySelection = "gallery.selection";
    public const string GalleryQuery = "gallery.query";
    public const string GalleryFilters = "gallery.filters";
    public const string GallerySort = "gallery.sort";
    public const string GalleryPagination = "gallery.pagination";
    public const string GalleryLayoutState = "gallery.layout-state";
    public const string GalleryCardDefinition = "gallery.card-definition";

    public const string ImportSource = "import.source";
    public const string ImportDestination = "import.destination-profile";
    public const string ImportReview = "import.review";
    public const string ImportProgress = "import.progress";
    public const string ImportActivity = "import.activity";
    public const string ImportNeedsAttention = "import.needs-attention";
    public const string ImportActions = "import.actions";

    /// <summary>Slots a card composition may bind to (text, image and badge nodes).</summary>
    public static IReadOnlySet<string> CardBindable { get; } = new HashSet<string>(StringComparer.Ordinal)
    {
        ProfileName, ProfileCover, ProfileBanner, ProfileFrame, ProfileRating, ProfileTier, ProfileFavorite,
        ProfileCategory, ProfileTags, ProfileOverview, ProfileMediaCount, ProfileRelatedCount,
    };

    /// <summary>Deprecated slot → replacement. Kept for the supported migration window (document 01 §7.6).</summary>
    public static IReadOnlyDictionary<string, string> Deprecated { get; } = new Dictionary<string, string>(StringComparer.Ordinal)
    {
        ["profile.name"] = ProfileName,
        ["profile.cover"] = ProfileCover,
        ["profile.banner"] = ProfileBanner,
    };

    public static string Canonical(string slot) => Deprecated.TryGetValue(slot, out var replacement) ? replacement : slot;
}
