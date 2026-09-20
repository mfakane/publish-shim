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

        var relative = Path.GetRelativePath(publishDirectory, combined);
        if (relative is "." or "")
        {
            throw new InvalidOperationException($"{parameterName} must not point to the publish root.");
        }

        return relative;
    }

    public static bool PathEquals(string left, string right)
    {
        return string.Equals(
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(left)),
            Path.TrimEndingDirectorySeparator(Path.GetFullPath(right)),
            StringComparison.OrdinalIgnoreCase);
    }

    private static string AppendDirectorySeparator(string path)
    {
        return path.EndsWith(Path.DirectorySeparatorChar) || path.EndsWith(Path.AltDirectorySeparatorChar)
            ? path
            : path + Path.DirectorySeparatorChar;
    }
}
