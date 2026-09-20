using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using BuildTask = Microsoft.Build.Utilities.Task;

namespace PublishShim.MSBuild.Tasks;

public sealed class RelocatePublishArtifactsTask : BuildTask
{
    [Required]
    public string PublishDirectory { get; set; } = string.Empty;

    [Required]
    public ITaskItem[] Files { get; set; } = Array.Empty<ITaskItem>();

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
            var destinationDirectory = Path.GetDirectoryName(actualApplicationPath)
                ?? throw new InvalidOperationException("Failed to resolve the shim destination directory.");
            var sourceFiles = ResolveSourceFiles(Files, publishDirectory, destinationDirectory);

            ValidateInputs(publishDirectory, publishedExecutablePath, sourceFiles);

            PrepareDestinationDirectory(actualApplicationPath);
            MovePublishFiles(publishDirectory, destinationDirectory, sourceFiles);

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

    private static IReadOnlyList<string> ResolveSourceFiles(ITaskItem[] files, string publishDirectory, string destinationDirectory)
    {
        if (files.Length == 0)
        {
            throw new InvalidOperationException("Files must contain at least one publish file.");
        }

        var sourceFiles = new List<string>(files.Length);
        foreach (var file in files)
        {
            var fullPath = file.GetMetadata("FullPath");
            if (string.IsNullOrWhiteSpace(fullPath))
            {
                fullPath = file.ItemSpec;
            }

            fullPath = Path.GetFullPath(fullPath);
            var relativePath = Path.GetRelativePath(publishDirectory, fullPath);
            relativePath = PublishShimPathUtilities.NormalizeRelativePath(relativePath, publishDirectory, "Files");
            var normalizedPath = Path.Combine(publishDirectory, relativePath);
            if (IsPathUnderDirectory(normalizedPath, destinationDirectory))
            {
                throw new InvalidOperationException($"Files must not contain files under ShimDirectory: '{fullPath}'.");
            }

            if (!File.Exists(normalizedPath))
            {
                throw new FileNotFoundException($"A file in Files does not exist: '{normalizedPath}'.", normalizedPath);
            }

            if (!sourceFiles.Any(path => PublishShimPathUtilities.PathEquals(path, normalizedPath)))
            {
                sourceFiles.Add(normalizedPath);
            }
        }

        return sourceFiles;
    }

    private static void ValidateInputs(string publishDirectory, string publishedExecutablePath, IReadOnlyList<string> sourceFiles)
    {
        if (!Directory.Exists(publishDirectory))
        {
            throw new DirectoryNotFoundException($"The publish directory does not exist: '{publishDirectory}'.");
        }

        if (!File.Exists(publishedExecutablePath))
        {
            throw new FileNotFoundException($"The published target executable was not found: '{publishedExecutablePath}'.", publishedExecutablePath);
        }

        if (!sourceFiles.Any(path => PublishShimPathUtilities.PathEquals(path, publishedExecutablePath)))
        {
            throw new InvalidOperationException($"Files must include the target executable: '{publishedExecutablePath}'.");
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

    private void MovePublishFiles(string publishDirectory, string destinationDirectory, IReadOnlyList<string> sourceFiles)
    {
        foreach (var sourcePath in sourceFiles)
        {
            var relativePath = Path.GetRelativePath(publishDirectory, sourcePath);
            var destinationPath = Path.Combine(destinationDirectory, relativePath);
            var parentDirectory = Path.GetDirectoryName(destinationPath)
                ?? throw new InvalidOperationException("Failed to resolve a relocated file's destination directory.");

            Directory.CreateDirectory(parentDirectory);
            Log.LogMessage(MessageImportance.Low, $"Moving '{sourcePath}' to '{destinationPath}'.");
            File.Move(sourcePath, destinationPath);
        }
    }

    private static bool IsPathUnderDirectory(string path, string directory)
    {
        var fullPath = Path.TrimEndingDirectorySeparator(Path.GetFullPath(path));
        var fullDirectory = Path.TrimEndingDirectorySeparator(Path.GetFullPath(directory));
        return string.Equals(fullPath, fullDirectory, StringComparison.OrdinalIgnoreCase)
            || fullPath.StartsWith(fullDirectory + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)
            || fullPath.StartsWith(fullDirectory + Path.AltDirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
    }
}
