namespace Csaver.Core;

public static class ReparsePointPaths
{
    public static string Normalize(string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath)) return string.Empty;
        var parts = relativePath.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        return string.Join('\\', parts);
    }

    public static bool TryNormalizeDeclaration(string value, out string normalized)
    {
        normalized = string.Empty;
        if (string.IsNullOrWhiteSpace(value)) return false;
        var text = value.Trim();
        if (text.IndexOf('\0') >= 0 || text.IndexOf(':') >= 0 || text.IndexOfAny(['*', '?']) >= 0) return false;
        if (Path.IsPathRooted(text)) return false;
        var parts = text.Replace('/', '\\').Split('\\', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length == 0 || parts.Any(part => part is "." or "..")) return false;
        normalized = string.Join('\\', parts);
        return true;
    }
}
