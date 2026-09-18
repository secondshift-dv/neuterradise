using Neuterradise.App.SystemServices.Operations;

namespace Neuterradise.App.SystemServices.Database.Reads;

public sealed class OperationReceiptReadsFacade
{
    private readonly CatalogDb _catalog;
    public OperationReceiptReadsFacade(CatalogDb catalog) => _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
    public Task<OperationReceipt?> ReadAsync(Guid operationId, CancellationToken cancellationToken = default) =>
        OperationReceiptReads.ReadAsync(_catalog, operationId, cancellationToken);
}
