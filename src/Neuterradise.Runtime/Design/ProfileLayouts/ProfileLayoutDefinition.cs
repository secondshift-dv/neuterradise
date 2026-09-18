using Neuterradise.App.Design.CoverFrames;

namespace Neuterradise.App.Design.ProfileLayouts;

public enum ProfileLayoutPreset
{
    Cinematic,
    CinematicWide,
    Immersive,
    Editorial,
    SplitLeft,
    SplitRight,
    ProfileFocus,
    Showcase,
    GalleryHero,
    CompactHero,
    Minimal,
    Prestige
}

public enum ProfileLayoutBannerMode
{
    None,
    Contained,
    FullBleed,
    Split,
    Backdrop
}

public enum ProfileLayoutHeroHeight
{
    Low,
    Medium,
    Tall
}

public enum ProfileLayoutCoverPlacement
{
    None,
    Left,
    Right,
    InlineLeft,
    InlineCenter,
    BottomLeftOverlap,
    BottomCenterOverlap
}

public enum ProfileLayoutCoverSize
{
    Small,
    Medium,
    Large,
    ExtraLarge
}

public enum ProfileLayoutIdentityAlignment
{
    Left,
    Center,
    Right
}

public enum ProfileLayoutContentWidth
{
    Normal,
    Wide
}

public enum ProfileLayoutOverlay
{
    Soft,
    Medium,
    Strong
}

public enum ProfileLayoutBannerBlur
{
    Off,
    Subtle
}

public enum ProfileLayoutHeroFade
{
    None,
    Bottom,
    Full
}

public enum ProfileLayoutSectionDensity
{
    Compact,
    Comfortable,
    Spacious
}

public enum ProfileLayoutModuleWidth
{
    Full,
    Half,
    Third
}

public enum ProfileLayoutModuleId
{
    Overview,
    Notes,
    Related,
    Faces,
    RecentMedia,
    Media
}

public sealed record ProfileLayoutHero(
    ProfileLayoutBannerMode BannerMode,
    ProfileLayoutHeroHeight Height,
    ProfileLayoutCoverPlacement CoverPlacement,
    ProfileLayoutCoverSize CoverSize,
    CoverFrameDetailLevel CoverDetailLevel,
    ProfileLayoutIdentityAlignment IdentityAlignment,
    ProfileLayoutContentWidth ContentWidth,
    ProfileLayoutOverlay Overlay,
    ProfileLayoutBannerBlur Blur,
    ProfileLayoutHeroFade Fade);


public sealed record ProfileLayoutModule(ProfileLayoutModuleId Id, int Order, bool Visible, ProfileLayoutModuleWidth Width);
public sealed record ProfileLayoutDefinition(int SchemaVersion, string Id, string Name, ProfileLayoutHero Hero, ProfileLayoutSectionDensity SectionDensity, IReadOnlyList<ProfileLayoutModule> Modules);
