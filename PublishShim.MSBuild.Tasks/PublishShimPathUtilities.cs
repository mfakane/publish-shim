namespace PublishShim.MSBuild.Tasks;

internal static class PublishShimPathUtilities
{
    public static string NormalizeFileName(string value, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{parameterName} must be provided.");
        }

        var trimmed = value.Trim();
        if (Path.IsPathRooted(trimmed) || trimmed.Contains('/') || trimmed.Contains('\\'))
        {
            throw new InvalidOperationException($"{parameterName} must be a file name: '{value}'.");
        }

        if (trimmed.IndexOfAny(Path.GetInvalidFileNameChars()) >= 0)
        {
            throw new InvalidOperationException($"{parameterName} contains invalid path characters: '{value}'.");
        }

        return trimmed;
    }

    public static string NormalizeRelativePath(string value, string publishDirectory, string parameterName)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new InvalidOperationException($"{parameterName} must be provided.");
        }

        var trimmed = value.Trim().Replace(Path.AltDirectorySeparatorChar, Path.DirectorySeparatorChar);
        if (Path.IsPathRooted(trimmed))
        {
            throw new InvalidOperationException($"{parameterName} must be relative to the publish root: '{value}'.");
        }

        var combined = Path.GetFullPath(Path.Combine(publishDirectory, trimmed));
        var publishRoot = AppendDirectorySeparator(Path.GetFullPath(publishDirectory));
        if (!combined.StartsWith(publishRoot, StringComparison.OrdinalIgnoreCase))
        {
            throw new InvalidOperationException($"{parameterName} escapes the publish root: '{value}'.");
        }

        var relative = GetRelativePath(publishDirectory, combined);
        if (relative is "." or "")
        {
            throw new InvalidOperationException($"{parameterName} must not point to the publish root.");
        }

        return relative;
    }

    public static bool PathEquals(string left, string right)
    {
        return string.Equals(
            TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.OrdinalIgnoreCase);
    }

    public static string GetRelativePath(string relativeTo, string path)
    {
        var relativeToFullPath = Path.GetFullPath(relativeTo);
        var pathFullPath = Path.GetFullPath(path);

        if (string.Equals(relativeToFullPath, pathFullPath, StringComparison.OrdinalIgnoreCase))
        {
            return ".";
        }

        var relativeToRoot = Path.GetPathRoot(relativeToFullPath);
        var pathRoot = Path.GetPathRoot(pathFullPath);
        if (!string.Equals(relativeToRoot, pathRoot, StringComparison.OrdinalIgnoreCase))
        {
            return pathFullPath;
        }

        var relativeToUri = new Uri(AppendDirectorySeparator(relativeToFullPath));
        var pathUri = new Uri(pathFullPath);
        var relativeUri = relativeToUri.MakeRelativeUri(pathUri);
        return Uri.UnescapeDataString(relativeUri.ToString())
            .Replace('/', Path.DirectorySeparatorChar);
    }

    public static string TrimEndingDirectorySeparator(string path)
    {
        if (string.IsNullOrEmpty(path))
        {
            return path;
        }

        var root = Path.GetPathRoot(path) ?? string.Empty;
        if (path.Length > root.Length && IsDirectorySeparator(path[path.Length - 1]))
        {
            return path.Substring(0, path.Length - 1);
        }

        return path;
    }

    private static string AppendDirectorySeparator(string path)
    {
        return path.EndsWith(Path.DirectorySeparatorChar.ToString(), StringComparison.Ordinal)
            || path.EndsWith(Path.AltDirectorySeparatorChar.ToString(), StringComparison.Ordinal)
            ? path
            : path + Path.DirectorySeparatorChar;
    }

    private static bool IsDirectorySeparator(char value)
    {
        return value == Path.DirectorySeparatorChar || value == Path.AltDirectorySeparatorChar;
    }
}
