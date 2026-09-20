using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using BuildTask = Microsoft.Build.Utilities.Task;

namespace PublishShim.MSBuild.Tasks;

public sealed class RelocatePublishArtifactsTask : BuildTask
{
    [Required]
    public string PublishDirectory { get; set; } = string.Empty;

    [Required]
    public string TargetExecutableName { get; set; } = string.Empty;

    public string ShimDirectory { get; set; } = ".app";

    [Output]
    public string ActualApplicationPath { get; private set; } = string.Empty;

    [Output]
    public string ActualApplicationRelativePath { get; private set; } = string.Empty;

    public override bool Execute()
    {
        try
        {
            var publishDirectory = Path.GetFullPath(PublishDirectory);
            var targetExecutableName = PublishShimPathUtilities.NormalizeFileName(TargetExecutableName, nameof(TargetExecutableName));
            var normalizedShimDirectory = PublishShimPathUtilities.NormalizeRelativePath(ShimDirectory, publishDirectory, nameof(ShimDirectory));
            var publishedExecutablePath = Path.Combine(publishDirectory, targetExecutableName);
            var actualApplicationRelativePath = Path.Combine(normalizedShimDirectory, targetExecutableName)
                .Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
            var actualApplicationPath = Path.Combine(publishDirectory, actualApplicationRelativePath);

            ValidateInputs(publishDirectory, publishedExecutablePath);

            PrepareDestinationDirectory(actualApplicationPath);
            MovePublishArtifacts(publishDirectory, normalizedShimDirectory);

            if (!File.Exists(actualApplicationPath))
            {
                Log.LogError($"The published target executable was not found after relocation: '{actualApplicationPath}'.");
                return false;
            }

            ActualApplicationPath = actualApplicationPath;
            ActualApplicationRelativePath = actualApplicationRelativePath;
            Log.LogMessage(MessageImportance.High, $"Relocated application: '{actualApplicationPath}'.");
            return !Log.HasLoggedErrors;
        }
        catch (Exception ex)
        {
            Log.LogErrorFromException(ex, showStackTrace: true, showDetail: true, file: null);
            return false;
        }
    }

    private static void ValidateInputs(string publishDirectory, string publishedExecutablePath)
    {
        if (!Directory.Exists(publishDirectory))
        {
            throw new DirectoryNotFoundException($"The publish directory does not exist: '{publishDirectory}'.");
        }

        if (!File.Exists(publishedExecutablePath))
        {
            throw new FileNotFoundException($"The published target executable was not found: '{publishedExecutablePath}'.", publishedExecutablePath);
        }
    }

    private static void PrepareDestinationDirectory(string actualApplicationPath)
    {
        var actualApplicationDirectory = Path.GetDirectoryName(actualApplicationPath)
            ?? throw new InvalidOperationException("Failed to resolve the shim destination directory.");

        if (Directory.Exists(actualApplicationDirectory))
        {
            Directory.Delete(actualApplicationDirectory, recursive: true);
        }

        Directory.CreateDirectory(actualApplicationDirectory);
    }

    private void MovePublishArtifacts(string publishDirectory, string normalizedShimDirectory)
    {
        var destinationDirectory = Path.Combine(publishDirectory, normalizedShimDirectory);
        var rootEntries = new DirectoryInfo(publishDirectory)
            .EnumerateFileSystemInfos()
            .Where(entry => !PublishShimPathUtilities.PathEquals(entry.FullName, destinationDirectory))
            .ToArray();

        foreach (var entry in rootEntries)
        {
            var destinationPath = Path.Combine(destinationDirectory, entry.Name);
            Log.LogMessage(MessageImportance.Low, $"Moving '{entry.FullName}' to '{destinationPath}'.");

            if (entry.Attributes.HasFlag(FileAttributes.Directory))
            {
                Directory.Move(entry.FullName, destinationPath);
            }
            else
            {
                File.Move(entry.FullName, destinationPath);
            }
        }
    }
}
