namespace Neuterradise.App.SystemServices.Jobs.Handlers;

public interface IGenerateModelPreviewJobOperation : IAuthorizedJobOperation;

public sealed class GenerateModelPreviewJobHandler : AuthorizedJobHandler
{
    public GenerateModelPreviewJobHandler(IGenerateModelPreviewJobOperation operation)
        : base("GenerateModelPreview", [JobLane.Media], "Asset", operation, runOffCallingThread: true)
    {
    }
}
