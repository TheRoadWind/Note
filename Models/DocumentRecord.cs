using System.Text.Json.Serialization;

namespace Note.Models;

/// <summary>一条文档浏览记录，同时保存上次阅读位置。</summary>
public sealed class DocumentRecord
{
    public string Id { get; set; } = string.Empty;

    /// <summary>内容指纹，用于把不同路径的同一份文档合并成一条记录。</summary>
    public string ContentId { get; set; } = string.Empty;

    public string FilePath { get; set; } = string.Empty;
    public string FileName { get; set; } = string.Empty;
    public long FileSize { get; set; }
    public int ChapterCount { get; set; }
    public int PageCount { get; set; }
    public int LastPageIndex { get; set; }
    public double ScrollRatio { get; set; }
    public string LastChapterTitle { get; set; } = string.Empty;
    public DateTime LastReadAt { get; set; } = DateTime.Now;

    [JsonIgnore]
    public bool IsMissing => !File.Exists(FilePath);

    [JsonIgnore]
    public string PathText => FilePath;

    [JsonIgnore]
    public string SizeText => FormatSize(FileSize);

    [JsonIgnore]
    public string ChapterText => ChapterCount > 1 ? $"{ChapterCount} 章" : "全文";

    [JsonIgnore]
    public string TimeText => FormatTime(LastReadAt);

    [JsonIgnore]
    public string DetailText => $"{SizeText} · {ChapterText} · {TimeText}";

    [JsonIgnore]
    public string PositionText
    {
        get
        {
            if (IsMissing) return "原文件已不存在，点击可清理记录";
            var position = PageCount > 1
                ? $"第 {Math.Min(LastPageIndex + 1, PageCount)}/{PageCount} 页"
                : "第 1 页";
            if (!string.IsNullOrWhiteSpace(LastChapterTitle))
                position += $" · {LastChapterTitle}";
            if (ScrollRatio > 0.01)
                position += $" · {ScrollRatio:P0}";
            return $"上次读到 {position}";
        }
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / 1024.0 / 1024.0:F1} MB";
    }

    private static string FormatTime(DateTime time)
    {
        var local = time.ToLocalTime();
        var today = DateTime.Today;
        if (local.Date == today) return $"今天 {local:HH:mm}";
        if (local.Date == today.AddDays(-1)) return $"昨天 {local:HH:mm}";
        if (local.Year == today.Year) return $"{local:M月d日 HH:mm}";
        return local.ToString("yyyy/M/d HH:mm");
    }
}
