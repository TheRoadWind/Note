using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Note.Models;

namespace Note.Services;

/// <summary>浏览记录与阅读位置的本地持久化。按内容指纹去重，同一份文档只保留一条记录。</summary>
public sealed class HistoryStore
{
    private static readonly Lazy<HistoryStore> _instance = new(() => new HistoryStore());
    public static HistoryStore Instance => _instance.Value;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly string _dataFile = Path.Combine(FileSystem.AppDataDirectory, "history.json");
    private bool _loaded;

    public List<DocumentRecord> Records { get; private set; } = new();

    /// <summary>用文件路径生成初始 ID（内容指纹才是最终的去重依据）。</summary>
    public static string IdOf(string path)
        => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(Path.GetFullPath(path))))[..16].ToLowerInvariant();

    private void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        try
        {
            if (File.Exists(_dataFile))
                Records = JsonSerializer.Deserialize<List<DocumentRecord>>(File.ReadAllText(_dataFile), JsonOptions)
                          ?? new List<DocumentRecord>();
        }
        catch
        {
            Records = new List<DocumentRecord>();
        }

        // 兼容旧数据：同一内容指纹只保留最近的一条，历史残留的重复记录会被合并
        var before = Records.Count;
        Records = Records
            .GroupBy(r => string.IsNullOrEmpty(r.ContentId) ? "id:" + r.Id : "c:" + r.ContentId)
            .Select(g => g.OrderByDescending(r => r.LastReadAt).First())
            .OrderByDescending(r => r.LastReadAt)
            .ToList();
        if (Records.Count != before) Save();
    }

    public void Load()
    {
        _loaded = false;
        EnsureLoaded();
    }

    public IReadOnlyList<DocumentRecord> GetAll()
    {
        EnsureLoaded();
        return Records.OrderByDescending(r => r.LastReadAt).ToList();
    }

    public DocumentRecord? Find(string? id)
    {
        if (string.IsNullOrEmpty(id)) return null;
        EnsureLoaded();
        return Records.FirstOrDefault(r => r.Id == id);
    }

    /// <summary>
    /// 打开文档时登记记录：优先按内容指纹匹配，老记录会补算指纹并合并去重。
    /// 返回实际使用的记录 ID，供阅读页保存位置。
    /// </summary>
    public async Task<string> RecordOpenAsync(ImportedDocument doc)
    {
        EnsureLoaded();
        var size = SafeSize(doc.SourcePath);
        var contentId = doc.ContentId;

        var candidates = Records.Where(r =>
            (!string.IsNullOrEmpty(contentId) && !string.IsNullOrEmpty(r.ContentId) && r.ContentId == contentId) ||
            (string.IsNullOrEmpty(r.ContentId) && r.FileName == doc.FileName && r.FileSize == size && size > 0)
        ).ToList();

        foreach (var legacy in candidates.Where(c => string.IsNullOrEmpty(c.ContentId) && File.Exists(c.FilePath)))
        {
            try
            {
                var id = await DocumentReader.GetContentIdAsync(legacy.FilePath);
                DocumentReader.RememberFingerprint(legacy.FilePath, id);
                legacy.ContentId = id;
            }
            catch
            {
                // 文件已不可读时跳过
            }
        }

        var matches = !string.IsNullOrEmpty(contentId)
            ? candidates.Where(c => c.ContentId == contentId).ToList()
            : new List<DocumentRecord>();

        var record = matches.OrderByDescending(r => r.LastReadAt).FirstOrDefault()
                     ?? Records.FirstOrDefault(r => r.Id == doc.RecordId);

        if (record is null)
        {
            record = new DocumentRecord { Id = doc.RecordId };
            Records.Add(record);
        }

        foreach (var duplicate in matches.Where(m => !ReferenceEquals(m, record)))
            Records.Remove(duplicate);

        record.ContentId = contentId;
        record.FilePath = doc.SourcePath;
        record.FileName = doc.FileName;
        record.FileSize = size;
        record.ChapterCount = doc.Chapters.Count;
        record.PageCount = Math.Max(1, doc.Pages.Count);
        record.LastReadAt = DateTime.Now;

        if (record.LastPageIndex >= record.PageCount)
        {
            record.LastPageIndex = 0;
            record.ScrollRatio = 0;
        }

        Save();
        return record.Id;
    }

    public void UpdatePosition(string id, int pageIndex, double scrollRatio, string chapterTitle, int pageCount)
    {
        EnsureLoaded();
        var record = Records.FirstOrDefault(r => r.Id == id);
        if (record is null) return;

        record.LastPageIndex = Math.Clamp(pageIndex, 0, Math.Max(0, pageCount - 1));
        record.ScrollRatio = Math.Clamp(scrollRatio, 0, 1);
        record.LastChapterTitle = chapterTitle ?? string.Empty;
        record.PageCount = Math.Max(1, pageCount);
        record.LastReadAt = DateTime.Now;
        Save();
    }

    public void Delete(DocumentRecord record)
    {
        EnsureLoaded();
        Records.RemoveAll(r => r.Id == record.Id);
        Save();
    }

    public void Clear()
    {
        EnsureLoaded();
        Records.Clear();
        Save();
    }

    private static long SafeSize(string path)
    {
        try { return new FileInfo(path).Length; }
        catch { return 0; }
    }

    private void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(Records, JsonOptions);
            var tmp = _dataFile + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _dataFile, true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"保存浏览记录失败: {ex}");
        }
    }
}
