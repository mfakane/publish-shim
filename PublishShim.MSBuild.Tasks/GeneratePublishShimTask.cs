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
    public string ShimExecutableName { get; set; } = string.Empty;

    [Required]
    public string TargetRelativePath { get; set; } = string.Empty;

    [Required]
    public string RuntimeIdentifier { get; set; } = string.Empty;

    public string PublishShimKind { get; set; } = "Auto";

    public string? NativeShimPath { get; set; }

    public string? NativeShimDirectory { get; set; }

    [Output]
    public string GeneratedShimPath { get; private set; } = string.Empty;

    public override bool Execute()
    {
        try
        {
            var publishDirectory = Path.GetFullPath(PublishDirectory);
            var shimExecutableName = PublishShimPathUtilities.NormalizeFileName(ShimExecutableName, nameof(ShimExecutableName));
            var targetRelativePath = PublishShimPathUtilities.NormalizeRelativePath(TargetRelativePath, publishDirectory, nameof(TargetRelativePath));
            var targetPath = Path.Combine(publishDirectory, targetRelativePath);
            var generatedShimPath = Path.Combine(publishDirectory, shimExecutableName);

            ValidateInputs(publishDirectory, targetPath, generatedShimPath);

            var nativeShimPath = ResolveNativeShimPath(RuntimeIdentifier, NativeShimPath, NativeShimDirectory, PublishShimKind, targetPath);

            CopyShim(nativeShimPath, generatedShimPath);
            AppendConfiguration(generatedShimPath, targetRelativePath);

            GeneratedShimPath = generatedShimPath;
            Log.LogMessage(MessageImportance.High, $"Generated publish shim: '{generatedShimPath}'.");
            return !Log.HasLoggedErrors;
        }
        catch (Exception ex)
        {
            Log.LogErrorFromException(ex, showStackTrace: true, showDetail: true, file: null);
            return false;
        }
    }

    private static void ValidateInputs(string publishDirectory, string targetPath, string generatedShimPath)
    {
        if (!Directory.Exists(publishDirectory))
        {
            throw new DirectoryNotFoundException($"The publish directory does not exist: '{publishDirectory}'.");
        }

        if (!File.Exists(targetPath))
        {
            throw new FileNotFoundException($"The target executable was not found: '{targetPath}'.", targetPath);
        }

        if (PublishShimPathUtilities.PathEquals(targetPath, generatedShimPath))
        {
            throw new InvalidOperationException("ShimExecutableName must not overwrite TargetRelativePath.");
        }
    }

    private string ResolveNativeShimPath(string runtimeIdentifier, string? nativeShimPath, string? nativeShimDirectory, string publishShimKind, string targetPath)
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
            ? DetectPublishShimKind(targetPath)
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

    private static PublishShimKindValue DetectPublishShimKind(string targetPath)
    {
        return ReadSubsystem(targetPath) switch
        {
            ImageSubsystem.WindowsCui => PublishShimKindValue.Exe,
            ImageSubsystem.WindowsGui => PublishShimKindValue.WinExe,
            var subsystem => throw new InvalidOperationException($"The target executable '{targetPath}' uses unsupported PE subsystem '{subsystem}'."),
        };
    }

    private static ImageSubsystem ReadSubsystem(string targetPath)
    {
        using var stream = new FileStream(targetPath, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete);
        Span<byte> dosHeader = stackalloc byte[64];
        ReadExactly(stream, dosHeader);

        if (BinaryPrimitives.ReadUInt16LittleEndian(dosHeader) != DosSignature)
        {
            throw new InvalidOperationException($"The target executable '{targetPath}' is not a valid MZ executable.");
        }

        var peHeaderOffset = BinaryPrimitives.ReadInt32LittleEndian(dosHeader.Slice(PeHeaderPointerOffset, sizeof(int)));
        if (peHeaderOffset < 0)
        {
            throw new InvalidOperationException($"The target executable '{targetPath}' has an invalid PE header offset.");
        }

        stream.Position = peHeaderOffset;
        Span<byte> headers = stackalloc byte[OptionalHeaderStartOffset + SubsystemOffset + sizeof(ushort)];
        ReadExactly(stream, headers);

        if (BinaryPrimitives.ReadUInt32LittleEndian(headers) != PeSignature)
        {
            throw new InvalidOperationException($"The target executable '{targetPath}' is missing a valid PE signature.");
        }

        var optionalHeaderSize = BinaryPrimitives.ReadUInt16LittleEndian(headers.Slice(OptionalHeaderSizeOffset, sizeof(ushort)));
        if (optionalHeaderSize < SubsystemOffset + sizeof(ushort))
        {
            throw new InvalidOperationException($"The target executable '{targetPath}' has an incomplete optional header.");
        }

        var optionalHeaderMagic = BinaryPrimitives.ReadUInt16LittleEndian(headers.Slice(OptionalHeaderStartOffset, sizeof(ushort)));
        if (optionalHeaderMagic is not Pe32Magic and not Pe32PlusMagic)
        {
            throw new InvalidOperationException($"The target executable '{targetPath}' uses unsupported optional header magic '0x{optionalHeaderMagic:X4}'.");
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

}
