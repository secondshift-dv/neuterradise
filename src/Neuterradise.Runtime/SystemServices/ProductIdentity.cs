using System.Reflection;

namespace Neuterradise.App.SystemServices;

/// <summary>
/// Canonical public identity for the Neu Terradise v0.0.1 product line.
/// Candidate update versions remain manifest-owned and must not be replaced with these values.
/// </summary>
public static class ProductIdentity
{
    public const string DisplayName = "Neu Terradise";
    public const string ProductSlug = "neuterradise";
    public const string ProductId = "neuterradise";
    public static string Version => typeof(ProductIdentity).Assembly
        .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>()
        .SingleOrDefault(attribute => attribute.Key == "ProductVersion")?.Value
        ?? throw new InvalidOperationException("ProductVersion assembly metadata is missing.");
    public static string AssemblyVersion => typeof(ProductIdentity).Assembly.GetName().Version?.ToString()
        ?? throw new InvalidOperationException("ProductAssemblyVersion assembly metadata is missing.");
    public static string RuntimeId => typeof(ProductIdentity).Assembly
        .GetCustomAttributes<System.Reflection.AssemblyMetadataAttribute>()
        .SingleOrDefault(attribute => attribute.Key == "ProductRuntimeIdentifier")?.Value
        ?? throw new InvalidOperationException("ProductRuntimeIdentifier assembly metadata is missing.");
    public const string Channel = "stable";

    public const string AppExecutableName = "NeuTerradise.exe";
    public const string ProfilingWorkerExecutableName = "NeuTerradise.Profiling.Worker.exe";
    public const string UpdaterExecutableName = "NeuTerradise.Updater.exe";
    public static string ZipArchiveName => $"NeuTerradise-v{Version}-{RuntimeId}.zip";
}
