using Microsoft.Data.Sqlite;
using Neuterradise.App.SystemServices.Database.Reads;
using Neuterradise.App.SystemServices.Database.Writes;
using Neuterradise.App.SystemServices.Storage;

namespace Neuterradise.App.SystemServices.Database;

public sealed class CatalogDb
{
    public static bool IsDatabaseFailure(Exception exception)
    {
        ArgumentNullException.ThrowIfNull(exception);
        for (var current = exception; current is not null; current = current.InnerException)
        {
            if (current is OperationCanceledException) return false;
            if (current is SqliteException) return true;
        }
        return false;
    }

    private readonly SchemaMigrator _schemaMigrator;
    private readonly TimeProvider _timeProvider;

    public CatalogDb(VaultPaths paths, TimeProvider? timeProvider = null)
    {
        ArgumentNullException.ThrowIfNull(paths);
        _timeProvider = timeProvider ?? TimeProvider.System;
        Paths = paths;
        ConnectionFactory = new CatalogConnectionFactory(paths);
        WriteCoordinator = new CatalogWriteCoordinator();
        ImportUnitMutations = new ImportUnitMutationCoordinator();
        _schemaMigrator = new SchemaMigrator(ConnectionFactory, WriteCoordinator, _timeProvider);
    }

    public VaultPaths Paths { get; }

    public CatalogConnectionFactory ConnectionFactory { get; }

    public CatalogWriteCoordinator WriteCoordinator { get; }

    public ImportUnitMutationCoordinator ImportUnitMutations { get; }

    public int? SchemaVersion { get; private set; }

    public async Task<MigrationResult> InitializeAsync(CancellationToken cancellationToken = default)
    {
        var result = await _schemaMigrator.MigrateAsync(cancellationToken).ConfigureAwait(false);
        SchemaVersion = result.CurrentVersion;
        TaxonomyNormalization = await new SettingsWrites(this)
            .ReconcileTaxonomyNormalizationAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public TaxonomyNormalizationResult? TaxonomyNormalization { get; private set; }

    internal async Task<MigrationResult> InitializeAsync(
        Action databaseOpened,
        CancellationToken cancellationToken = default)
    {
        var result = await _schemaMigrator.MigrateAsync(databaseOpened, cancellationToken).ConfigureAwait(false);
        SchemaVersion = result.CurrentVersion;
        TaxonomyNormalization = await new SettingsWrites(this)
            .ReconcileTaxonomyNormalizationAsync(cancellationToken).ConfigureAwait(false);
        return result;
    }

    public Task<SqliteConnection> OpenConnectionAsync(CancellationToken cancellationToken = default) =>
        ConnectionFactory.OpenConnectionAsync(cancellationToken);

    public GalleryReads GalleryReads => new(this);
    public ProfileReads ProfileReads => new(this);
    public MediaReads MediaReads => new(this);
    public ImportReads ImportReads => new(this);
    public FaceReads FaceReads => new(this);
    public RelatedReads RelatedReads => new(this);
    public SchedulerReads SchedulerReads => new(this);
    public SettingsReads SettingsReads => new(this);
    public TrashReads TrashReads => new(this);
    public HealthReads HealthReads => new(this);

    public async Task<WalCheckpointResult> CheckpointWalAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await OpenConnectionAsync(cancellationToken).ConfigureAwait(false);
        return await SqlitePragmas.CheckpointWalPassiveAsync(connection, cancellationToken)
            .ConfigureAwait(false);
    }
    public ActivityReads ActivityReads => new(this);
    public OperationReceiptReadsFacade OperationReceipts => new(this);

    public ProfileWrites ProfileWrites => new(this, _timeProvider);
    public AssetWrites AssetWrites => new(this, _timeProvider);
    public ImportWrites ImportWrites => new(this, _timeProvider);
    public SettingsWrites SettingsWrites => new(this, _timeProvider);
    public TrashWrites TrashWrites => new(this);
    public MaintenanceWrites MaintenanceWrites => new(this);
    public ActivityWrites ActivityWrites => new(this);
}
