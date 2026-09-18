using Neuterradise.Profiling.Protocol;
using System.Collections.Concurrent;
using System.Security.Cryptography;
using OpenCvSharp;

namespace Neuterradise.Profiling.Worker.FaceAnalysis;
public sealed class OpenCvFaceImageSource : IFaceImageSource
{
    public IFaceImage LoadImage(string path)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        if (!File.Exists(path))
        {
            throw new FaceInputException($"Analyzed image does not exist: {path}.");
        }

        var mat = Cv2.ImRead(path, ImreadModes.Color);
        if (mat is null || mat.Empty())
        {
            mat?.Dispose();
            throw new FaceInputException($"Analyzed image could not be decoded: {path}.");
        }

        return new OpenCvFaceImage(mat);
    }

    public IReadOnlyList<FaceFrameSample> LoadVideoSamples(string path, VideoSamplePlan plan)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);
        ArgumentNullException.ThrowIfNull(plan);
        if (plan.IsEmpty)
        {
            throw new FaceInputException("A VIDEO sample plan with no timestamps cannot be decoded.");
        }

        if (!File.Exists(path))
        {
            throw new FaceInputException($"Analyzed video does not exist: {path}.");
        }

        using var capture = new VideoCapture(path);
        if (!capture.IsOpened())
        {
            throw new FaceInputException($"Analyzed video could not be opened: {path}.");
        }

        var samples = new List<FaceFrameSample>(plan.Count);
        foreach (var timestamp in plan.SampleTimestampMilliseconds)
        {
            capture.Set(VideoCaptureProperties.PosMsec, timestamp);
            using var frame = new Mat();
            if (!capture.Read(frame) || frame.Empty())
            {
                continue;
            }

            samples.Add(new FaceFrameSample(timestamp, new OpenCvFaceImage(frame.Clone())));
        }

        return samples;
    }

    public void Dispose()
    {
    }
}
