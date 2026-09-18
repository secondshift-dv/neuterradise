using System.Globalization;
using System.IO;
using Microsoft.Data.Sqlite;
using Neuterradise.App.Media;
using Neuterradise.App.Media.Image;
using Neuterradise.App.Media.Model;
using Neuterradise.App.Media.Video;
using Neuterradise.App.Profiles;
using Neuterradise.App.SystemServices.Storage;
using Neuterradise.App.SystemServices.TimeAndIds;

namespace Neuterradise.App.SystemServices.Database.Reads;

public sealed class MediaReads
{
    private readonly CatalogConnectionFactory _connectionFactory;
    private readonly VaultPaths? _vaultPaths;

    public MediaReads(CatalogDb catalog)
    {
        ArgumentNullException.ThrowIfNull(catalog);
        _connectionFactory = catalog.ConnectionFactory;
        _vaultPaths = catalog.Paths;
    }

    public MediaReads(CatalogConnectionFactory connectionFactory, VaultPaths? vaultPaths = null)
    {
        _connectionFactory = connectionFactory ?? throw new ArgumentNullException(nameof(connectionFactory));
        _vaultPaths = vaultPaths;
    }

    public async Task<MediaDetailReadModel?> GetMediaDetailAsync(
        Guid assetId,
        CancellationToken cancellationToken = default)
    {
        if (assetId == Guid.Empty)
        {
            return null;
        }

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        // I07.1: PHASE 1 — Asset base row + persisted asset_metadata in one query.
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT a.asset_id, a.media_type,
                   coalesce(a.current_managed_file_name, a.original_file_name, 'unknown') AS file_name,
                   coalesce(a.original_file_name, 'unknown') AS original_name,
                   coalesce(a.current_managed_relative_path, '') AS current_location,
                   a.target_managed_relative_path, a.path_state,
                   coalesce(a.sha256, '') AS file_fingerprint,
                   coalesce(a.byte_length, 0) AS byte_length,
                   a.state,
                   coalesce(a.added_to_library_at_ms, a.created_at_ms) AS added_at_ms,
                   a.source_display_name,
                   a.source_kind,
                   a.original_source_path,
                   a.created_at_ms,
                   a.row_version,
                   a.dependency_status,
                   a.bundle_sha256,
                   a.is_favorite,
                   am.width, am.height, am.duration_ms, am.captured_at_ms, am.metadata_json
            FROM assets a
            LEFT JOIN asset_metadata am ON am.asset_id = a.asset_id
            WHERE a.asset_id = $assetId;
            """;
        command.Parameters.AddWithValue("$assetId", DbGuid.Format(assetId));

        MediaType mType;
        string fileName;
        string originalName;
        string currentLoc;
        string? targetLoc;
        ManagedPathState pathState;
        string fingerprint;
        long byteLength;
        AssetState status;
        DateTimeOffset addedAt;
        string? sourceDisplayName;
        string? sourceKind;
        string? originalPath;
        DateTimeOffset createdAt;
        long rowVersion;
        AssetDependencyStatus dependencyStatus;
        string? bundleSha256;
        bool isFavorite;
        int? metaWidth = null;
        int? metaHeight = null;
        int? metaDurationMs = null;
        DateTimeOffset? capturedAt = null;
        string? metadataJson = null;

        await using (var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false))
        {
            if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return null;
            }

            mType = DbEnum.ParseMediaType(reader.GetString(1));
            fileName = reader.GetString(2);
            originalName = reader.GetString(3);
            currentLoc = reader.GetString(4);
            targetLoc = reader.IsDBNull(5) ? null : reader.GetString(5);
            pathState = DbEnum.ParseManagedPathState(reader.GetString(6));
            fingerprint = reader.GetString(7);
            byteLength = reader.GetInt64(8);
            status = DbEnum.ParseAssetState(reader.GetString(9));
            addedAt = DbTime.Parse(reader.GetInt64(10));

            sourceDisplayName = reader.IsDBNull(11) ? null : reader.GetString(11);
            sourceKind = reader.IsDBNull(12) ? null : reader.GetString(12);
            originalPath = reader.IsDBNull(13) ? null : reader.GetString(13);
            createdAt = DbTime.Parse(reader.GetInt64(14));
            rowVersion = reader.GetInt64(15);
            dependencyStatus = reader.IsDBNull(16)
                ? AssetDependencyStatus.SelfContained
                : DbEnum.ParseAssetDependencyStatus(reader.GetString(16));
            bundleSha256 = reader.IsDBNull(17) ? null : reader.GetString(17);
            isFavorite = !reader.IsDBNull(18) && reader.GetInt64(18) != 0;

            metaWidth = reader.IsDBNull(19) ? null : reader.GetInt32(19);
            metaHeight = reader.IsDBNull(20) ? null : reader.GetInt32(20);
            metaDurationMs = reader.IsDBNull(21) ? null : reader.GetInt32(21);
            capturedAt = reader.IsDBNull(22) ? null : DbTime.Parse(reader.GetInt64(22));
            metadataJson = reader.IsDBNull(23) ? null : reader.GetString(23);
        }

        // I07.2/PHASE 2: Published Profile relations — one query returns all visible relations.
        // Owner is derived from this relation list, eliminating a redundant join.
        MediaOwnerProfile? ownerProfile = null;
        var peopleInMedia = new List<MediaPersonItem>();
        var linkedProfiles = new List<MediaLinkedProfileItem>();

        var appearanceBasis = mType == MediaType.Video
            ? "Confirmed across sampled frames"
            : "Confirmed Appearance";

        await using (var relCmd = connection.CreateCommand())
        {
            relCmd.CommandText = """
                SELECT pa.relation_type, p.profile_id, p.display_name, p.profile_storage_token
                FROM profile_assets pa
                JOIN profiles p ON pa.profile_id = p.profile_id
                WHERE pa.asset_id = $assetId
                  AND pa.publication_import_unit_id IS NULL
                  AND p.trashed_at_ms IS NULL
                  AND p.visibility = 'PUBLISHED'
                ORDER BY p.display_name;
                """;
            relCmd.Parameters.AddWithValue("$assetId", DbGuid.Format(assetId));
            await using var relReader = await relCmd.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await relReader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                var relType = DbEnum.ParseProfileAssetRelation(relReader.GetString(0));
                var pid = DbGuid.Parse(relReader.GetString(1));
                var pName = relReader.GetString(2);
                var token = relReader.IsDBNull(3) ? null : relReader.GetString(3);

                if (relType == ProfileAssetRelation.Owner && ownerProfile is null)
                {
                    ownerProfile = new MediaOwnerProfile(pid, pName, token);
                }
                else if (relType == ProfileAssetRelation.Appears)
                {
                    peopleInMedia.Add(new MediaPersonItem(
                        pid, pName, token, IsConfirmed: true, AppearanceBasis: appearanceBasis));
                }
                else if (relType == ProfileAssetRelation.Manual)
                {
                    linkedProfiles.Add(new MediaLinkedProfileItem(pid, pName, token));
                }
            }
        }

        // Build media info from persisted metadata only (I07.6: no probing).
        object? info = null;
        ImageMetadata? imageMeta = null;
        VideoMetadata? videoMeta = null;
        ModelMetadata? modelMeta = null;

        if (mType == MediaType.Image)
        {
            imageMeta = !string.IsNullOrWhiteSpace(metadataJson)
                ? ImageMetadataAdapter.Deserialize(metadataJson)
                : null;

            if (imageMeta != null)
            {
                metaWidth ??= imageMeta.Width;
                metaHeight ??= imageMeta.Height;
                capturedAt ??= imageMeta.CapturedAt;

                info = new ImageMediaInfo(
                    Format: imageMeta.Format ?? Path.GetExtension(fileName).TrimStart('.').ToUpperInvariant(),
                    PixelWidth: metaWidth ?? 0,
                    PixelHeight: metaHeight ?? 0,
                    Orientation: imageMeta.Orientation,
                    ColorSpace: imageMeta.ColorSpace,
                    BitDepth: imageMeta.BitDepth);
            }
            else if (metaWidth.HasValue && metaHeight.HasValue)
            {
                info = new ImageMediaInfo(
                    Format: Path.GetExtension(fileName).TrimStart('.').ToUpperInvariant(),
                    PixelWidth: metaWidth.Value,
                    PixelHeight: metaHeight.Value);
            }
        }
        else if (mType == MediaType.Video)
        {
            videoMeta = !string.IsNullOrWhiteSpace(metadataJson)
                ? VideoMetadataAdapter.Deserialize(metadataJson)
                : null;

            if (videoMeta != null)
            {
                metaWidth ??= videoMeta.Width;
                metaHeight ??= videoMeta.Height;
                metaDurationMs ??= (int?)videoMeta.Duration?.TotalMilliseconds;
                capturedAt ??= videoMeta.CapturedAt;

                info = new VideoMediaInfo(
                    Container: videoMeta.Container ?? Path.GetExtension(fileName).TrimStart('.').ToUpperInvariant(),
                    Duration: metaDurationMs.HasValue ? TimeSpan.FromMilliseconds(metaDurationMs.Value) : (videoMeta.Duration ?? TimeSpan.Zero),
                    PixelWidth: metaWidth ?? 0,
                    PixelHeight: metaHeight ?? 0,
                    VideoCodec: videoMeta.VideoCodec ?? "H.264 / AVC",
                    FrameRate: videoMeta.FrameRate,
                    AudioCodec: videoMeta.AudioCodec);
            }
            else
            {
                info = new VideoMediaInfo(
                    Container: Path.GetExtension(fileName).TrimStart('.').ToUpperInvariant(),
                    Duration: metaDurationMs.HasValue ? TimeSpan.FromMilliseconds(metaDurationMs.Value) : TimeSpan.Zero,
                    PixelWidth: metaWidth ?? 0,
                    PixelHeight: metaHeight ?? 0,
                    VideoCodec: "H.264 / AVC");
            }
        }
        else if (mType == MediaType.Model)
        {
            modelMeta = !string.IsNullOrWhiteSpace(metadataJson)
                ? ModelMetadata.FromJson(metadataJson)
                : null;

            if (modelMeta != null)
            {
                info = new ModelMediaInfo(
                    Format: modelMeta.Format ?? Path.GetExtension(fileName).TrimStart('.').ToUpperInvariant(),
                    MeshCount: modelMeta.MeshCount,
                    MaterialCount: modelMeta.MaterialCount,
                    AnimationCount: modelMeta.AnimationCount);
            }
            else
            {
                info = new ModelMediaInfo(
                    Format: Path.GetExtension(fileName).TrimStart('.').ToUpperInvariant());
            }
        }

        var ownerName = ownerProfile?.DisplayName;

        string who = peopleInMedia.Count > 0
            ? string.Join(", ", peopleInMedia.Select(p => p.DisplayName))
            : (!string.IsNullOrWhiteSpace(ownerName) ? ownerName : MediaCluesBuilder.MissingValue);

        string where = !string.IsNullOrWhiteSpace(sourceDisplayName)
            ? sourceDisplayName
            : (!string.IsNullOrWhiteSpace(originalPath) ? originalPath : MediaCluesBuilder.MissingValue);

        MediaClues clues;
        if (imageMeta != null)
        {
            clues = MediaCluesBuilder.BuildForImage(imageMeta, who: who, fallbackWhere: where);
        }
        else if (videoMeta != null)
        {
            clues = MediaCluesBuilder.BuildForVideo(videoMeta, who: who, fallbackWhere: where);
        }
        else if (modelMeta != null)
        {
            clues = MediaCluesBuilder.BuildForModel(
                modelMeta,
                who: who,
                fallbackWhere: where,
                fallbackFormat: Path.GetExtension(fileName).TrimStart('.'),
                fileSizeBytes: byteLength);
        }
        else
        {
            string when = capturedAt.HasValue
                ? capturedAt.Value.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)
                : MediaCluesBuilder.MissingValue;

            string how = !string.IsNullOrWhiteSpace(sourceKind)
                ? sourceKind
                : (mType != MediaType.Model ? mType.ToString().ToUpperInvariant() : MediaCluesBuilder.MissingValue);

            clues = MediaCluesBuilder.Build(who, where, when, how);
        }

        var baselineModel = new MediaDetailReadModel(
            assetId,
            mType,
            fileName,
            originalName,
            currentLoc,
            targetLoc,
            pathState,
            fingerprint,
            byteLength,
            status,
            addedAt,
            clues,
            DependencyStatus: dependencyStatus,
            BundleSha256: bundleSha256);

        var fileDetails = MediaFileDetailsBuilder.BuildGroups(
            baselineModel,
            metaWidth,
            metaHeight,
            metaDurationMs,
            createdAt,
            imageMetadata: imageMeta,
            videoMetadata: videoMeta,
            modelMetadata: modelMeta);

        // I07.4: Resolve absolute file path during the read, off-UI.
        string? absoluteFilePath = null;
        if (_vaultPaths is not null
            && !string.IsNullOrWhiteSpace(currentLoc)
            && !string.IsNullOrWhiteSpace(fileName))
        {
            try
            {
                absoluteFilePath = _vaultPaths.ResolveVaultRelativePath(currentLoc + "/" + fileName);
                if (!File.Exists(absoluteFilePath))
                {
                    absoluteFilePath = null;
                }
            }
            catch (ArgumentException)
            {
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return new MediaDetailReadModel(
            assetId,
            mType,
            fileName,
            originalName,
            currentLoc,
            targetLoc,
            pathState,
            fingerprint,
            byteLength,
            status,
            addedAt,
            clues,
            ownerProfile,
            peopleInMedia,
            linkedProfiles,
            info,
            fileDetails,
            absoluteFilePath,
            RowVersion: rowVersion,
            DependencyStatus: dependencyStatus,
            BundleSha256: bundleSha256,
            IsFavorite: isFavorite);
    }

    public async Task<AssetSource?> GetAssetSourceAsync(
        Guid assetId,
        CancellationToken cancellationToken = default)
    {
        if (assetId == Guid.Empty)
        {
            return null;
        }

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT a.asset_id, a.state, a.media_type, a.sha256, a.byte_length,
                   a.original_source_path, a.current_managed_relative_path,
                   a.current_managed_file_name,
                   (SELECT i.source_path FROM import_items i
                    WHERE i.candidate_asset_id = a.asset_id LIMIT 1),
                   m.duration_ms
            FROM assets a
            LEFT JOIN asset_metadata m ON m.asset_id = a.asset_id
            WHERE a.asset_id = $assetId;
            """;
        command.Parameters.AddWithValue("$assetId", DbGuid.Format(assetId));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        var importSourcePath = reader.IsDBNull(8) ? null : reader.GetString(8);
        var originalSourcePath = reader.IsDBNull(5) ? null : reader.GetString(5);

        return new AssetSource(
            DbGuid.Parse(reader.GetString(0)),
            DbEnum.ParseAssetState(reader.GetString(1)),
            DbEnum.ParseMediaType(reader.GetString(2)),
            reader.IsDBNull(3) ? null : reader.GetString(3),
            reader.IsDBNull(4) ? null : reader.GetInt64(4),
            reader.IsDBNull(6) ? null : reader.GetString(6),
            reader.IsDBNull(7) ? null : reader.GetString(7),
            importSourcePath ?? originalSourcePath,
            reader.IsDBNull(9) ? null : reader.GetInt64(9));
    }

    public async Task<bool> HasModelMetadataAsync(
        Guid assetId,
        CancellationToken cancellationToken = default)
    {
        if (assetId == Guid.Empty)
        {
            return false;
        }

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT metadata_json IS NOT NULL AND metadata_json <> ''
            FROM asset_metadata
            WHERE asset_id = $assetId;
            """;
        command.Parameters.AddWithValue("$assetId", DbGuid.Format(assetId));
        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        return result is long l && l == 1;
    }

    public async Task<MediaTileReadModel?> GetMediaTileAsync(
        Guid assetId,
        CancellationToken cancellationToken = default)
    {
        if (assetId == Guid.Empty)
        {
            return null;
        }

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = """
            SELECT a.asset_id, a.media_type, a.state, a.asset_storage_token,
                   a.current_managed_relative_path, a.current_managed_file_name,
                   m.width, m.height, m.duration_ms, a.byte_length,
                   a.created_at_ms, a.added_to_library_at_ms,
                   p.profile_id AS owner_profile_id,
                   p.display_name AS owner_display_name,
                   a.row_version
            FROM assets a
            LEFT JOIN asset_metadata m ON a.asset_id = m.asset_id
            LEFT JOIN profile_assets pa ON a.asset_id = pa.asset_id AND pa.relation_type = 'OWNER' AND pa.publication_import_unit_id IS NULL
            LEFT JOIN profiles p ON pa.profile_id = p.profile_id
            WHERE a.asset_id = $assetId;
            """;
        command.Parameters.AddWithValue("$assetId", DbGuid.Format(assetId));

        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        if (!await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            return null;
        }

        return ReadMediaTile(reader);
    }

    public async Task<MediaPage> GetMediaPageAsync(
        MediaQuery? query = null,
        CancellationToken cancellationToken = default)
    {
        query ??= new MediaQuery();
        var pageSize = Math.Clamp(query.PageSize, 1, 500);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        var (whereClause, whereParams) = BuildWhereClause(query);

        long totalCount;
        await using (var countCommand = connection.CreateCommand())
        {
            countCommand.CommandText = $"""
                SELECT COUNT(*)
                FROM assets a
                LEFT JOIN profile_assets pa ON a.asset_id = pa.asset_id AND pa.relation_type = 'OWNER' AND pa.publication_import_unit_id IS NULL
                {whereClause};
                """;
            foreach (var (name, value) in whereParams)
            {
                countCommand.Parameters.AddWithValue(name, value);
            }
            totalCount = Convert.ToInt64(
                await countCommand.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false),
                CultureInfo.InvariantCulture);
        }

        var (orderClause, cursorClause, cursorParams) = BuildCursorAndOrder(query);
        var combinedWhere = string.IsNullOrWhiteSpace(whereClause)
            ? (string.IsNullOrWhiteSpace(cursorClause) ? "" : $"WHERE {cursorClause}")
            : (string.IsNullOrWhiteSpace(cursorClause) ? whereClause : $"{whereClause} AND ({cursorClause})");

        var sql = $"""
            SELECT a.asset_id, a.media_type, a.state, a.asset_storage_token,
                   a.current_managed_relative_path, a.current_managed_file_name,
                   m.width, m.height, m.duration_ms, a.byte_length,
                   a.created_at_ms, a.added_to_library_at_ms,
                   p.profile_id AS owner_profile_id,
                   p.display_name AS owner_display_name,
                   a.row_version
            FROM assets a
            LEFT JOIN asset_metadata m ON a.asset_id = m.asset_id
            LEFT JOIN profile_assets pa ON a.asset_id = pa.asset_id AND pa.relation_type = 'OWNER' AND pa.publication_import_unit_id IS NULL
            LEFT JOIN profiles p ON pa.profile_id = p.profile_id
            {combinedWhere}
            {orderClause}
            LIMIT {pageSize + 1};
            """;

        var items = new List<MediaTileReadModel>();
        await using (var command = connection.CreateCommand())
        {
            command.CommandText = sql;
            foreach (var (name, value) in whereParams)
            {
                command.Parameters.AddWithValue(name, value);
            }
            foreach (var (name, value) in cursorParams)
            {
                command.Parameters.AddWithValue(name, value);
            }

            await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
            while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
            {
                items.Add(ReadMediaTile(reader));
            }
        }

        var hasMore = items.Count > pageSize;
        if (hasMore)
        {
            items.RemoveAt(items.Count - 1);
        }

        string? nextPageToken = null;
        if (hasMore && items.Count > 0)
        {
            var last = items[^1];
            nextPageToken = EncodeContinuationToken(query.SortOrder, last);
        }

        return new MediaPage(items, nextPageToken, hasMore, totalCount);
    }

    public async Task<MediaTileReadModel?> GetMediaByStorageTokenAsync(
        string storageToken,
        CancellationToken cancellationToken = default)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(storageToken);

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);

        await using var command = connection.CreateCommand();
        command.CommandText = "SELECT asset_id FROM assets WHERE asset_storage_token = $token;";
        command.Parameters.AddWithValue("$token", storageToken.Trim());

        var result = await command.ExecuteScalarAsync(cancellationToken).ConfigureAwait(false);
        if (result is null or DBNull)
        {
            return null;
        }

        return await GetMediaTileAsync(DbGuid.Parse((string)result), cancellationToken).ConfigureAwait(false);
    }

    private static MediaTileReadModel ReadMediaTile(SqliteDataReader reader)
    {
        var assetId = DbGuid.Parse(reader.GetString(0));
        var mType = DbEnum.ParseMediaType(reader.GetString(1));
        var state = DbEnum.ParseAssetState(reader.GetString(2));
        var storageToken = reader.IsDBNull(3) ? null : reader.GetString(3);
        var currentPath = reader.IsDBNull(4) ? null : reader.GetString(4);
        var currentFileName = reader.IsDBNull(5) ? null : reader.GetString(5);
        var width = reader.IsDBNull(6) ? (int?)null : reader.GetInt32(6);
        var height = reader.IsDBNull(7) ? (int?)null : reader.GetInt32(7);
        var durationMs = reader.IsDBNull(8) ? (int?)null : reader.GetInt32(8);
        var byteLength = reader.IsDBNull(9) ? (long?)null : reader.GetInt64(9);
        var createdAt = DbTime.Parse(reader.GetInt64(10));
        var addedAt = reader.IsDBNull(11) ? (DateTimeOffset?)null : DbTime.Parse(reader.GetInt64(11));
        var ownerProfileId = reader.IsDBNull(12) ? (Guid?)null : DbGuid.Parse(reader.GetString(12));
        var ownerDisplayName = reader.IsDBNull(13) ? null : reader.GetString(13);
        var rowVersion = reader.GetInt64(14);

        return new MediaTileReadModel(
            assetId,
            mType,
            state,
            storageToken,
            currentPath,
            currentFileName,
            width,
            height,
            durationMs,
            byteLength,
            createdAt,
            addedAt,
            ownerProfileId,
            ownerDisplayName,
            rowVersion);
    }

    private static (string WhereClause, List<(string Name, object Value)> Parameters) BuildWhereClause(
        MediaQuery query)
    {
        var clauses = new List<string>();
        var parameters = new List<(string Name, object Value)>();

        if (query.MediaType.HasValue)
        {
            clauses.Add("a.media_type = $mediaType");
            parameters.Add(("$mediaType", DbEnum.Format(query.MediaType.Value)));
        }

        if (query.State.HasValue)
        {
            clauses.Add("a.state = $state");
            parameters.Add(("$state", DbEnum.Format(query.State.Value)));
        }

        if (query.OwnerProfileId.HasValue)
        {
            clauses.Add("pa.profile_id = $ownerProfileId");
            parameters.Add(("$ownerProfileId", DbGuid.Format(query.OwnerProfileId.Value)));
        }

        var whereClause = clauses.Count > 0 ? "WHERE " + string.Join(" AND ", clauses) : "";
        return (whereClause, parameters);
    }

    private static (string OrderClause, string CursorClause, List<(string Name, object Value)> Parameters) BuildCursorAndOrder(
        MediaQuery query)
    {
        var parameters = new List<(string Name, object Value)>();
        string orderClause;
        string cursorClause = "";

        var token = DecodeContinuationToken(query.ContinuationToken);

        switch (query.SortOrder)
        {
            case MediaSortOrder.AddedToLibraryAsc:
                orderClause = "ORDER BY coalesce(a.added_to_library_at_ms, a.created_at_ms) ASC, a.asset_id ASC";
                if (token is not null)
                {
                    cursorClause = "(coalesce(a.added_to_library_at_ms, a.created_at_ms) > $cTime OR (coalesce(a.added_to_library_at_ms, a.created_at_ms) = $cTime AND a.asset_id > $cId))";
                    parameters.Add(("$cTime", token.Value.LongValue));
                    parameters.Add(("$cId", DbGuid.Format(token.Value.AssetId)));
                }
                break;

            case MediaSortOrder.CreatedAtDesc:
                orderClause = "ORDER BY a.created_at_ms DESC, a.asset_id DESC";
                if (token is not null)
                {
                    cursorClause = "(a.created_at_ms < $cTime OR (a.created_at_ms = $cTime AND a.asset_id < $cId))";
                    parameters.Add(("$cTime", token.Value.LongValue));
                    parameters.Add(("$cId", DbGuid.Format(token.Value.AssetId)));
                }
                break;

            case MediaSortOrder.CreatedAtAsc:
                orderClause = "ORDER BY a.created_at_ms ASC, a.asset_id ASC";
                if (token is not null)
                {
                    cursorClause = "(a.created_at_ms > $cTime OR (a.created_at_ms = $cTime AND a.asset_id > $cId))";
                    parameters.Add(("$cTime", token.Value.LongValue));
                    parameters.Add(("$cId", DbGuid.Format(token.Value.AssetId)));
                }
                break;

            case MediaSortOrder.ByteLengthDesc:
                orderClause = "ORDER BY coalesce(a.byte_length, 0) DESC, a.created_at_ms DESC, a.asset_id DESC";
                if (token is not null)
                {
                    cursorClause = "(coalesce(a.byte_length, 0) < $cBytes OR (coalesce(a.byte_length, 0) = $cBytes AND (a.created_at_ms < $cTime OR (a.created_at_ms = $cTime AND a.asset_id < $cId))))";
                    parameters.Add(("$cBytes", token.Value.LongValue));
                    parameters.Add(("$cTime", token.Value.SecondLongValue));
                    parameters.Add(("$cId", DbGuid.Format(token.Value.AssetId)));
                }
                break;

            case MediaSortOrder.AddedToLibraryDesc:
            default:
                orderClause = "ORDER BY coalesce(a.added_to_library_at_ms, a.created_at_ms) DESC, a.asset_id DESC";
                if (token is not null)
                {
                    cursorClause = "(coalesce(a.added_to_library_at_ms, a.created_at_ms) < $cTime OR (coalesce(a.added_to_library_at_ms, a.created_at_ms) = $cTime AND a.asset_id < $cId))";
                    parameters.Add(("$cTime", token.Value.LongValue));
                    parameters.Add(("$cId", DbGuid.Format(token.Value.AssetId)));
                }
                break;
        }

        return (orderClause, cursorClause, parameters);
    }

    private static string EncodeContinuationToken(MediaSortOrder sortOrder, MediaTileReadModel item)
    {
        var addedOrCreated = (item.AddedToLibraryAtUtc ?? item.CreatedAtUtc).ToUnixTimeMilliseconds();
        return sortOrder switch
        {
            MediaSortOrder.ByteLengthDesc => $"{item.AssetId:D}:{item.ByteLength ?? 0}:{item.CreatedAtUtc.ToUnixTimeMilliseconds()}",
            MediaSortOrder.CreatedAtDesc or MediaSortOrder.CreatedAtAsc => $"{item.AssetId:D}:{item.CreatedAtUtc.ToUnixTimeMilliseconds()}",
            _ => $"{item.AssetId:D}:{addedOrCreated}",
        };
    }

    private static (Guid AssetId, long LongValue, long SecondLongValue)? DecodeContinuationToken(string? token)
    {
        if (string.IsNullOrWhiteSpace(token))
        {
            return null;
        }

        var parts = token.Split(':');
        if (parts.Length < 2 || !DomainId.TryParse(parts[0], out var assetId))
        {
            return null;
        }

        if (parts.Length == 3 && long.TryParse(parts[1], out var val1) && long.TryParse(parts[2], out var val2))
        {
            return (assetId, val1, val2);
        }

        if (long.TryParse(parts[1], out var longVal))
        {
            return (assetId, longVal, 0);
        }

        return null;
    }

    public async Task<IReadOnlyList<Database.Writes.AssetComponentRecord>> GetAssetComponentsAsync(
        Guid assetId,
        CancellationToken cancellationToken = default)
    {
        if (assetId == Guid.Empty) return [];

        await using var connection = await _connectionFactory.OpenConnectionAsync(cancellationToken)
            .ConfigureAwait(false);
        await using var command = connection.CreateCommand();
        command.CommandText =
            """
            SELECT asset_id, component_relative_path, normalized_component_path,
                   component_role, sha256, byte_length,
                   original_source_path, source_identity_json,
                   source_cleanup_state, source_cleanup_error, row_version
            FROM asset_components
            WHERE asset_id = $assetId
            ORDER BY component_role ASC, normalized_component_path ASC;
            """;
        command.Parameters.AddWithValue("$assetId", DbGuid.Format(assetId));

        var list = new List<Database.Writes.AssetComponentRecord>();
        await using var reader = await command.ExecuteReaderAsync(cancellationToken).ConfigureAwait(false);
        while (await reader.ReadAsync(cancellationToken).ConfigureAwait(false))
        {
            list.Add(new Database.Writes.AssetComponentRecord(
                DbGuid.Parse(reader.GetString(0)),
                reader.GetString(1),
                reader.GetString(2),
                DbEnum.ParseComponentRole(reader.GetString(3)),
                reader.GetString(4),
                reader.GetInt64(5),
                reader.IsDBNull(6) ? null : reader.GetString(6),
                reader.IsDBNull(7) ? null : reader.GetString(7),
                DbEnum.ParseSourceCleanupState(reader.GetString(8)),
                reader.IsDBNull(9) ? null : reader.GetString(9),
                reader.GetInt64(10)));
        }

        return list;
    }

}

public sealed record AssetSource(
    Guid AssetId,
    AssetState State,
    MediaType MediaType,
    string? Sha256,
    long? ByteLength,
    string? CurrentManagedRelativePath,
    string? ManagedFileName,
    string? SourcePath,
    long? DurationMilliseconds = null)
{

    public bool IsCandidate => State == AssetState.Candidate;

    public bool IsActive => State == AssetState.Active;

    public bool IsRetired => State == AssetState.Retired;

    public string? ResolveReadablePath(VaultPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        if (!string.IsNullOrWhiteSpace(CurrentManagedRelativePath)
            && !string.IsNullOrWhiteSpace(ManagedFileName))
        {
            try
            {
                var vaultRelativeFile = CurrentManagedRelativePath + "/" + ManagedFileName;
                var managed = paths.ResolveVaultRelativePath(vaultRelativeFile);
                if (File.Exists(managed))
                {
                    return managed;
                }
            }
            catch (ArgumentException)
            {
            }
            catch (IOException)
            {
            }
            catch (UnauthorizedAccessException)
            {
            }
        }

        return !string.IsNullOrWhiteSpace(SourcePath) && File.Exists(SourcePath)
            ? SourcePath
            : null;
    }

    /// <summary>
    /// Resolves the canonical Vault-managed path only. Unlike <see cref="ResolveReadablePath"/>,
    /// this never falls back to the original source path. Stage 2 handlers must use this to
    /// guarantee they read from canonical managed media.
    /// </summary>
    public string? ResolveManagedPath(VaultPaths paths)
    {
        ArgumentNullException.ThrowIfNull(paths);

        if (string.IsNullOrWhiteSpace(CurrentManagedRelativePath)
            || string.IsNullOrWhiteSpace(ManagedFileName))
        {
            return null;
        }

        try
        {
            var vaultRelativeFile = CurrentManagedRelativePath + "/" + ManagedFileName;
            var managed = paths.ResolveVaultRelativePath(vaultRelativeFile);
            return File.Exists(managed) ? managed : null;
        }
        catch (ArgumentException)
        {
            return null;
        }
        catch (IOException)
        {
            return null;
        }
        catch (UnauthorizedAccessException)
        {
            return null;
        }
    }
}
