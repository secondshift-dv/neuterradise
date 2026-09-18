namespace Neuterradise.App.SystemServices.Jobs.Handlers;

public interface IGenerateVideoPreviewJobOperation : IAuthorizedJobOperation;

public sealed class GenerateVideoPreviewJobHandler : AuthorizedJobHandler
{
    public GenerateVideoPreviewJobHandler(IGenerateVideoPreviewJobOperation operation)
        : base("GenerateVideoPreview", [JobLane.Media], "Asset", operation, runOffCallingThread: true)
    {
    }
}
