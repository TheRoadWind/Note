using System.Text.RegularExpressions;

namespace Note.Services;

/// <summary>便签内嵌图片：正文用 [img:文件名] 标记，图片文件存于应用私有目录。</summary>
public static class NoteImages
{
    private static readonly Regex MarkerRegex = new(@"\[img:([^\]\r\n]+)\]");

    public static string Directory => Path.Combine(FileSystem.AppDataDirectory, "note_images");

    public static string Marker(string fileName) => $"[img:{fileName}]";

    /// <summary>把图片标记换成可读文字，用于列表预览与剪贴板。</summary>
    public static string StripMarkers(string? text)
        => string.IsNullOrEmpty(text) ? string.Empty : MarkerRegex.Replace(text, "［图片］");

    public static IEnumerable<string> FileNames(string? text)
    {
        if (string.IsNullOrEmpty(text)) yield break;
        foreach (Match match in MarkerRegex.Matches(text))
        {
            var name = Path.GetFileName(match.Groups[1].Value);
            if (!string.IsNullOrWhiteSpace(name)) yield return name;
        }
    }

    public static IEnumerable<string> Paths(string? text)
    {
        foreach (var name in FileNames(text))
            yield return Path.Combine(Directory, name);
    }

    /// <summary>删除便签时一并清理它引用的图片文件。</summary>
    public static void DeleteFor(string? text)
    {
        foreach (var path in Paths(text).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            try
            {
                if (File.Exists(path)) File.Delete(path);
            }
            catch
            {
                // 图片清理失败不影响删除便签
            }
        }
    }
}
