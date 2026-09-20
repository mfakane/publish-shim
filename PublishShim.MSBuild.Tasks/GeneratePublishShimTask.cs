using System.Buffers.Binary;
using System.Text;
using Microsoft.Build.Framework;
using Microsoft.Build.Utilities;
using BuildTask = Microsoft.Build.Utilities.Task;

namespace PublishShim.MSBuild.Tasks;

public sealed class GeneratePublishShimTask : BuildTask
{
    private const uint PublishShimMagic = 0x4D485350;
    private const ushort PublishShimVersion = 1;
    private const ushort DosSignature = 0x5A4D;
    private const uint PeSignature = 0x00004550;
    private const ushort Pe32Magic = 0x10B;
    private const ushort Pe32PlusMagic = 0x20B;
    private const int PeHeaderPointerOffset = 0x3C;
    private const int OptionalHeaderSizeOffset = 20;
    private const int OptionalHeaderStartOffset = 24;
    private const int SubsystemOffset = 68;
    private const string ConsoleShimFileName = "publishshim.exe";
    private const string GuiShimFileName = "publishshim.winexe.exe";

    private enum PublishShimKindValue
    {
        Auto,
        Exe,
        WinExe,
    }

    private enum ImageSubsystem : ushort
    {
        WindowsGui = 2,
        WindowsCui = 3,
    }

    [Required]
    public string PublishDirectory { get; set; } = string.Empty;

    [Required]
    public string TargetExecutableName { get; set; } = string.Empty;

    public string ShimDirectory { get; set; } = ".app";

    [Required]
    public string RuntimeIdentifier { get; set; } = string.Empty;

    public string PublishShimKind { get; set; } = "Auto";

    public string? NativeShimPath { get; set; }

    public string? NativeShimDirectory { get; set; }

    [Output]
    public string GeneratedShimPath { get; private set; } = string.Empty;

    [Output]
    public string ActualApplicationPath { get; private set; } = string.Empty;

    public override bool Execute()
    {
        try
        {
            var publishDirectory = Path.GetFullPath(PublishDirectory);
            var targetExecutableName = TargetExecutableName.Trim();
            var normalizedShimDirectory = NormalizeShimDirectory(ShimDirectory, publishDirectory);
            var publishedExecutablePath = Path.Combine(publishDirectory, targetExecutableName);
            var actualApplicationRelativePath = Path.Combine(normalizedShimDirectory, targetExecutableName).Replace('/', '\\');
            var actualApplicationPath = Path.Combine(publishDirectory, actualApplicationRelativePath);
            var generatedShimPath = Path.Combine(publishDirectory, targetExecutableName);

            ValidateInputs(publishDirectory, targetExecutableName, normalizedShimDirectory, publishedExecutablePath);

            var nativeShimPath = ResolveNativeShimPath(RuntimeIdentifier, NativeShimPath, NativeShimDirectory, PublishShimKind, publishedExecutablePath);

            PrepareDestinationDirectory(actualApplicationPath);
            MovePublishArtifacts(publishDirectory, normalizedShimDirectory);

            if (!File.Exists(actualApplicationPath))
            {
                Log.LogError($"The published target executable was not found after relocation: '{actualApplicationPath}'.");
                return false;
            }

            CopyShim(nativeShimPath, generatedShimPath);
            AppendConfiguration(generatedShimPath, actualApplicationRelativePath);

            GeneratedShimPath = generatedShimPath;
            ActualApplicationPath = actualApplicationPath;

            Log.LogMessage(MessageImportance.High, $"Generated publish shim: '{generatedShimPath}'.");
            Log.LogMessage(MessageImportance.High, $"Relocated application: '{actualApplicationPath}'.");
            return !Log.HasLoggedErrors;
        }
        catch (Exception ex)
        {
            Log.LogErrorFromException(ex, showStackTrace: true, showDetail: true, file: null);
            return false;
        }
    }

    private static void ValidateInputs(string publishDirectory, string targetExecutableName, string normalizedShimDirectory, string publishedExecutablePath)
    {
        if (!Directory.Exists(publishDirectory))
        {
            throw new DirectoryNotFoundException($"The publish directory does not exist: '{publishDirectory}'.");
        }

        if (string.IsNullOrWhiteSpace(targetExecutableName))
        {
            throw new InvalidOperationException("TargetExecutableName must be provided.");
        }

        if (targetExecutableName.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InvalidOperationException($"TargetExecutableName contains invalid path characters: '{targetExecutableName}'.");
        }

        if (string.IsNullOrWhiteSpace(normalizedShimDirectory))
        {
            throw new InvalidOperationException("ShimDirectory must resolve to a non-empty relative path.");
        }

        if (!File.Exists(publishedExecutablePath))
        {
            throw new FileNotFoundException($"The published target executable was not found: '{publishedExecutablePath}'.", publishedExecutablePath);
        }
    }

    private string NormalizeShimDirectory(string shimDirectory, string publishDirectory)
    {
        if (string.IsNullOrWhiteSpace(shimDirectory))
        {
            throw new InvalidOperationException("ShimDirectory must be provided.");
        }

        var trimmed = shimDirectory.Trim().Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(trimmed))
        {
            throw new InvalidOperationException($"ShimDirectory must be relative to the publish root: '{shimDirectory}'.");
        }

        var combined = Path.GetFullPath(Path.Combine(publishDirectory, trimmed));
        var publishRoot = AppendDirectorySeparator(Path.GetFullPath(publishDirectory));
        if (!combined.StartsWith(publishRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"ShimDirectory escapes the publish root: '{shimDirectory}'.");
        }

        var relative = Path.GetRelativePath(publishDirectory, combined);
        if (relative is "." or "")
        {
            throw new InvalidOperationException("ShimDirectory must not point to the publish root.");
        }

        return relative;
    }

    private string ResolveNativeShimPath(string runtimeIdentifier, string? nativeShimPath, string? nativeShimDirectory, string publishShimKind, string publishedExecutablePath)
    {
        if (!string.IsNullOrWhiteSpace(nativeShimPath))
        {
            var explicitPath = Path.GetFullPath(nativeShimPath);
            if (!File.Exists(explicitPath))
            {
                throw new FileNotFoundException($"The native shim executable was not found: '{explicitPath}'.", explicitPath);
            }

            return explicitPath;
        }

        if (string.IsNullOrWhiteSpace(runtimeIdentifier))
        {
            throw new InvalidOperationException("RuntimeIdentifier must be provided when NativeShimPath is not specified.");
        }

        var requestedKind = ParsePublishShimKind(publishShimKind);
        var resolvedKind = requestedKind == PublishShimKindValue.Auto
            ? DetectPublishShimKind(publishedExecutablePath)
            : requestedKind;
        var toolsRoot = string.IsNullOrWhiteSpace(nativeShimDirectory)
            ? Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "..", "..", "..", "tools"))
            : Path.GetFullPath(nativeShimDirectory);
        var shimFileName = resolvedKind == PublishShimKindValue.WinExe ? GuiShimFileName : ConsoleShimFileName;
        var resolvedPath = Path.Combine(toolsRoot, runtimeIdentifier, shimFileName);

        if (!File.Exists(resolvedPath))
        {
            throw new InvalidOperationException($"No native shim is available for RuntimeIdentifier '{runtimeIdentifier}' and kind '{resolvedKind}'. Expected '{resolvedPath}'.");
        }

        Log.LogMessage(MessageImportance.Low, $"Using publish shim kind '{resolvedKind}' from '{resolvedPath}'.");
        return resolvedPath;
    }

    private static PublishShimKindValue ParsePublishShimKind(string? publishShimKind)
    {
        if (string.IsNullOrWhiteSpace(publishShimKind))
        {
            return PublishShimKindValue.Auto;
        }

        return publishShimKind.Trim().ToUpperInvariant() switch
        {
            "AUTO" => PublishShimKindValue.Auto,
            "EXE" => PublishShimKindValue.Exe,
            "WINEXE" => PublishShimKindValue.WinExe,
            _ => throw new InvalidOperationException($"PublishShimKind must be one of Auto, Exe, or WinExe. Actual value: '{publishShimKind}'."),
        };
    }

    private static PublishShimKindValue DetectPublishShimKind(string publishedExecutablePath)
    {
        return ReadSubsystem(publishedExecutablePath) switch
        {
            ImageSubsystem.WindowsCui => PublishShimKindValue.Exe,
            ImageSubsystem.WindowsGui => PublishShimKindValue.WinExe,
            var subsystem => throw new InvalidOperationException($"The published target executable '{publishedExecutablePath}' uses unsupported PE subsystem '{subsystem}'."),
        };
    }

    private static ImageSubsystem ReadSubsystem(string publishedExecutablePath)
    {
        using var stream = new FileStream(publishedExecutablePath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        Span<byte> dosHeader = stackalloc byte[64];
        ReadExactly(stream, dosHeader);

        if (BinaryPrimitives.ReadUInt16LittleEndian(dosHeader) != DosSignature)
        {
            throw new InvalidOperationException($"The published target executable '{publishedExecutablePath}' is not a valid MZ executable.");
        }

        var peHeaderOffset = BinaryPrimitives.ReadInt32LittleEndian(dosHeader.Slice(PeHeaderPointerOffset, sizeof(int)));
        if (peHeaderOffset < 0)
        {
            throw new InvalidOperationException($"The published target executable '{publishedExecutablePath}' has an invalid PE header offset.");
        }

        stream.Position = peHeaderOffset;
        Span<byte> headers = stackalloc byte[OptionalHeaderStartOffset + SubsystemOffset + sizeof(ushort)];
        ReadExactly(stream, headers);

        if (BinaryPrimitives.ReadUInt32LittleEndian(headers) != PeSignature)
        {
            throw new InvalidOperationException($"The published target executable '{publishedExecutablePath}' is missing a valid PE signature.");
        }

        var optionalHeaderSize = BinaryPrimitives.ReadUInt16LittleEndian(headers.Slice(OptionalHeaderSizeOffset, sizeof(ushort)));
        if (optionalHeaderSize < SubsystemOffset + sizeof(ushort))
        {
            throw new InvalidOperationException($"The published target executable '{publishedExecutablePath}' has an incomplete optional header.");
        }

        var optionalHeaderMagic = BinaryPrimitives.ReadUInt16LittleEndian(headers.Slice(OptionalHeaderStartOffset, sizeof(ushort)));
        if (optionalHeaderMagic is not Pe32Magic and not Pe32PlusMagic)
        {
            throw new InvalidOperationException($"The published target executable '{publishedExecutablePath}' uses unsupported optional header magic '0x{optionalHeaderMagic:X4}'.");
        }

        return (ImageSubsystem)BinaryPrimitives.ReadUInt16LittleEndian(headers.Slice(OptionalHeaderStartOffset + SubsystemOffset, sizeof(ushort)));
    }

    private static void ReadExactly(FileStream stream, Span<byte> buffer)
    {
        var remaining = buffer;
        while (!remaining.IsEmpty)
        {
            var bytesRead = stream.Read(remaining);
            if (bytesRead == 0)
            {
                throw new EndOfStreamException($"Unexpected end of file while reading '{stream.Name}'.");
            }

            remaining = remaining[bytesRead..];
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
            .Where(entry => !PathEquals(entry.FullName, destinationDirectory))
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

    private static void CopyShim(string nativeShimPath, string generatedShimPath)
    {
        var parentDirectory = Path.GetDirectoryName(generatedShimPath);
        if (!string.IsNullOrEmpty(parentDirectory))
        {
            Directory.CreateDirectory(parentDirectory);
        }

        File.Copy(nativeShimPath, generatedShimPath, overwrite: true);
    }

    private static void AppendConfiguration(string generatedShimPath, string actualApplicationRelativePath)
    {
        var normalizedRelativePath = actualApplicationRelativePath.Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        using var stream = new FileStream(generatedShimPath, FileMode.Append, FileAccess.Write, FileShare.Read);
        using var writer = new BinaryWriter(stream, Encoding.Unicode, leaveOpen: false);

        writer.Write(normalizedRelativePath.Length);
        writer.Write(Encoding.Unicode.GetBytes(normalizedRelativePath));
        writer.Write(PublishShimMagic);
        writer.Write(PublishShimVersion);
        writer.Write((ushort)0);
        writer.Write(sizeof(int) + normalizedRelativePath.Length * sizeof(char));
    }

    private static string AppendDirectorySeparator(string path)
    {
        return path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;
    }

    private static bool PathEquals(string left, string right)
    {
        return string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.OrdinalIgnoreCase);
    }
}
