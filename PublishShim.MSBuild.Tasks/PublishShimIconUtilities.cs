using System.Buffers.Binary;
using System.ComponentModel;
using System.Runtime.InteropServices;

namespace PublishShim.MSBuild.Tasks;

internal static class PublishShimIconUtilities
{
    private const ushort IcoType = 1;
    private const ushort DosSignature = 0x5A4D;
    private const uint PeSignature = 0x00004550;
    private const ushort Pe32Magic = 0x10B;
    private const ushort Pe32PlusMagic = 0x20B;
    private const int PeHeaderPointerOffset = 0x3C;
    private const int CoffHeaderSize = 20;
    private const int OptionalHeaderSizeOffset = 16;
    private const int ResourceDirectoryIndex = 2;
    private const uint ResourceTypeIcon = 3;
    private const uint ResourceTypeGroupIcon = 14;
    private const uint ResourceDirectoryOffsetMask = 0x7FFF_FFFF;
    private const uint ResourceNameIdMask = 0x7FFF_FFFF;
    private const uint ResourceDirectoryFlag = 0x8000_0000;
    private const ushort NeutralLanguage = 0;
    private const int ResourceDirectoryHeaderSize = 16;
    private const int ResourceDirectoryEntrySize = 8;
    private const int ResourceDataEntrySize = 16;

    public static bool TryApplyIcon(string generatedShimPath, string? iconPath, string targetPath)
    {
        var sourcePath = string.IsNullOrWhiteSpace(iconPath)
            ? targetPath
            : Path.GetFullPath(iconPath);
        if (!File.Exists(sourcePath))
        {
            throw new FileNotFoundException($"The icon source was not found: '{sourcePath}'.", sourcePath);
        }

        var icons = Path.GetExtension(sourcePath).Equals(".ico", StringComparison.OrdinalIgnoreCase)
            ? ReadIcoFile(sourcePath)
            : ReadExecutableIcon(sourcePath);
        if (icons is null || icons.Count == 0)
        {
            return false;
        }

        UpdateIconResources(generatedShimPath, icons);
        return true;
    }

    private static IReadOnlyList<IconImage> ReadIcoFile(string path)
    {
        var image = File.ReadAllBytes(path);
        if (image.Length < 6 || ReadUInt16(image, 0) != 0 || ReadUInt16(image, 2) != IcoType)
        {
            throw new InvalidDataException($"The icon file is not a valid ICO file: '{path}'.");
        }

        var count = ReadUInt16(image, 4);
        if (count == 0)
        {
            throw new InvalidDataException($"The icon file contains no images: '{path}'.");
        }

        var directorySize = checked(6 + count * 16);
        EnsureRange(image, 0, directorySize, "ICO directory");
        var icons = new List<IconImage>(count);
        for (var index = 0; index < count; index++)
        {
            var entryOffset = 6 + index * 16;
            var bytesInResource = ReadUInt32(image, entryOffset + 8);
            var imageOffset = ReadUInt32(image, entryOffset + 12);
            if (bytesInResource == 0 || imageOffset > image.Length || bytesInResource > image.Length - imageOffset)
            {
                throw new InvalidDataException($"The ICO image entry {index} is outside the file: '{path}'.");
            }

            var data = image.AsSpan((int)imageOffset, (int)bytesInResource).ToArray();
            icons.Add(new IconImage(
                image[entryOffset],
                image[entryOffset + 1],
                image[entryOffset + 2],
                image[entryOffset + 3],
                ReadUInt16(image, entryOffset + 4),
                ReadUInt16(image, entryOffset + 6),
                data));
        }

        return icons;
    }

    private static IReadOnlyList<IconImage>? ReadExecutableIcon(string path)
    {
        var image = File.ReadAllBytes(path);
        var resourceRootOffset = GetResourceRootOffset(image);
        if (resourceRootOffset is null)
        {
            return null;
        }

        var groupTypeEntry = FindEntry(image, resourceRootOffset.Value, ResourceTypeGroupIcon);
        var iconTypeEntry = FindEntry(image, resourceRootOffset.Value, ResourceTypeIcon);
        if (groupTypeEntry is null || iconTypeEntry is null)
        {
            return null;
        }

        var groupNameDirectory = GetDirectoryOffset(image, resourceRootOffset.Value, groupTypeEntry.Value);
        var groupNameEntry = FindEntry(image, groupNameDirectory, 1) ?? FindFirstEntry(image, groupNameDirectory);
        if (groupNameEntry is null)
        {
            return null;
        }

        var groupLanguageDirectory = GetDirectoryOffset(image, resourceRootOffset.Value, groupNameEntry.Value);
        var groupLanguageEntry = FindFirstEntry(image, groupLanguageDirectory);
        if (groupLanguageEntry is null)
        {
            return null;
        }

        var groupResource = ReadResourceData(image, resourceRootOffset.Value, groupLanguageEntry.Value);
        var groupData = groupResource.Data;
        if (groupData.Length < 6 || ReadUInt16(groupData, 0) != 0 || ReadUInt16(groupData, 2) != IcoType)
        {
            throw new InvalidDataException($"The executable contains an invalid group icon resource: '{path}'.");
        }

        var count = ReadUInt16(groupData, 4);
        var directorySize = checked(6 + count * 14);
        EnsureRange(groupData, 0, directorySize, "group icon directory");

        var icons = new List<IconImage>(count);
        var iconNameDirectory = GetDirectoryOffset(image, resourceRootOffset.Value, iconTypeEntry.Value);
        for (var index = 0; index < count; index++)
        {
            var entryOffset = 6 + index * 14;
            var iconId = ReadUInt16(groupData, entryOffset + 12);
            var iconNameEntry = FindEntry(image, iconNameDirectory, iconId);
            if (iconNameEntry is null)
            {
                throw new InvalidDataException($"The executable is missing icon resource {iconId}: '{path}'.");
            }

            var iconLanguageDirectory = GetDirectoryOffset(image, resourceRootOffset.Value, iconNameEntry.Value);
            var iconLanguageEntry = FindEntry(image, iconLanguageDirectory, groupResource.LanguageId)
                ?? FindFirstEntry(image, iconLanguageDirectory);
            if (iconLanguageEntry is null)
            {
                throw new InvalidDataException($"The executable is missing the language resource for icon {iconId}: '{path}'.");
            }

            var iconResource = ReadResourceData(image, resourceRootOffset.Value, iconLanguageEntry.Value);
            icons.Add(new IconImage(
                groupData[entryOffset],
                groupData[entryOffset + 1],
                groupData[entryOffset + 2],
                groupData[entryOffset + 3],
                ReadUInt16(groupData, entryOffset + 4),
                ReadUInt16(groupData, entryOffset + 6),
                iconResource.Data));
        }

        return icons;
    }

    private static int? GetResourceRootOffset(byte[] image)
    {
        EnsureRange(image, 0, 64, "DOS header");
        if (ReadUInt16(image, 0) != DosSignature)
        {
            throw new InvalidDataException($"The executable is not a valid MZ image: '{image}'.");
        }

        var peHeaderOffset = ReadInt32(image, PeHeaderPointerOffset);
        if (peHeaderOffset < 0 || peHeaderOffset > image.Length - 4 - CoffHeaderSize)
        {
            throw new InvalidDataException("The executable contains an invalid PE header offset.");
        }

        if (ReadUInt32(image, peHeaderOffset) != PeSignature)
        {
            throw new InvalidDataException("The executable is missing a valid PE signature.");
        }

        var coffOffset = peHeaderOffset + 4;
        var sectionCount = ReadUInt16(image, coffOffset + 2);
        var optionalHeaderSize = ReadUInt16(image, coffOffset + OptionalHeaderSizeOffset);
        var optionalHeaderOffset = coffOffset + CoffHeaderSize;
        EnsureRange(image, optionalHeaderOffset, optionalHeaderSize, "PE optional header");

        var optionalHeaderMagic = ReadUInt16(image, optionalHeaderOffset);
        var dataDirectoryOffset = optionalHeaderMagic switch
        {
            Pe32Magic => optionalHeaderOffset + 96,
            Pe32PlusMagic => optionalHeaderOffset + 112,
            _ => throw new InvalidDataException("The executable uses an unsupported PE optional header format."),
        };

        var numberOfRvaAndSizesOffset = optionalHeaderMagic == Pe32Magic
            ? optionalHeaderOffset + 92
            : optionalHeaderOffset + 108;
        if (ReadUInt32(image, numberOfRvaAndSizesOffset) <= ResourceDirectoryIndex)
        {
            return null;
        }

        EnsureRange(image, dataDirectoryOffset + ResourceDirectoryIndex * 8, 8, "PE resource data directory");
        var resourceRva = ReadUInt32(image, dataDirectoryOffset + ResourceDirectoryIndex * 8);
        if (resourceRva == 0)
        {
            return null;
        }

        var sectionHeaderOffset = optionalHeaderOffset + optionalHeaderSize;
        EnsureRange(image, sectionHeaderOffset, checked(sectionCount * 40), "PE section headers");
        return RvaToFileOffset(image, sectionHeaderOffset, sectionCount, resourceRva);
    }

    private static int RvaToFileOffset(byte[] image, int sectionHeaderOffset, ushort sectionCount, uint rva)
    {
        for (var index = 0; index < sectionCount; index++)
        {
            var sectionOffset = sectionHeaderOffset + index * 40;
            var virtualSize = ReadUInt32(image, sectionOffset + 8);
            var virtualAddress = ReadUInt32(image, sectionOffset + 12);
            var rawSize = ReadUInt32(image, sectionOffset + 16);
            var rawPointer = ReadUInt32(image, sectionOffset + 20);
            var sectionSize = Math.Max(virtualSize, rawSize);
            if (rva < virtualAddress || (ulong)rva - virtualAddress >= sectionSize)
            {
                continue;
            }

            var offset = (ulong)rawPointer + rva - virtualAddress;
            if (offset > int.MaxValue || offset > (ulong)image.Length)
            {
                throw new InvalidDataException("The PE resource points outside the file.");
            }

            var fileOffset = (int)offset;
            if ((ulong)fileOffset + Math.Min(rawSize, sectionSize - (rva - virtualAddress)) > (ulong)image.Length)
            {
                throw new InvalidDataException("The PE resource section is truncated.");
            }

            return fileOffset;
        }

        throw new InvalidDataException("The PE resource directory could not be mapped to the file.");
    }

    private static ResourceEntry? FindEntry(byte[] image, int rootOffset, uint id)
    {
        foreach (var entry in ReadEntries(image, rootOffset))
        {
            if ((entry.Name & ResourceDirectoryFlag) == 0 && (entry.Name & ResourceNameIdMask) == id)
            {
                return entry;
            }
        }

        return null;
    }

    private static ResourceEntry? FindFirstEntry(byte[] image, int directoryOffset)
    {
        foreach (var entry in ReadEntries(image, directoryOffset))
        {
            return entry;
        }

        return null;
    }

    private static IEnumerable<ResourceEntry> ReadEntries(byte[] image, int directoryOffset)
    {
        EnsureRange(image, directoryOffset, ResourceDirectoryHeaderSize, "resource directory header");
        var namedCount = ReadUInt16(image, directoryOffset + 12);
        var idCount = ReadUInt16(image, directoryOffset + 14);
        var count = checked(namedCount + idCount);
        var entriesOffset = checked(directoryOffset + ResourceDirectoryHeaderSize);
        EnsureRange(image, entriesOffset, checked(count * ResourceDirectoryEntrySize), "resource directory entries");

        for (var index = 0; index < count; index++)
        {
            var entryOffset = entriesOffset + index * ResourceDirectoryEntrySize;
            yield return new ResourceEntry(ReadUInt32(image, entryOffset), ReadUInt32(image, entryOffset + 4));
        }
    }

    private static int GetDirectoryOffset(byte[] image, int rootOffset, ResourceEntry entry)
    {
        if ((entry.Offset & ResourceDirectoryFlag) == 0)
        {
            throw new InvalidDataException("The PE resource directory has an invalid child entry.");
        }

        var offset = checked((long)rootOffset + (entry.Offset & ResourceDirectoryOffsetMask));
        if (offset > int.MaxValue)
        {
            throw new InvalidDataException("The PE resource directory offset is too large.");
        }

        EnsureRange(image, (int)offset, ResourceDirectoryHeaderSize, "resource directory");
        return (int)offset;
    }

    private static ResourceData ReadResourceData(byte[] image, int rootOffset, ResourceEntry entry)
    {
        if ((entry.Offset & ResourceDirectoryFlag) != 0)
        {
            throw new InvalidDataException("The PE resource data entry is unexpectedly a directory.");
        }

        var dataEntryOffset = checked((long)rootOffset + (entry.Offset & ResourceDirectoryOffsetMask));
        if (dataEntryOffset > int.MaxValue)
        {
            throw new InvalidDataException("The PE resource data offset is too large.");
        }

        EnsureRange(image, (int)dataEntryOffset, ResourceDataEntrySize, "resource data entry");
        var dataRva = ReadUInt32(image, (int)dataEntryOffset);
        var dataSize = ReadUInt32(image, (int)dataEntryOffset + 4);
        var dataOffset = FindRvaInImage(image, dataRva);
        if (dataSize > image.Length - dataOffset)
        {
            throw new InvalidDataException("The PE resource data is truncated.");
        }

        var languageId = entry.Name & ResourceNameIdMask;
        return new ResourceData(languageId, image.AsSpan(dataOffset, checked((int)dataSize)).ToArray());
    }

    private static int FindRvaInImage(byte[] image, uint rva)
    {
        // Resource data is always in the resource section. Locate the containing
        // section from the PE headers again so data entries can be copied exactly.
        var peHeaderOffset = ReadInt32(image, PeHeaderPointerOffset);
        var coffOffset = peHeaderOffset + 4;
        var sectionCount = ReadUInt16(image, coffOffset + 2);
        var optionalHeaderSize = ReadUInt16(image, coffOffset + OptionalHeaderSizeOffset);
        var sectionHeaderOffset = coffOffset + CoffHeaderSize + optionalHeaderSize;
        return RvaToFileOffset(image, sectionHeaderOffset, sectionCount, rva);
    }

    private static void UpdateIconResources(string destinationPath, IReadOnlyList<IconImage> icons)
    {
        var updateHandle = BeginUpdateResource(destinationPath, deleteExistingResources: false);
        if (updateHandle == IntPtr.Zero)
        {
            throw new Win32Exception(Marshal.GetLastWin32Error(), $"Failed to open '{destinationPath}' for icon resource updates.");
        }

        var commit = false;
        try
        {
            for (var index = 0; index < icons.Count; index++)
            {
                var icon = icons[index];
                if (!UpdateResource(
                        updateHandle,
                        new IntPtr(ResourceTypeIcon),
                        new IntPtr(index + 1),
                        NeutralLanguage,
                        icon.Data,
                        checked((uint)icon.Data.Length)))
                {
                    throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to update an RT_ICON resource.");
                }
            }

            var groupData = BuildGroupIconData(icons);
            if (!UpdateResource(
                    updateHandle,
                    new IntPtr(ResourceTypeGroupIcon),
                    new IntPtr(1),
                    NeutralLanguage,
                    groupData,
                    checked((uint)groupData.Length)))
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to update the RT_GROUP_ICON resource.");
            }

            commit = true;
        }
        finally
        {
            if (!EndUpdateResource(updateHandle, discard: !commit) && commit)
            {
                throw new Win32Exception(Marshal.GetLastWin32Error(), "Failed to commit icon resource updates.");
            }
        }
    }

    private static byte[] BuildGroupIconData(IReadOnlyList<IconImage> icons)
    {
        var data = new byte[checked(6 + icons.Count * 14)];
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(0, 2), 0);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(2, 2), IcoType);
        BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(4, 2), checked((ushort)icons.Count));

        for (var index = 0; index < icons.Count; index++)
        {
            var icon = icons[index];
            var entryOffset = 6 + index * 14;
            data[entryOffset] = icon.Width;
            data[entryOffset + 1] = icon.Height;
            data[entryOffset + 2] = icon.ColorCount;
            data[entryOffset + 3] = icon.Reserved;
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(entryOffset + 4, 2), icon.Planes);
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(entryOffset + 6, 2), icon.BitCount);
            BinaryPrimitives.WriteUInt32LittleEndian(data.AsSpan(entryOffset + 8, 4), checked((uint)icon.Data.Length));
            BinaryPrimitives.WriteUInt16LittleEndian(data.AsSpan(entryOffset + 12, 2), checked((ushort)(index + 1)));
        }

        return data;
    }

    private static ushort ReadUInt16(byte[] data, int offset)
    {
        EnsureRange(data, offset, sizeof(ushort), "binary data");
        return BinaryPrimitives.ReadUInt16LittleEndian(data.AsSpan(offset, sizeof(ushort)));
    }

    private static uint ReadUInt32(byte[] data, int offset)
    {
        EnsureRange(data, offset, sizeof(uint), "binary data");
        return BinaryPrimitives.ReadUInt32LittleEndian(data.AsSpan(offset, sizeof(uint)));
    }

    private static int ReadInt32(byte[] data, int offset)
    {
        EnsureRange(data, offset, sizeof(int), "binary data");
        return BinaryPrimitives.ReadInt32LittleEndian(data.AsSpan(offset, sizeof(int)));
    }

    private static void EnsureRange(byte[] data, int offset, int length, string description)
    {
        if (offset < 0 || length < 0 || offset > data.Length - length)
        {
            throw new InvalidDataException($"The {description} is outside the file.");
        }
    }

    private readonly record struct ResourceEntry(uint Name, uint Offset);

    private readonly record struct ResourceData(uint LanguageId, byte[] Data);

    private sealed record IconImage(
        byte Width,
        byte Height,
        byte ColorCount,
        byte Reserved,
        ushort Planes,
        ushort BitCount,
        byte[] Data);

    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    private static extern IntPtr BeginUpdateResource(string fileName, [MarshalAs(UnmanagedType.Bool)] bool deleteExistingResources);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool UpdateResource(
        IntPtr updateHandle,
        IntPtr type,
        IntPtr name,
        ushort language,
        byte[] data,
        uint dataSize);

    [DllImport("kernel32.dll", SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    private static extern bool EndUpdateResource(IntPtr updateHandle, [MarshalAs(UnmanagedType.Bool)] bool discard);
}
