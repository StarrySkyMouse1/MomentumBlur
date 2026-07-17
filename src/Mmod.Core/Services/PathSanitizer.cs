using System.IO;
using System.Text;

namespace Mmod.Core.Services;

public static class PathSanitizer
{
    public static string Clean(string? path)
    {
        if (string.IsNullOrEmpty(path))
            return string.Empty;

        var builder = new StringBuilder(path.Length);
        foreach (var ch in path)
        {
            if (ch != '\0')
                builder.Append(ch);
        }

        return builder.ToString().Trim();
    }

    public static string GetFullPath(string? path)
    {
        var clean = Clean(path);
        if (string.IsNullOrWhiteSpace(clean))
            throw new ArgumentException("路径不能为空。", nameof(path));

        return Path.GetFullPath(clean);
    }
}
