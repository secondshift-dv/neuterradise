using System.Diagnostics;
using System.Text.Json;
using System.Text.Json.Nodes;
using Neuterradise.App.Design.GalleryCards;
using Neuterradise.App.Design.ProfileLayouts;
using Neuterradise.App.Design.Themes;
using Neuterradise.App.Profiles;
using Neuterradise.App.Settings;
using Neuterradise.App.SystemServices.Database;
using Neuterradise.App.SystemServices.Database.Writes;

namespace Neuterradise.App.Presentation;

/// <summary>
/// A Profile's committed appearance as the resolver needs it (the existing durable authority).
/// <see cref="Sources"/> carries the Cover/Banner source assets so a Customization session previews and
/// commits a source change together with its framing; null means the sources are not part of this state.
/// </summary>
public sealed record ProfilePresentationState(
    Guid ProfileId,
    long RowVersion,
    string? LayoutPresetId,
    ProfileAppearanceOverrides Overrides,
    ProfileMediaSources? Sources = null)
{
    public ProfileMediaPresentation Media => ProfileMediaPresentation.From(Overrides);
}

/// <summary>The Cover and Banner source assets of a Profile (the frame timestamp lives in the overrides).</summary>
public sealed record ProfileMediaSources(Guid? CoverAssetId, Guid? BannerAssetId);

/// <summary>Where a resolution happens: which surface, Profile and item are in view.</summary>
public sealed record PresentationContext(string? Surface = null, Guid? ProfileId = null, Guid? ItemId = null, ProfilePresentationState? Profile = null)
{
    public static PresentationContext Global { get; } = new();

    public static PresentationContext ForProfile(ProfilePresentationState profile, string surface = "profile") =>
        new(surface, profile.ProfileId, null, profile);
}

public sealed record ResolutionStep(ResolutionSource Source, DefinitionRef? Definition, string? StateJson);

public sealed record ResolvedSelection(
    string Slot,
    DefinitionRef? Definition,
    string? StateJson,
    ResolutionSource Source,
    IReadOnlyList<ResolutionStep> Chain)
{
    /// <summary>True when the value at <paramref name="scope"/> comes from a parent rather than an override there.</summary>
    public bool IsInheritedAt(ScopeKind scope) => !Chain.Any(step => step.Source == ToSource(scope) && (step.Definition is not null || step.StateJson is not null));

    public static ResolutionSource ToSource(ScopeKind scope) => scope switch
    {
        ScopeKind.Global => ResolutionSource.Global,
        ScopeKind.Surface => ResolutionSource.Surface,
        ScopeKind.Profile => ResolutionSource.Profile,
        _ => ResolutionSource.Item,
    };
}

public sealed class PresentationChangedEventArgs(IReadOnlyCollection<string> slots, bool packsChanged) : EventArgs
{
    public IReadOnlyCollection<string> Slots { get; } = slots;

    public bool PacksChanged { get; } = packsChanged;

    public bool Affects(string slot) => PacksChanged || Slots.Contains(slot);
}

public sealed record PresentationApplyResult(bool Succeeded, string? Message, long? ProfileRowVersion = null);

public sealed record PackRemovalResult(bool Removed, int BindingsReset, string? Message);

/// <summary>
/// The presentation authority for one Vault: registry (built-in + user packs through the same compiler),
/// generic bindings, scope resolution with explicit inheritance, preview transactions and atomic Apply.
///
/// Profile-scoped Layout, Card, Frame, Cover and Banner stay on their existing durable authority
/// (<c>profile_appearance</c>), bridged here so existing user choices keep meaning. Only definitions a
/// legacy field cannot express (user pack layouts/cards/frames) are stored as Profile-scope bindings.
/// </summary>
public sealed class PresentationRuntime
{
    private const string LegacySeedKey = "presentation.legacySeed.v1";

    private readonly CatalogDb _catalog;
    private readonly PresentationPackStore _packs;
    private readonly PresentationBindingStore _store;
    private readonly PresentationCompiler _compiler;
    private readonly Lock _sync = new();
    private Dictionary<DefinitionRef, CompiledDefinition> _definitions = [];
    private Dictionary<string, IReadOnlyList<CompiledDefinition>> _byKind = new(StringComparer.Ordinal);
    private BindingSet _committed = BindingSet.Empty;

    public PresentationRuntime(CatalogDb catalog, PresentationPackStore packs)
    {
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _packs = packs ?? throw new ArgumentNullException(nameof(packs));
        _store = new PresentationBindingStore(catalog);
        _compiler = new PresentationCompiler(packs);
    }

    public event EventHandler<PresentationChangedEventArgs>? Changed;

    public BindingSet Committed
    {
        get { lock (_sync) return _committed; }
    }

    public IReadOnlyList<InstalledPack> Packs => _packs.Packs;

    public PresentationPackStore PackStore => _packs;

    public IPresentationAssetStore Assets => _packs;

    public int CompiledPlanCount => _compiler.CachedCount;

    /// <summary>Loads packs, compiles every definition once, loads bindings and seeds from legacy settings once.</summary>
    public async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        await Task.Run(() => _packs.LoadAll(), cancellationToken).ConfigureAwait(false);
        foreach (var pack in _packs.Packs)
        {
            try
            {
                await _store.RecordPackAsync(pack, cancellationToken).ConfigureAwait(false);
            }
            catch (Exception exception) when (CatalogDb.IsDatabaseFailure(exception))
            {
                Trace.TraceWarning("Presentation pack registry could not record {0}: {1}", pack.PackId, exception.GetType().Name);
            }
        }

        RebuildCatalog();
        var loaded = await _store.LoadAsync(cancellationToken).ConfigureAwait(false);
        lock (_sync)
        {
            _committed = loaded;
        }

        await SeedFromLegacySettingsAsync(cancellationToken).ConfigureAwait(false);
    }

    // ---------------------------------------------------------------- registry

    public IReadOnlyList<CompiledDefinition> DefinitionsOf(string kind)
    {
        lock (_sync)
        {
            return _byKind.TryGetValue(kind, out var list) ? list : [];
        }
    }

    public CompiledDefinition? Find(DefinitionRef? reference)
    {
        if (reference is not { } value)
        {
            return null;
        }

        lock (_sync)
        {
            return _definitions.TryGetValue(value, out var compiled) ? compiled : null;
        }
    }

    private void RebuildCatalog()
    {
        var definitions = new Dictionary<DefinitionRef, CompiledDefinition>();
        foreach (var pack in _packs.Packs.Where(p => p.IsUsable))
        {
            foreach (var definition in pack.Manifest.Definitions)
            {
                var compiled = _compiler.Compile(pack, definition);
                if (compiled is null)
                {
                    Trace.TraceWarning("Presentation definition {0} failed to compile and falls back.", definition.Id);
                    continue;
                }

                definitions[compiled.Ref] = compiled;
            }
        }

        var byKind = definitions.Values
            .GroupBy(d => d.Kind, StringComparer.Ordinal)
            .ToDictionary(g => g.Key, g => (IReadOnlyList<CompiledDefinition>)[.. g.OrderBy(d => d.Origin).ThenBy(d => SortIndex(d))], StringComparer.Ordinal);
        lock (_sync)
        {
            _definitions = definitions;
            _byKind = byKind;
        }
    }

    private int SortIndex(CompiledDefinition definition)
    {
        if (definition.Origin != PackOrigin.BuiltIn)
        {
            return 0;
        }

        var manifest = _packs.Packs.First(p => p.Origin == PackOrigin.BuiltIn).Manifest;
        for (var i = 0; i < manifest.Definitions.Count; i++)
        {
            if (manifest.Definitions[i].Id == definition.Ref.DefinitionId)
            {
                return i;
            }
        }

        return int.MaxValue;
    }

    // ---------------------------------------------------------------- resolution

    public ResolvedSelection Resolve(string slot, PresentationContext context, BindingSet? bindings = null)
    {
        var descriptor = PresentationSlots.Get(slot);
        var set = bindings ?? Committed;
        var chain = new List<ResolutionStep> { new(ResolutionSource.BuiltInDefault, descriptor.Default, null) };

        void Add(ResolutionSource source, PresentationBinding? binding)
        {
            if (binding is not null)
            {
                chain.Add(new ResolutionStep(source, binding.Definition, binding.StateJson));
            }
        }

        if (descriptor.AllowsScope(ScopeKind.Global))
        {
            Add(ResolutionSource.Global, set.Get(ScopeKind.Global, ScopeIds.Global, slot));
        }

        if (descriptor.AllowsScope(ScopeKind.Surface) && context.Surface is { } surface)
        {
            Add(ResolutionSource.Surface, set.Get(ScopeKind.Surface, surface, slot));
        }

        if (descriptor.AllowsScope(ScopeKind.Profile) && context.ProfileId is { } profileId)
        {
            var binding = set.Get(ScopeKind.Profile, ScopeIds.Of(profileId), slot);
            if (binding is not null)
            {
                Add(ResolutionSource.Profile, binding);
            }
            else if (BridgedProfileDefinition(slot, context.Profile) is { } bridged)
            {
                chain.Add(new ResolutionStep(ResolutionSource.Profile, bridged, null));
            }
        }

        if (descriptor.AllowsScope(ScopeKind.Item) && context.ItemId is { } itemId)
        {
            Add(ResolutionSource.Item, set.Get(ScopeKind.Item, ScopeIds.Of(itemId), slot));
        }

        // Definition: the most specific step whose definition is usable and of the slot's kind.
        DefinitionRef? definition = null;
        var source = ResolutionSource.BuiltInDefault;
        for (var i = chain.Count - 1; i >= 0; i--)
        {
            if (chain[i].Definition is { } candidate && IsUsable(candidate, descriptor.Kind))
            {
                definition = candidate;
                source = chain[i].Source;
                break;
            }
        }

        if (descriptor.Kind is not null && definition is null)
        {
            definition = DefinitionsOf(descriptor.Kind).FirstOrDefault()?.Ref;
        }

        // State: shallow merge from least to most specific, so a delta only overrides what it names.
        string? state = null;
        foreach (var step in chain.Where(s => s.StateJson is not null))
        {
            state = MergeState(state, step.StateJson!);
            if (descriptor.IsStateOnly)
            {
                source = step.Source;
            }
        }

        return new ResolvedSelection(slot, definition, state, source, chain);
    }

    /// <summary>Resolves a slot to a compiled definition. Never returns null for a definition slot.</summary>
    public CompiledDefinition ResolveCompiled(string slot, PresentationContext context, BindingSet? bindings = null)
    {
        var descriptor = PresentationSlots.Get(slot);
        var resolved = Resolve(slot, context, bindings);
        return Find(resolved.Definition)
            ?? Find(descriptor.Default)
            ?? DefinitionsOf(descriptor.Kind ?? string.Empty).FirstOrDefault()
            ?? throw new InvalidOperationException($"No usable definition exists for slot '{slot}'.");
    }

    public T ResolvePlan<T>(string slot, PresentationContext context, BindingSet? bindings = null) where T : class =>
        ResolveCompiled(slot, context, bindings).PlanAs<T>();

    /// <summary>The Cover/Banner presentation a surface shows: the Profile canonical state plus that surface's delta.</summary>
    public ProfileMediaPresentation ResolveMedia(ProfilePresentationState profile, string? surfaceCoverSlot, string? surfaceBannerSlot, BindingSet? bindings = null)
    {
        var media = profile.Media;
        var set = bindings ?? Committed;
        var scope = ScopeIds.Of(profile.ProfileId);
        var cover = surfaceCoverSlot is null ? media.Cover : media.Cover.ApplyDelta(set.Get(ScopeKind.Profile, scope, surfaceCoverSlot)?.StateJson);
        var banner = surfaceBannerSlot is null ? media.Banner : media.Banner.ApplyDelta(set.Get(ScopeKind.Profile, scope, surfaceBannerSlot)?.StateJson);
        return media with { Cover = cover, Banner = banner };
    }

    private bool IsUsable(DefinitionRef reference, string? kind) =>
        Find(reference) is { } compiled && (kind is null || compiled.Kind == kind);

    private static string MergeState(string? baseJson, string overlayJson)
    {
        if (baseJson is null)
        {
            return overlayJson;
        }

        try
        {
            var target = JsonNode.Parse(baseJson) as JsonObject ?? [];
            if (JsonNode.Parse(overlayJson) is JsonObject overlay)
            {
                foreach (var (key, value) in overlay)
                {
                    target[key] = value?.DeepClone();
                }
            }

            return target.ToJsonString();
        }
        catch (JsonException)
        {
            return overlayJson;
        }
    }

    // ---------------------------------------------------------------- Profile bridge

    public static DefinitionRef? BridgedProfileDefinition(string slot, ProfilePresentationState? profile)
    {
        if (profile is null)
        {
            return null;
        }

        return slot switch
        {
            PresentationSlots.ProfileLayout when profile.LayoutPresetId is { } preset => DefinitionRef.BuiltIn($"builtin.neuterradise.profile.{preset}"),
            PresentationSlots.GalleryCard when profile.Overrides.GalleryCardVariantId is { } variant => CardRefForVariant(variant),
            PresentationSlots.ProfileFrame when profile.Overrides.CoverFrameId is { } frame => DefinitionRef.BuiltIn($"builtin.neuterradise.frame.{frame}"),
            _ => null,
        };
    }

    public static DefinitionRef CardRefForVariant(string variantId) =>
        DefinitionRef.BuiltIn(variantId == "hero-card" ? "builtin.neuterradise.card.hero" : $"builtin.neuterradise.card.{variantId}");

    /// <summary>The legacy field value a built-in definition maps to, or null when only a binding can express it.</summary>
    public static string? LegacyValueFor(string slot, DefinitionRef definition)
    {
        if (definition.PackId != PresentationContract.BuiltInPackId)
        {
            return null;
        }

        var id = definition.DefinitionId;
        return slot switch
        {
            PresentationSlots.ProfileLayout when id.StartsWith("builtin.neuterradise.profile.", StringComparison.Ordinal) => id["builtin.neuterradise.profile.".Length..],
            PresentationSlots.GalleryCard when id == "builtin.neuterradise.card.hero" => "hero-card",
            PresentationSlots.GalleryCard when id.StartsWith("builtin.neuterradise.card.", StringComparison.Ordinal) => id["builtin.neuterradise.card.".Length..],
            PresentationSlots.ProfileFrame when id.StartsWith("builtin.neuterradise.frame.", StringComparison.Ordinal) => id["builtin.neuterradise.frame.".Length..],
            PresentationSlots.Theme when id.StartsWith("builtin.neuterradise.theme.", StringComparison.Ordinal) => id["builtin.neuterradise.theme.".Length..],
            _ => null,
        };
    }

    public static bool IsBridgedProfileSlot(string slot) =>
        slot is PresentationSlots.ProfileLayout or PresentationSlots.GalleryCard or PresentationSlots.ProfileFrame;

    public async Task<ProfilePresentationState?> LoadProfileStateAsync(Guid profileId, CancellationToken cancellationToken = default)
    {
        var detail = await _catalog.ProfileReads.GetDetailAsync(profileId, cancellationToken).ConfigureAwait(false);
        return detail is null
            ? null
            : new ProfilePresentationState(
                profileId,
                detail.RowVersion,
                detail.LayoutPresetId,
                ProfileAppearanceRules.NormalizeBannerSourceKind(
                    ProfileAppearanceOverrides.Parse(detail.AppearanceOverridesJson),
                    detail.BannerMediaType),
                new ProfileMediaSources(detail.CoverAssetId, detail.BannerAssetId));
    }

    // ---------------------------------------------------------------- preview / apply

    public PreviewSession BeginPreview(PresentationContext context) => new(this, context, Committed);

    internal async Task<PresentationApplyResult> CommitAsync(PreviewSession session, CancellationToken cancellationToken)
    {
        var changes = session.Working.DiffFrom(session.Baseline);
        var changedSlots = changes.Select(c => c.Slot).ToHashSet(StringComparer.Ordinal);
        long? newRowVersion = null;
        ProfilePresentationState? previous = session.OriginalProfile;
        ApplyProfilePresentationRequest? profileRequest = null;
        ProfilePresentationWriteResult? profileWrite = null;
        var profileOperations = new ProfileAppearanceOperations(_catalog);
        try
        {
            await using var lease = await _catalog.WriteCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
            await using var connection = await _catalog.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = CatalogTransaction.Begin(connection, _catalog.WriteCoordinator);

            if (session.ProfileWorking is { } profile && previous is not null && session.ProfileChanged)
            {
                profileRequest = new ApplyProfilePresentationRequest(
                    profile.ProfileId,
                    previous.RowVersion,
                    ApplyLayout: profile.LayoutPresetId != previous.LayoutPresetId,
                    profile.LayoutPresetId,
                    profile.Overrides,
                    SourceChangeBetween(previous, profile));
                profileWrite = await profileOperations.ApplyPresentationInTransactionAsync(
                    profileRequest, transaction, cancellationToken).ConfigureAwait(false);
                var result = profileWrite.Result;
                if (!result.IsSuccess || result.Value is null)
                {
                    return new PresentationApplyResult(false, result.Error?.UserMessage ?? "The Profile appearance could not be saved.");
                }

                newRowVersion = result.Value.RowVersion;
                changedSlots.UnionWith([PresentationSlots.ProfileCover, PresentationSlots.ProfileBanner, PresentationSlots.ProfileLayout, PresentationSlots.GalleryCard, PresentationSlots.ProfileFrame]);
            }

            await _store.ApplyInTransactionAsync(transaction, changes, cancellationToken).ConfigureAwait(false);
            await MirrorLegacyInTransactionAsync(transaction, changes, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
            lock (_sync)
            {
                _committed = session.Working;
            }
        }
        catch (Exception exception) when (CatalogDb.IsDatabaseFailure(exception) || exception is ArgumentException or JsonException or InvalidOperationException)
        {
            return new PresentationApplyResult(false, "Your changes could not be saved: " + exception.Message);
        }

        if (profileRequest is not null && profileWrite is not null)
        {
            await profileOperations.CompletePresentationApplyAsync(
                profileWrite, profileRequest.ProfileId, cancellationToken).ConfigureAwait(false);
        }

        Changed?.Invoke(this, new PresentationChangedEventArgs(changedSlots, packsChanged: false));
        return new PresentationApplyResult(true, null, newRowVersion);
    }

    /// <summary>The Cover/Banner source edits that turn <paramref name="from"/> into <paramref name="to"/>.</summary>
    internal static ProfileMediaSourceChange? SourceChangeBetween(ProfilePresentationState from, ProfilePresentationState to)
    {
        if (from.Sources is not { } before || to.Sources is not { } after)
        {
            return null;
        }

        var coverChanged = before.CoverAssetId != after.CoverAssetId
            || from.Overrides.CoverVideoTimestampMilliseconds != to.Overrides.CoverVideoTimestampMilliseconds;
        var bannerChanged = before.BannerAssetId != after.BannerAssetId
            || from.Overrides.BannerSourceKind != to.Overrides.BannerSourceKind
            || from.Overrides.BannerVideoFrameTimestampMilliseconds != to.Overrides.BannerVideoFrameTimestampMilliseconds;
        return coverChanged || bannerChanged
            ? new ProfileMediaSourceChange(
                coverChanged,
                after.CoverAssetId,
                bannerChanged,
                after.BannerAssetId,
                to.Overrides.ResolvedBannerSourceKind,
                to.Overrides.BannerVideoFrameTimestampMilliseconds)
            : null;
    }

    private async Task MirrorLegacyInTransactionAsync(
        CatalogTransaction transaction,
        IReadOnlyList<BindingChange> changes,
        CancellationToken cancellationToken)
    {
        foreach (var change in changes.Where(c => c.ScopeKind == ScopeKind.Global))
        {
            string? key = null;
            string? valueJson = null;
            ActivityEntryPersistence? activity = null;
            switch (change.Slot)
            {
                case PresentationSlots.Theme:
                    var themeId = change.Remove ? ThemeLoader.FallbackThemeId : change.Definition is { } theme ? LegacyValueFor(change.Slot, theme) : null;
                    if (themeId is null) continue;
                    key = SettingsOperations.ThemeKey;
                    valueJson = JsonSerializer.Serialize(themeId);
                    activity = new ActivityEntryPersistence(
                        Guid.NewGuid(), ActivityEventType.ThemeChanged, null, null, null, null,
                        JsonSerializer.Serialize(new { themeId }), TimeProvider.System.GetUtcNow());
                    break;
                case PresentationSlots.ProfileLayout:
                    var preset = change.Remove ? ProfileLayoutResolver.FallbackPresetId : change.Definition is { } layout ? LegacyValueFor(change.Slot, layout) : null;
                    if (preset is null) continue;
                    key = SettingsOperations.DefaultProfileLayoutPresetKey;
                    valueJson = JsonSerializer.Serialize(preset);
                    break;
                case PresentationSlots.GalleryCard:
                case PresentationSlots.GalleryDensity:
                    var stored = await ReadSettingInTransactionAsync(
                        transaction, SettingsOperations.GalleryPresentationKey, cancellationToken).ConfigureAwait(false);
                    var current = string.IsNullOrWhiteSpace(stored)
                        ? GalleryCardCatalog.Default
                        : GalleryCardCatalog.ParsePreference(stored, "Settings").Preference;
                    var next = change.Slot == PresentationSlots.GalleryCard
                        ? current with { CardVariantId = change.Definition is { } card ? LegacyValueFor(change.Slot, card) ?? current.CardVariantId : GalleryCardCatalog.FallbackVariantId }
                        : GalleryDensityState.Parse(change.StateJson).WriteTo(current);
                    key = SettingsOperations.GalleryPresentationKey;
                    valueJson = GalleryCardCatalog.WritePreference(next);
                    break;
            }

            if (key is not null && valueJson is not null)
            {
                await SettingsWrites.SetSettingInTransactionAsync(
                    transaction, key, valueJson, activity, TimeProvider.System.GetUtcNow(), cancellationToken).ConfigureAwait(false);
            }
        }
    }

    private static async Task<string?> ReadSettingInTransactionAsync(
        CatalogTransaction transaction,
        string key,
        CancellationToken cancellationToken)
    {
        await using var command = transaction.CreateCommand("SELECT value_json FROM settings WHERE key = $key;");
        command.Parameters.AddWithValue("$key", key);
        return await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false) as string;
    }

    private async Task SeedFromLegacySettingsAsync(CancellationToken cancellationToken)
    {
        try
        {
            var seeded = await _catalog.SettingsReads.GetSettingValueAsync(LegacySeedKey, cancellationToken).ConfigureAwait(false);
            if (seeded is not null)
            {
                return;
            }

            var settings = new SettingsOperations(_catalog);
            var changes = new List<BindingChange>();
            var committed = Committed;

            var theme = await settings.GetThemeAsync(cancellationToken).ConfigureAwait(false);
            if (theme != ThemeLoader.FallbackThemeId && committed.Get(ScopeKind.Global, ScopeIds.Global, PresentationSlots.Theme) is null
                && Find(DefinitionRef.BuiltIn($"builtin.neuterradise.theme.{theme}")) is { } themeDefinition)
            {
                changes.Add(new BindingChange(ScopeKind.Global, ScopeIds.Global, PresentationSlots.Theme, themeDefinition.Ref, null));
            }

            var gallery = await settings.GetGalleryPresentationPreferencesAsync(cancellationToken).ConfigureAwait(false);
            if (gallery.CardVariantId != GalleryCardCatalog.FallbackVariantId && committed.Get(ScopeKind.Global, ScopeIds.Global, PresentationSlots.GalleryCard) is null)
            {
                changes.Add(new BindingChange(ScopeKind.Global, ScopeIds.Global, PresentationSlots.GalleryCard, CardRefForVariant(gallery.CardVariantId), null));
            }

            if (gallery != GalleryCardCatalog.Default && committed.Get(ScopeKind.Global, ScopeIds.Global, PresentationSlots.GalleryDensity) is null)
            {
                changes.Add(new BindingChange(ScopeKind.Global, ScopeIds.Global, PresentationSlots.GalleryDensity, null, GalleryDensityState.From(gallery).ToJson()));
            }

            var layout = await settings.GetDefaultProfileLayoutAsync(cancellationToken).ConfigureAwait(false);
            if (layout != ProfileLayoutResolver.FallbackPresetId && committed.Get(ScopeKind.Global, ScopeIds.Global, PresentationSlots.ProfileLayout) is null)
            {
                changes.Add(new BindingChange(ScopeKind.Global, ScopeIds.Global, PresentationSlots.ProfileLayout, DefinitionRef.BuiltIn($"builtin.neuterradise.profile.{layout}"), null));
            }

            var result = await _store.ApplyAsync(changes, cancellationToken).ConfigureAwait(false);
            lock (_sync)
            {
                _committed = result;
            }

            await _catalog.SettingsWrites.SetSettingAsync(LegacySeedKey, "true", cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (CatalogDb.IsDatabaseFailure(exception) || exception is InvalidOperationException or ArgumentException)
        {
            Trace.TraceWarning("Presentation legacy seeding skipped: {0}", exception.GetType().Name);
        }
    }

    // ---------------------------------------------------------------- packs

    public async Task<PackInstallResult> InstallPackAsync(string sourcePath, bool replaceExisting, CancellationToken cancellationToken = default)
    {
        var result = await _packs.InstallAsync(sourcePath, replaceExisting, cancellationToken).ConfigureAwait(false);
        if (result.Succeeded && result.Pack is { } pack)
        {
            _compiler.Invalidate(pack.PackId);
            await _store.RecordPackAsync(pack, cancellationToken).ConfigureAwait(false);
            RebuildCatalog();
            Changed?.Invoke(this, new PresentationChangedEventArgs([], packsChanged: true));
        }

        return result;
    }

    public Task ExportPackAsync(string packId, string destinationFile, CancellationToken cancellationToken = default) =>
        _packs.ExportAsync(packId, destinationFile, cancellationToken);

    public int CountUsages(string packId) => Committed.UsingPack(packId).Count();

    /// <summary>
    /// Removes a user pack through an atomic same-volume rename followed by one catalog transaction.
    /// If staging or the catalog write fails, the pack is restored before any failure is reported.
    /// </summary>
    public async Task<PackRemovalResult> RemovePackAsync(string packId, CancellationToken cancellationToken = default)
    {
        if (!_packs.TryGetPack(packId, out var pack) || pack is null || pack.Origin != PackOrigin.User)
        {
            return new PackRemovalResult(false, 0, "Only installed user packs can be removed.");
        }

        var usages = Committed.UsingPack(packId).Select(b => BindingChange.Reset(b.ScopeKind, b.ScopeId, b.Slot)).ToList();
        var stage = await _packs.StageRemovalAsync(packId, cancellationToken).ConfigureAwait(false);
        if (stage is null)
        {
            return new PackRemovalResult(false, 0, "The pack folder could not be staged for removal.");
        }

        try
        {
            await using var lease = await _catalog.WriteCoordinator.EnterAsync(cancellationToken).ConfigureAwait(false);
            await using var connection = await _catalog.OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
            await using var transaction = CatalogTransaction.Begin(connection, _catalog.WriteCoordinator);
            await _store.ApplyInTransactionAsync(transaction, usages, cancellationToken).ConfigureAwait(false);
            await PresentationBindingStore.ForgetPackInTransactionAsync(transaction, packId, cancellationToken).ConfigureAwait(false);
            await transaction.CommitAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (Exception exception) when (exception is not StackOverflowException and not OutOfMemoryException)
        {
            var restored = _packs.RestoreRemoval(stage);
            return new PackRemovalResult(
                false,
                0,
                restored
                    ? "The pack could not be removed; its folder and bindings were preserved."
                    : "The pack removal failed and its folder could not be restored; presentation consistency requires repair. " + exception.Message);
        }

        lock (_sync)
        {
            _committed = _committed.Apply(usages);
        }
        _packs.CompleteRemoval(stage);
        _compiler.Invalidate(packId);
        RebuildCatalog();
        Changed?.Invoke(this, new PresentationChangedEventArgs(usages.Select(u => u.Slot).ToHashSet(), packsChanged: true));
        return new PackRemovalResult(true, usages.Count, null);
    }
}

public static class ScopeIds
{
    public const string Global = "*";

    public static string Of(Guid id) => id.ToString("D");
}

/// <summary>
/// An in-memory preview transaction (document 01 §21.3): committed state → preview copy → live render.
/// Nothing reaches SQLite until <see cref="ApplyAsync"/>; <see cref="Cancel"/> simply drops the copy.
/// </summary>
public sealed class PreviewSession
{
    private readonly PresentationRuntime _runtime;

    internal PreviewSession(PresentationRuntime runtime, PresentationContext context, BindingSet committed)
    {
        _runtime = runtime;
        Context = context;
        Baseline = committed;
        Working = committed;
        OriginalProfile = context.Profile;
        ProfileWorking = context.Profile;
    }

    public event EventHandler<string>? Changed;

    public PresentationContext Context { get; private set; }

    public BindingSet Baseline { get; }

    public BindingSet Working { get; private set; }

    public ProfilePresentationState? OriginalProfile { get; private set; }

    public ProfilePresentationState? ProfileWorking { get; private set; }

    public bool ProfileChanged => ProfileWorking is not null && OriginalProfile is not null
        && (ProfileWorking.LayoutPresetId != OriginalProfile.LayoutPresetId
            || ProfileWorking.Overrides != OriginalProfile.Overrides
            || ProfileWorking.Sources != OriginalProfile.Sources);

    public bool HasChanges => ProfileChanged || Working.DiffFrom(Baseline).Count > 0;

    public bool IsClosed { get; private set; }

    /// <summary>The context with the in-preview Profile state, for live rendering.</summary>
    public PresentationContext LiveContext => Context with { Profile = ProfileWorking };

    public ResolvedSelection Resolve(string slot) => _runtime.Resolve(slot, ContextFor(slot), Working);

    public CompiledDefinition ResolveCompiled(string slot) => _runtime.ResolveCompiled(slot, ContextFor(slot), Working);

    /// <summary>
    /// The live context for one slot. A surface-scoped slot always resolves against its own surface, so the
    /// central Customization Center shows and edits "Only Gallery" / "Only Home" values without being opened
    /// from that page.
    /// </summary>
    public PresentationContext ContextFor(string slot) =>
        PresentationSlots.Get(slot).Surface is { } surface ? LiveContext with { Surface = surface } : LiveContext;

    public ProfileMediaPresentation? ResolveMedia(string? coverSlot, string? bannerSlot) =>
        ProfileWorking is null ? null : _runtime.ResolveMedia(ProfileWorking, coverSlot, bannerSlot, Working);

    public string ScopeIdFor(ScopeKind scope, string slot) => scope switch
    {
        ScopeKind.Global => ScopeIds.Global,
        ScopeKind.Surface => PresentationSlots.Get(slot).Surface ?? Context.Surface ?? throw new InvalidOperationException("This slot has no surface."),
        ScopeKind.Profile => ScopeIds.Of(Context.ProfileId ?? throw new InvalidOperationException("This preview has no Profile.")),
        _ => ScopeIds.Of(Context.ItemId ?? throw new InvalidOperationException("This preview has no item.")),
    };

    /// <summary>Selects a definition (and optional state) for a slot at a scope.</summary>
    public void Select(string slot, ScopeKind scope, DefinitionRef? definition, string? stateJson = null)
    {
        EnsureOpen();
        if (scope == ScopeKind.Profile && PresentationRuntime.IsBridgedProfileSlot(slot) && ProfileWorking is { } profile && definition is { } reference
            && PresentationRuntime.LegacyValueFor(slot, reference) is { } legacy)
        {
            // Built-in choices keep living on the Profile's existing durable appearance record.
            ProfileWorking = slot switch
            {
                PresentationSlots.ProfileLayout => profile with { LayoutPresetId = legacy },
                PresentationSlots.GalleryCard => profile with { Overrides = profile.Overrides with { GalleryCardVariantId = legacy } },
                _ => profile with { Overrides = profile.Overrides with { CoverFrameId = legacy } },
            };
            Working = Working.Apply(BindingChange.Reset(scope, ScopeIdFor(scope, slot), slot));
        }
        else
        {
            Working = Working.Apply(new BindingChange(scope, ScopeIdFor(scope, slot), slot, definition, stateJson));
        }

        Changed?.Invoke(this, slot);
    }

    /// <summary>Stores a state-only value (e.g. Gallery density, a surface Cover delta).</summary>
    public void SetState(string slot, ScopeKind scope, string? stateJson)
    {
        EnsureOpen();
        Working = stateJson is null
            ? Working.Apply(BindingChange.Reset(scope, ScopeIdFor(scope, slot), slot))
            : Working.Apply(new BindingChange(scope, ScopeIdFor(scope, slot), slot, null, stateJson));
        Changed?.Invoke(this, slot);
    }

    /// <summary>Removes the override at this scope so the value is inherited from the parent again.</summary>
    public void ResetToParent(string slot, ScopeKind scope)
    {
        EnsureOpen();
        Working = Working.Apply(BindingChange.Reset(scope, ScopeIdFor(scope, slot), slot));
        if (scope == ScopeKind.Profile && ProfileWorking is { } profile)
        {
            ProfileWorking = slot switch
            {
                PresentationSlots.ProfileLayout => profile with { LayoutPresetId = null },
                PresentationSlots.GalleryCard => profile with { Overrides = profile.Overrides with { GalleryCardVariantId = null } },
                PresentationSlots.ProfileFrame => profile with { Overrides = profile.Overrides with { CoverFrameId = null } },
                // Cover/Banner are Profile-owned with no parent scope: resetting discards this session's edits,
                // restoring the committed source together with its framing (and, for the Banner, playback).
                PresentationSlots.ProfileCover when OriginalProfile is { } original => RestoreCover(profile, original),
                PresentationSlots.ProfileBanner when OriginalProfile is { } original => RestoreBanner(profile, original),
                _ => profile,
            };
        }

        Changed?.Invoke(this, slot);
    }

    /// <summary>Clears every narrower override in this context so the Global selection applies.</summary>
    public void UseGlobal(string slot)
    {
        foreach (var scope in new[] { ScopeKind.Item, ScopeKind.Profile, ScopeKind.Surface })
        {
            var descriptor = PresentationSlots.Get(slot);
            var hasScope = scope switch
            {
                ScopeKind.Surface => descriptor.Surface is not null || Context.Surface is not null,
                ScopeKind.Profile => Context.ProfileId is not null,
                _ => Context.ItemId is not null,
            };
            if (descriptor.AllowsScope(scope) && hasScope)
            {
                ResetToParent(slot, scope);
            }
        }
    }

    /// <summary>
    /// Previews a new Cover source (image, or a video frame at <paramref name="frameTimestampMilliseconds"/>).
    /// Nothing is written until Apply, where the source commits in the same transaction as its framing.
    /// </summary>
    public void SelectCoverSource(Guid? assetId, bool isVideoFrame, long? frameTimestampMilliseconds)
    {
        EnsureOpen();
        var profile = ProfileWorking ?? throw new InvalidOperationException("This preview is not bound to a Profile.");
        var sources = profile.Sources ?? new ProfileMediaSources(null, null);
        ProfileWorking = profile with
        {
            Sources = sources with { CoverAssetId = assetId },
            Overrides = profile.Overrides with
            {
                CoverSourceKind = assetId is null ? null : (isVideoFrame ? CoverVisualSourceKind.VideoFrame : CoverVisualSourceKind.Image).ToString(),
                CoverVideoTimestampMilliseconds = assetId is not null && isVideoFrame ? frameTimestampMilliseconds : null,
            },
        };
        Changed?.Invoke(this, PresentationSlots.ProfileCover);
    }

    /// <summary>Previews a new Banner source. Committed with its framing and playback on Apply.</summary>
    public void SelectBannerSource(
        Guid? assetId,
        BannerVisualSourceKind sourceKind = BannerVisualSourceKind.VideoClip,
        long? frameTimestampMilliseconds = null,
        double? startPointSeconds = null,
        double? durationSeconds = null)
    {
        EnsureOpen();
        var profile = ProfileWorking ?? throw new InvalidOperationException("This preview is not bound to a Profile.");
        var sources = profile.Sources ?? new ProfileMediaSources(null, null);
        ProfileWorking = profile with
        {
            Sources = sources with { BannerAssetId = assetId },
            Overrides = profile.Overrides with
            {
                BannerSourceKind = assetId is null ? null : sourceKind.ToString(),
                BannerVideoFrameTimestampMilliseconds = assetId is not null && sourceKind == BannerVisualSourceKind.VideoFrame
                    ? frameTimestampMilliseconds
                    : null,
                BannerStartPointSeconds = assetId is not null && sourceKind == BannerVisualSourceKind.VideoClip
                    ? startPointSeconds ?? profile.Overrides.BannerStartPointSeconds
                    : ProfileAppearanceOverrides.Default.BannerStartPointSeconds,
                BannerDurationSeconds = assetId is not null && sourceKind == BannerVisualSourceKind.VideoClip
                    ? durationSeconds ?? profile.Overrides.BannerDurationSeconds
                    : ProfileAppearanceOverrides.Default.BannerDurationSeconds,
            },
        };
        Changed?.Invoke(this, PresentationSlots.ProfileBanner);
    }

    private static ProfilePresentationState RestoreCover(ProfilePresentationState working, ProfilePresentationState original)
    {
        var o = original.Overrides;
        return working with
        {
            Sources = (working.Sources ?? original.Sources) is { } sources ? sources with { CoverAssetId = original.Sources?.CoverAssetId } : null,
            Overrides = working.Overrides with
            {
                CoverSourceKind = o.CoverSourceKind,
                CoverVideoTimestampMilliseconds = o.CoverVideoTimestampMilliseconds,
                CoverFit = o.CoverFit,
                CropX = o.CropX,
                CropY = o.CropY,
                Zoom = o.Zoom,
                CoverOffsetX = o.CoverOffsetX,
                CoverOffsetY = o.CoverOffsetY,
                CoverRotation = o.CoverRotation,
            },
        };
    }

    private static ProfilePresentationState RestoreBanner(ProfilePresentationState working, ProfilePresentationState original)
    {
        var o = original.Overrides;
        return working with
        {
            Sources = (working.Sources ?? original.Sources) is { } sources ? sources with { BannerAssetId = original.Sources?.BannerAssetId } : null,
            Overrides = working.Overrides with
            {
                BannerSourceKind = o.BannerSourceKind,
                BannerVideoFrameTimestampMilliseconds = o.BannerVideoFrameTimestampMilliseconds,
                BannerFit = o.BannerFit,
                BannerFocusX = o.BannerFocusX,
                BannerFocusY = o.BannerFocusY,
                BannerZoom = o.BannerZoom,
                BannerOffsetX = o.BannerOffsetX,
                BannerOffsetY = o.BannerOffsetY,
                BannerRotation = o.BannerRotation,
                BannerStartPointSeconds = o.BannerStartPointSeconds,
                BannerDurationSeconds = o.BannerDurationSeconds,
                BannerLoop = o.BannerLoop,
                BannerLoopMode = o.BannerLoopMode,
                BannerMute = o.BannerMute,
                BannerPlaybackRate = o.BannerPlaybackRate,
                BannerReducedMotion = o.BannerReducedMotion,
            },
        };
    }

    /// <summary>Live Profile edit (Cover drag/zoom, Banner timeline, frame tint...). In memory only.</summary>
    public void EditProfile(Func<ProfileAppearanceOverrides, ProfileAppearanceOverrides> edit)
    {
        EnsureOpen();
        if (ProfileWorking is null)
        {
            throw new InvalidOperationException("This preview is not bound to a Profile.");
        }

        ProfileWorking = ProfileWorking with { Overrides = edit(ProfileWorking.Overrides) };
        Changed?.Invoke(this, PresentationSlots.ProfileCover);
    }

    /// <summary>Re-targets an open preview after the Profile's durable row changed underneath it.</summary>
    public void RebaseProfile(ProfilePresentationState committed)
    {
        OriginalProfile = committed;
        if (!ProfileChanged)
        {
            ProfileWorking = committed;
        }
        else if (ProfileWorking is not null)
        {
            ProfileWorking = ProfileWorking with { RowVersion = committed.RowVersion };
        }

        Context = Context with { Profile = committed };
    }

    public async Task<PresentationApplyResult> ApplyAsync(CancellationToken cancellationToken = default)
    {
        EnsureOpen();
        var result = await _runtime.CommitAsync(this, cancellationToken).ConfigureAwait(false);
        if (result.Succeeded)
        {
            IsClosed = true;
        }

        return result;
    }

    public void Cancel() => IsClosed = true;

    private void EnsureOpen()
    {
        if (IsClosed)
        {
            throw new InvalidOperationException("This preview has already been applied or cancelled.");
        }
    }
}

/// <summary>Gallery density/details state (slot <see cref="PresentationSlots.GalleryDensity"/>).</summary>
public sealed record GalleryDensityState(
    GalleryCardSize CardSize,
    GalleryInformationDensity InformationDensity,
    bool ShowTags,
    bool ShowCategory,
    bool ShowRating,
    bool ShowTier,
    bool ShowMediaCount,
    bool ShowRelated,
    BannerMotionMode BannerMotion)
{
    public static GalleryDensityState Default { get; } = From(GalleryCardCatalog.Default);

    public static GalleryDensityState From(GalleryPresentationPreference preference) => new(
        preference.CardSize, preference.InformationDensity, preference.ShowTags, preference.ShowCategory,
        preference.ShowRating, preference.ShowTier, preference.ShowMediaCount, preference.ShowRelatedIndicator, preference.BannerMotion);

    public GalleryPresentationPreference WriteTo(GalleryPresentationPreference preference) => preference with
    {
        CardSize = CardSize,
        InformationDensity = InformationDensity,
        ShowTags = ShowTags,
        ShowCategory = ShowCategory,
        ShowRating = ShowRating,
        ShowTier = ShowTier,
        ShowMediaCount = ShowMediaCount,
        ShowRelatedIndicator = ShowRelated,
        BannerMotion = BannerMotion,
    };

    public double CardScale => CardSize switch
    {
        GalleryCardSize.Small => 0.8,
        GalleryCardSize.Large => 1.25,
        _ => 1.0,
    };

    public string ToJson() => JsonSerializer.Serialize(new
    {
        cardSize = CardSize.ToString(),
        informationDensity = InformationDensity.ToString(),
        showTags = ShowTags,
        showCategory = ShowCategory,
        showRating = ShowRating,
        showTier = ShowTier,
        showMediaCount = ShowMediaCount,
        showRelated = ShowRelated,
        bannerMotion = BannerMotion.ToString(),
    });

    public static GalleryDensityState Parse(string? json)
    {
        if (string.IsNullOrWhiteSpace(json))
        {
            return Default;
        }

        try
        {
            using var document = JsonDocument.Parse(json);
            var root = document.RootElement;
            T E<T>(string name, T fallback) where T : struct, Enum =>
                root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String && Enum.TryParse<T>(v.GetString(), out var parsed) ? parsed : fallback;
            bool B(string name, bool fallback) =>
                root.TryGetProperty(name, out var v) && v.ValueKind is JsonValueKind.True or JsonValueKind.False ? v.GetBoolean() : fallback;
            var d = Default;
            return new GalleryDensityState(
                E("cardSize", d.CardSize), E("informationDensity", d.InformationDensity),
                B("showTags", d.ShowTags), B("showCategory", d.ShowCategory), B("showRating", d.ShowRating),
                B("showTier", d.ShowTier), B("showMediaCount", d.ShowMediaCount), B("showRelated", d.ShowRelated),
                E("bannerMotion", d.BannerMotion));
        }
        catch (JsonException)
        {
            return Default;
        }
    }
}
