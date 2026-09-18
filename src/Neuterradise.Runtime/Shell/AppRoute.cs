using Neuterradise.App.Gallery;
using Neuterradise.App.Localization;

namespace Neuterradise.App.Shell;

public abstract record AppRoute;

public sealed record HomeRoute : AppRoute;

/// <summary>
/// Snapshot of the Gallery query/pagination state (search, filters, sort, page, page size) at
/// the moment the user left Gallery for another destination. Card size/density are not carried
/// here because SetCardSize/SetCardVariant/SetInformationDensity persist immediately through
/// SettingsOperations, so re-entering Gallery already restores them from durable preferences.
/// </summary>
public sealed record GalleryOriginState(
    string? SearchText,
    string? CategoryId,
    string? TagId,
    bool FavoritesOnly,
    int? MinRating,
    int? MaxRating,
    bool HasImages,
    bool HasVideos,
    bool HasModels,
    Guid? RelatedToProfileId,
    string? RelatedToProfileName,
    bool HasSharedMedia,
    bool HasConfirmedFaceRelation,
    bool HasManualRelation,
    bool ShowUnresolvedUnknown,
    GallerySortOrder SortOrder,
    int PageIndex,
    int PageSize,
    Guid? SelectedProfileId = null,
    Guid? AnchorProfileId = null,
    double AnchorOffsetDip = 0);

public sealed record GalleryRoute(GalleryOriginState? Origin = null) : AppRoute;

public sealed record ImportRoute : AppRoute;

public enum SettingsSection
{
    General,
    Display,
    Language,
    Library,
    Organization,
    System,
    People,
    Activity,
    Trash,
    About,
}

public enum SettingsSubsection
{
    Categories,
    Tags,
    MediaPreferences,
    ImportPreferences,
    LibraryHealth,
}

public sealed record SettingsRoute(
    SettingsSection Section = SettingsSection.General,
    SettingsSubsection? Subsection = null) : AppRoute;

/// <summary>
/// A Profile page. <paramref name="InspectAssetId"/> opens the media inspector on that asset once
/// the page is shown; media detail is an inspector inside the Profile, not a destination of its own.
/// </summary>
public sealed record ProfileRoute(
    Guid ProfileId,
    MediaOriginState? Origin = null,
    Guid? InspectAssetId = null) : AppRoute;

public sealed record MediaOriginState(
    Guid ProfileId,
    string RelationFilter,
    string TypeFilter,
    string Sort,
    Guid? AnchorAssetId,
    double AnchorOffsetDip,
    IReadOnlyList<Guid> SelectedAssetIds,
    int Page = 1,
    int PageSize = 96,
    bool IsFavoriteOnly = false)
{
    public bool Equals(MediaOriginState? other)
    {
        if (ReferenceEquals(this, other))
        {
            return true;
        }

        return other is not null
            && ProfileId == other.ProfileId
            && RelationFilter == other.RelationFilter
            && TypeFilter == other.TypeFilter
            && Sort == other.Sort
            && AnchorAssetId == other.AnchorAssetId
            && AnchorOffsetDip.Equals(other.AnchorOffsetDip)
            && SelectedAssetIds.SequenceEqual(other.SelectedAssetIds)
            && Page == other.Page
            && PageSize == other.PageSize
            && IsFavoriteOnly == other.IsFavoriteOnly;
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(ProfileId);
        hash.Add(RelationFilter);
        hash.Add(TypeFilter);
        hash.Add(Sort);
        hash.Add(AnchorAssetId);
        hash.Add(AnchorOffsetDip);
        hash.Add(Page);
        hash.Add(PageSize);
        hash.Add(IsFavoriteOnly);

        foreach (var assetId in SelectedAssetIds)
        {
            hash.Add(assetId);
        }

        return hash.ToHashCode();
    }
}

public sealed record FaceReviewRoute(Guid? ProfileId = null) : AppRoute;

public sealed record TrashRoute : AppRoute;

public sealed record LibraryHealthRoute : AppRoute;

public sealed record BreadcrumbItem(string Label, AppRoute? Target = null);

/// <summary>Single typed authority for route ownership and ancestry shown by the shell.</summary>
public static class NavigationContextResolver
{
    public static AppRoute OwningTopLevel(AppRoute route) => route switch
    {
        HomeRoute => new HomeRoute(),
        GalleryRoute or ProfileRoute => new GalleryRoute(),
        ImportRoute => new ImportRoute(),
        SettingsRoute or TrashRoute or LibraryHealthRoute or FaceReviewRoute => new SettingsRoute(),
        _ => throw new NotSupportedException($"No top-level owner is defined for {route.GetType().Name}."),
    };

    public static IReadOnlyList<BreadcrumbItem> Breadcrumb(AppRoute route) => route switch
    {
        HomeRoute => [new(SurfaceText.Get("Nav.Home", "Home"))],
        GalleryRoute => [new(SurfaceText.Get("Nav.Gallery", "Gallery"))],
        ImportRoute => [new(SurfaceText.Get("Nav.Import", "Import"))],
        SettingsRoute settings => SettingsBreadcrumb(settings),
        TrashRoute => [
            new(SurfaceText.Get("Nav.Settings", "Settings"), new SettingsRoute()),
            new(SurfaceText.Get("Settings.Trash.Title", "Trash"), new SettingsRoute(SettingsSection.Trash))],
        LibraryHealthRoute => [
            new(SurfaceText.Get("Nav.Settings", "Settings"), new SettingsRoute()),
            new(SurfaceText.Get("Breadcrumb.System", "System"), new SettingsRoute(SettingsSection.System)),
            new(SurfaceText.Get("Breadcrumb.LibraryHealth", "Library Health"), new SettingsRoute(SettingsSection.System, SettingsSubsection.LibraryHealth))],
        FaceReviewRoute => [
            new(SurfaceText.Get("Nav.Settings", "Settings"), new SettingsRoute()),
            new(SurfaceText.Get("Breadcrumb.People", "People"), new SettingsRoute(SettingsSection.People))],
        ProfileRoute profile =>
            [new(SurfaceText.Get("Nav.Gallery", "Gallery"), new GalleryRoute()), new($"{SurfaceText.Get("Breadcrumb.Profile", "Profile")} {profile.ProfileId.ToString("N")[..8]}")],
        _ => [new(route.GetType().Name)],
    };

    private static IReadOnlyList<BreadcrumbItem> SettingsBreadcrumb(SettingsRoute route)
    {
        var root = new BreadcrumbItem(SurfaceText.Get("Nav.Settings", "Settings"), new SettingsRoute());
        if (route.Section == SettingsSection.General && route.Subsection is null)
        {
            return [root];
        }

        var section = new BreadcrumbItem(
            SurfaceText.Get($"Settings.{route.Section}.Title", route.Section.ToString()),
            new SettingsRoute(route.Section));
        return route.Subsection is null
            ? [root, section]
            : [
                root,
                section,
                new BreadcrumbItem(
                    SurfaceText.Get($"Settings.{route.Subsection}.Title", SplitName(route.Subsection.Value.ToString())),
                    new SettingsRoute(route.Section, route.Subsection))];
    }

    private static string SplitName(string value) =>
        string.Concat(value.Select((character, index) => index > 0 && char.IsUpper(character) ? $" {character}" : character.ToString()));
}
