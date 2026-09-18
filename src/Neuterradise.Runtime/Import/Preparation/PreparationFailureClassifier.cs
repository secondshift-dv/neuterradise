using System.IO;
using Neuterradise.App.Import.Verification;
using Neuterradise.App.SystemServices.Jobs;

namespace Neuterradise.App.Import.Preparation;

public enum PreparationFailureKind
{
    SourceMissing,
    SourceUnreadable,
    SourceChanged,
    InvalidMedia,
    ExactDuplicateRequired,
    PreviewFailed,
    FaceAnalysisFailed,
    GenericError
}

public sealed record PreparationFailureInfo(
    PreparationFailureKind Kind,
    bool IsBlocking,
    string ErrorCode,
    string ErrorMessage,
    JobFailureClassification JobClassification,
    string? Details = null);

public static class PreparationFailureClassifier
{
    public static bool IsBlocking(string jobKind, JobFailureClassification classification)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(jobKind);
        _ = classification;

        if (string.Equals(jobKind, "FaceAnalysis", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        if (string.Equals(jobKind, "GenerateThumbnail", StringComparison.OrdinalIgnoreCase)
            || string.Equals(jobKind, "GenerateVideoPreview", StringComparison.OrdinalIgnoreCase)
            || string.Equals(jobKind, "GenerateModelPreview", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        return true;
    }

    public static PreparationFailureInfo Classify(Exception ex, string? sourcePath = null)
    {
        ArgumentNullException.ThrowIfNull(ex);

        return ex switch
        {
            FileNotFoundException or DirectoryNotFoundException => new PreparationFailureInfo(
                PreparationFailureKind.SourceMissing,
                IsBlocking: true,
                ErrorCode: "SOURCE_MISSING",
                ErrorMessage: $"Source file not found at '{sourcePath ?? ex.Message}'.",
                JobClassification: JobFailureClassification.DeterministicInvalidInput,
                Details: ex.ToString()),

            UnauthorizedAccessException => new PreparationFailureInfo(
                PreparationFailureKind.SourceUnreadable,
                IsBlocking: true,
                ErrorCode: "SOURCE_ACCESS_DENIED",
                ErrorMessage: $"Access denied to source file at '{sourcePath ?? ex.Message}'.",
                JobClassification: JobFailureClassification.AccessTemporarilyDenied,
                Details: ex.ToString()),

            IOException ioEx => new PreparationFailureInfo(
                PreparationFailureKind.SourceUnreadable,
                IsBlocking: true,
                ErrorCode: "SOURCE_IO_ERROR",
                ErrorMessage: $"I/O error reading source file: {ioEx.Message}",
                JobClassification: JobFailureClassification.TransientIo,
                Details: ioEx.ToString()),

            InvalidDataException => new PreparationFailureInfo(
                PreparationFailureKind.InvalidMedia,
                IsBlocking: true,
                ErrorCode: "INVALID_MEDIA",
                ErrorMessage: $"Source media is corrupt or invalid: {ex.Message}",
                JobClassification: JobFailureClassification.DeterministicInvalidInput,
                Details: ex.ToString()),

            _ => new PreparationFailureInfo(
                PreparationFailureKind.GenericError,
                IsBlocking: true,
                ErrorCode: "PROCESSING_ERROR",
                ErrorMessage: ex.Message,
                JobClassification: JobFailureClassification.DeterministicInvalidInput,
                Details: ex.ToString())
        };
    }

    public static PreparationFailureInfo ClassifyJobFailure(
        string jobKind,
        JobFailureClassification classification,
        string errorCode,
        string? detail = null)
    {
        var blocking = IsBlocking(jobKind, classification);
        var kind = jobKind.ToUpperInvariant() switch
        {
            "FACEANALYSIS" => PreparationFailureKind.FaceAnalysisFailed,
            "GENERATETHUMBNAIL" or "GENERATEVIDEOPREVIEW" or "GENERATEMODELPREVIEW" => PreparationFailureKind.PreviewFailed,
            _ => PreparationFailureKind.GenericError
        };

        return new PreparationFailureInfo(
            kind,
            IsBlocking: blocking,
            ErrorCode: errorCode,
            ErrorMessage: detail ?? $"Job '{jobKind}' failed with classification {classification}.",
            JobClassification: classification,
            Details: detail);
    }

    public static VerificationBlocker ToBlocker(Guid? importItemId, PreparationFailureInfo failure) =>
        new(importItemId, failure.ErrorCode, failure.ErrorMessage, failure.Details);
}
