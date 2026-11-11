using System.IO.Compression;
using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace Note.Services;

/// <summary>导入文件的解析结果。正文按章节写入本地预览文件，由 WebView 直接加载。</summary>
public sealed class ImportedDocument
{
    public string FileName { get; set; } = string.Empty;
    public string Title { get; set; } = string.Empty;

    /// <summary>用于「存为便签」的文本（已限长）。</summary>
    public string Text { get; set; } = string.Empty;

    public string? PreviewPath { get; set; }

    /// <summary>按章节切分后的全部页面路径。</summary>
    public List<string> Pages { get; set; } = new();

    /// <summary>自动生成的目录。</summary>
    public List<ChapterInfo> Chapters { get; set; } = new();

    public string SourcePath { get; set; } = string.Empty;
    public string RecordId { get; set; } = string.Empty;

    /// <summary>文件内容指纹：同一份文档无论从哪个路径打开都一致。</summary>
    public string ContentId { get; set; } = string.Empty;

    public string CacheKey { get; set; } = string.Empty;

    /// <summary>HTML 预览模板版本，模板升级后旧缓存自动重建。</summary>
    public int Schema { get; set; }

    public long TotalChars { get; set; }
    public string? Warning { get; set; }

    [JsonIgnore]
    public bool UseWebView => Pages.Count > 0 || !string.IsNullOrEmpty(PreviewPath);
}

/// <summary>支持 TXT / HTML / DOC / DOCX 的本地文件读取，采用流式处理以兼容大文件。</summary>
public static class DocumentReader
{
    public const int MaxNoteChars = 100_000;

    private const int ChunkSize = 8192;
    private const int MaxLineChars = 16_384;

    /// <summary>预览 HTML 模板版本；改动交互脚本时递增，旧缓存会自动失效。</summary>
    private const int PreviewSchema = 2;

    /// <summary>单个章节过长时的兜底拆页阈值（正常章节整章一页，不按固定字符数截断）。</summary>
    private const long MaxPageChars = 1_500_000;

    private static readonly JsonSerializerOptions MetaOptions = new()
    {
        WriteIndented = false,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static readonly object FingerprintLock = new();
    private static Dictionary<string, FingerprintEntry> _fingerprints = new();
    private static bool _fingerprintsLoaded;

    private static string CacheDir => Path.Combine(FileSystem.CacheDirectory, "previews");
    private static string DocCopyDir => Path.Combine(FileSystem.CacheDirectory, "docs");
    private static string FingerprintFile => Path.Combine(FileSystem.AppDataDirectory, "fingerprints.json");

    private sealed class FingerprintEntry
    {
        public long Size { get; set; }
        public long Ticks { get; set; }
        public string ContentId { get; set; } = string.Empty;
    }

    public static FilePickerFileType PickTypes { get; } = new(new Dictionary<DevicePlatform, IEnumerable<string>>
    {
        [DevicePlatform.Android] = new[]
        {
            "text/plain",
            "text/html",
            "application/msword",
            "application/vnd.openxmlformats-officedocument.wordprocessingml.document"
        },
        [DevicePlatform.WinUI] = new[] { ".txt", ".html", ".htm", ".doc", ".docx" },
        [DevicePlatform.iOS] = new[] { "public.plain-text", "public.html", "com.microsoft.word.doc", "org.openxmlformats.wordprocessingml.document" },
        [DevicePlatform.MacCatalyst] = new[] { "public.plain-text", "public.html", "com.microsoft.word.doc", "org.openxmlformats.wordprocessingml.document" }
    });

    public static Task<ImportedDocument> ReadAsync(FileResult file)
        => ReadAsync(file, null);

    public static async Task<ImportedDocument> ReadAsync(FileResult file, IProgress<double>? progress)
    {
        var path = file.FullPath;
        if (string.IsNullOrWhiteSpace(path) || !File.Exists(path))
        {
            using var stream = await file.OpenReadAsync();
            path = await CopyToCacheAsync(stream, file.FileName);
        }
        else
        {
            // 系统文件选择器常给出随机临时路径，统一规范成按内容哈希命名的副本
            path = await NormalizeTransientAsync(path, file.FileName, progress);
        }

        return await ReadAsync(path, file.FileName, progress);
    }

    public static Task<ImportedDocument> ReadAsync(string path)
        => ReadAsync(path, (IProgress<double>?)null);

    public static Task<ImportedDocument> ReadAsync(string path, IProgress<double>? progress)
        => ReadAsync(path, Path.GetFileName(path), progress);

    public static async Task<ImportedDocument> ReadAsync(string path, string fileName, IProgress<double>? progress)
    {
        var full = Path.GetFullPath(path);
        var info = new FileInfo(full);
        var ext = Path.GetExtension(fileName).ToLowerInvariant();
        var title = Path.GetFileNameWithoutExtension(fileName);
        if (string.IsNullOrWhiteSpace(title)) title = "导入的文件";

        var recordId = HistoryStore.IdOf(full);

        try
        {
            // 身份与缓存都以「内容指纹」为准：不同路径的同一份文档共用一套预览
            var contentId = TryGetFingerprint(full, info) ?? await ComputeContentIdAsync(full, progress);
            RememberFingerprint(full, contentId);

            CleanupCache(contentId);
            CleanupDocCopies();

            var metaPath = MetaPath(contentId);
            if (File.Exists(metaPath))
            {
                try
                {
                    var cached = JsonSerializer.Deserialize<ImportedDocument>(File.ReadAllText(metaPath), MetaOptions);
                    if (cached is not null && cached.Schema == PreviewSchema && cached.Pages.Count > 0 &&
                        cached.Pages.All(p => File.Exists(p) && new FileInfo(p).Length > 0))
                    {
                        cached.SourcePath = full;
                        cached.FileName = fileName;
                        cached.RecordId = recordId;
                        cached.ContentId = contentId;
                        cached.CacheKey = contentId;
                        progress?.Report(1.0);
                        return cached;
                    }
                }
                catch
                {
                    // 缓存损坏则重建
                }
            }

            // 重建前清掉该文档可能残留的半份缓存
            CleanupCache(null);

            ImportedDocument doc = ext switch
            {
                ".html" or ".htm" => await ReadHtmlAsync(full, fileName, title, contentId, progress),
                ".docx" => await ReadDocxAsync(full, fileName, title, contentId, progress),
                ".doc" => await ReadLegacyDocAsync(full, fileName, title, contentId, progress),
                _ => await ReadTextAsync(full, fileName, title, contentId, progress)
            };

            doc.SourcePath = full;
            doc.RecordId = recordId;
            doc.ContentId = contentId;
            doc.CacheKey = contentId;
            doc.Schema = PreviewSchema;
            File.WriteAllText(metaPath, JsonSerializer.Serialize(doc, MetaOptions));
            progress?.Report(1.0);
            return doc;
        }
        catch (Exception ex)
        {
            progress?.Report(1.0);
            return new ImportedDocument
            {
                FileName = fileName,
                Title = title,
                SourcePath = full,
                RecordId = recordId,
                Warning = $"打开失败：{ex.Message}"
            };
        }
    }

    /// <summary>计算文件内容指纹（SHA256 前 16 位），流式读取以便报告进度。</summary>
    public static Task<string> GetContentIdAsync(string path) => ComputeContentIdAsync(path, null);

    private static async Task<string> ComputeContentIdAsync(string path, IProgress<double>? progress)
    {
        using var hash = IncrementalHash.CreateHash(HashAlgorithmName.SHA256);
        using var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 64 * 1024, true);
        var total = Math.Max(1L, fs.Length);
        var buffer = new byte[64 * 1024];
        long read = 0;
        int n;

        while ((n = await fs.ReadAsync(buffer, 0, buffer.Length)) > 0)
        {
            hash.AppendData(buffer, 0, n);
            read += n;
            progress?.Report(Math.Min(0.12, 0.12 * read / total));
        }

        return Convert.ToHexString(hash.GetHashAndReset())[..16].ToLowerInvariant();
    }

    /// <summary>把「路径 -> 指纹」缓存起来，重复打开同一路径时无需再读一遍文件。</summary>
    public static void RememberFingerprint(string path, string contentId)
    {
        try
        {
            var full = Path.GetFullPath(path);
            var info = new FileInfo(full);
            lock (FingerprintLock)
            {
                EnsureFingerprintsLoaded();
                _fingerprints[full] = new FingerprintEntry
                {
                    Size = info.Length,
                    Ticks = info.LastWriteTimeUtc.Ticks,
                    ContentId = contentId
                };
                SaveFingerprints();
            }
        }
        catch
        {
            // 指纹缓存失败不影响打开
        }
    }

    private static string? TryGetFingerprint(string path, FileInfo info)
    {
        lock (FingerprintLock)
        {
            EnsureFingerprintsLoaded();
            if (_fingerprints.TryGetValue(path, out var entry) &&
                entry.Size == info.Length && entry.Ticks == info.LastWriteTimeUtc.Ticks &&
                !string.IsNullOrEmpty(entry.ContentId))
                return entry.ContentId;
        }
        return null;
    }

    private static void EnsureFingerprintsLoaded()
    {
        if (_fingerprintsLoaded) return;
        _fingerprintsLoaded = true;
        try
        {
            if (File.Exists(FingerprintFile))
                _fingerprints = JsonSerializer.Deserialize<Dictionary<string, FingerprintEntry>>(
                                    File.ReadAllText(FingerprintFile)) ?? new Dictionary<string, FingerprintEntry>();
        }
        catch
        {
            _fingerprints = new Dictionary<string, FingerprintEntry>();
        }
    }

    private static void SaveFingerprints()
    {
        try
        {
            if (_fingerprints.Count > 200)
                _fingerprints = _fingerprints
                    .OrderByDescending(p => p.Value.Ticks)
                    .Take(200)
                    .ToDictionary(p => p.Key, p => p.Value);

            var tmp = FingerprintFile + ".tmp";
            File.WriteAllText(tmp, JsonSerializer.Serialize(_fingerprints));
            File.Move(tmp, FingerprintFile, true);
        }
        catch
        {
            // ignore
        }
    }

    /// <summary>
    /// 把应用缓存目录里的临时副本规范化为 c_&lt;内容指纹&gt; 命名；
    /// 同名内容直接复用，避免每次「外部打开/文件选择器」都多留一份副本。
    /// </summary>
    public static async Task<string> NormalizeTransientAsync(
        string path,
        string? displayName = null,
        IProgress<double>? progress = null)
    {
        var full = Path.GetFullPath(path);
        var name = Path.GetFileName(full);

        if (name.StartsWith("c_", StringComparison.Ordinal) && name.Length >= 18 &&
            name.Substring(2, 16).All(Uri.IsHexDigit))
        {
            var knownId = name.Substring(2, 16).ToLowerInvariant();
            RememberFingerprint(full, knownId);
            return full;
        }

        var cacheRoot = Path.GetFullPath(FileSystem.CacheDirectory);
        if (!full.StartsWith(cacheRoot, StringComparison.OrdinalIgnoreCase)) return full;

        var id = await ComputeContentIdAsync(full, progress);
        var ext = Path.GetExtension(displayName ?? full).ToLowerInvariant();
        Directory.CreateDirectory(DocCopyDir);
        var target = Path.Combine(DocCopyDir, "c_" + id + ext);

        if (string.Equals(full, target, StringComparison.OrdinalIgnoreCase))
        {
            RememberFingerprint(full, id);
            return full;
        }

        try
        {
            if (File.Exists(target)) TryDelete(full);
            else File.Move(full, target, true);
            RememberFingerprint(target, id);
            return target;
        }
        catch
        {
            // 无法移动时仍用原路径，指纹表会在读取时补上
            RememberFingerprint(full, id);
            return full;
        }
    }

    private static async Task<ImportedDocument> ReadTextAsync(
        string path, string fileName, string title, string key, IProgress<double>? progress)
    {
        var preview = new PreviewBuilder(key);
        using var stream = new ProgressStream(File.OpenRead(path), new FileInfo(path).Length, progress);
        using var reader = new StreamReader(stream, DetectEncoding(path), detectEncodingFromByteOrderMarks: true);
        var buffer = new char[ChunkSize];
        int n;
        while ((n = await reader.ReadAsync(buffer, 0, buffer.Length)) > 0)
            preview.Append(new string(buffer, 0, n));

        return BuildDoc(fileName, title, preview.Finish(), null);
    }

    private static async Task<ImportedDocument> ReadHtmlAsync(
        string path, string fileName, string title, string key, IProgress<double>? progress)
    {
        var preview = new PreviewBuilder(key);
        var converter = new HtmlToText(preview);

        using var stream = new ProgressStream(File.OpenRead(path), new FileInfo(path).Length, progress);
        using var reader = new StreamReader(stream, DetectEncoding(path), detectEncodingFromByteOrderMarks: true);
        var buffer = new char[ChunkSize];
        int n;
        while ((n = await reader.ReadAsync(buffer, 0, buffer.Length)) > 0)
            converter.Append(new string(buffer, 0, n));
        converter.Finish();

        var result = preview.Finish();
        return BuildDoc(fileName, title, result,
            result.TotalChars == 0 ? "该 HTML 文件未提取到可显示文字。" : null);
    }

    private static async Task<ImportedDocument> ReadDocxAsync(
        string path, string fileName, string title, string key, IProgress<double>? progress)
    {
        var preview = new PreviewBuilder(key);

        using (var fs = new ProgressStream(File.OpenRead(path), new FileInfo(path).Length, progress))
        using (var zip = new ZipArchive(fs, ZipArchiveMode.Read))
        {
            var entry = zip.GetEntry("word/document.xml")
                        ?? zip.Entries.FirstOrDefault(e => e.FullName.EndsWith("document.xml", StringComparison.OrdinalIgnoreCase));

            if (entry is not null)
            {
                using var stream = entry.Open();
                using var reader = new StreamReader(stream, Encoding.UTF8, true);
                var buffer = new char[ChunkSize];
                var text = new StringBuilder();
                var tag = new StringBuilder();
                var inTag = false;
                int n;

                while ((n = await reader.ReadAsync(buffer, 0, buffer.Length)) > 0)
                {
                    for (var i = 0; i < n; i++)
                    {
                        var c = buffer[i];
                        if (inTag)
                        {
                            if (c == '>')
                            {
                                inTag = false;
                                var t = tag.ToString().Trim();
                                if (t.StartsWith("w:tab", StringComparison.OrdinalIgnoreCase)) text.Append('\t');
                                else if (t.StartsWith("w:br", StringComparison.OrdinalIgnoreCase)) text.Append('\n');
                                else if (t.StartsWith("/w:p", StringComparison.OrdinalIgnoreCase)
                                         || t.StartsWith("/w:tr", StringComparison.OrdinalIgnoreCase)) text.Append('\n');
                                tag.Clear();
                            }
                            else
                            {
                                tag.Append(c);
                            }
                        }
                        else if (c == '<')
                        {
                            inTag = true;
                            tag.Clear();
                        }
                        else
                        {
                            text.Append(c);
                        }

                        if (text.Length >= ChunkSize)
                        {
                            preview.Append(WebUtility.HtmlDecode(text.ToString()));
                            text.Clear();
                        }
                    }
                }

                if (text.Length > 0) preview.Append(WebUtility.HtmlDecode(text.ToString()));
            }
        }

        var result = preview.Finish();
        return BuildDoc(fileName, title, result,
            result.TotalChars == 0 ? "该文档可能只包含图片，未能提取文字。" : null);
    }

    private static async Task<ImportedDocument> ReadLegacyDocAsync(
        string path, string fileName, string title, string key, IProgress<double>? progress)
    {
        var preview = new PreviewBuilder(key);
        var line = new StringBuilder();
        var buffer = new byte[64 * 1024];

        using (var fs = new ProgressStream(File.OpenRead(path), new FileInfo(path).Length, progress))
        {
            int n;
            while ((n = await fs.ReadAsync(buffer, 0, buffer.Length)) > 0)
            {
                for (var i = 0; i + 1 < n; i += 2)
                {
                    var c = (char)(buffer[i] | (buffer[i + 1] << 8));
                    if (IsReadableChar(c) && c is not ('\n' or '\r'))
                    {
                        line.Append(c);
                    }
                    else
                    {
                        FlushLine(line, preview);
                    }

                    if (line.Length >= ChunkSize)
                    {
                        preview.Append(line.Append('\n').ToString());
                        line.Clear();
                    }
                }
            }
        }

        FlushLine(line, preview);
        var result = preview.Finish();
        return BuildDoc(fileName, title, result,
            "旧版 .doc 为二进制格式，文字为尽力提取，可能不完整或顺序错乱。");
    }

    private static ImportedDocument BuildDoc(
        string fileName,
        string title,
        (string Text, List<string> Pages, List<ChapterInfo> Chapters, long TotalChars) result,
        string? warning)
        => new()
        {
            FileName = fileName,
            Title = title,
            Text = result.Text,
            PreviewPath = result.Pages.Count > 0 ? result.Pages[0] : null,
            Pages = result.Pages,
            Chapters = result.Chapters,
            TotalChars = result.TotalChars,
            Warning = warning
        };

    private static void FlushLine(StringBuilder line, PreviewBuilder preview)
    {
        if (line.Length >= 2)
        {
            var s = line.ToString();
            if (s.Any(char.IsLetterOrDigit)) preview.Append(s + "\n");
        }
        line.Clear();
    }

    private static bool IsReadableChar(char c)
        => c is '\n' or '\r' or '\t'
           || (c >= 0x20 && c <= 0x7E)
           || (c >= 0x2000 && c <= 0x206F)
           || (c >= 0x3000 && c <= 0x303F)
           || (c >= 0x4E00 && c <= 0x9FFF)
           || (c >= 0xFF00 && c <= 0xFFEF);

    private static Encoding DetectEncoding(string path)
    {
        var bom = new byte[4];
        using (var fs = File.OpenRead(path))
        {
            var n = fs.Read(bom, 0, bom.Length);
            if (n >= 3 && bom[0] == 0xEF && bom[1] == 0xBB && bom[2] == 0xBF) return Encoding.UTF8;
            if (n >= 2 && bom[0] == 0xFF && bom[1] == 0xFE) return Encoding.Unicode;
            if (n >= 2 && bom[0] == 0xFE && bom[1] == 0xFF) return Encoding.BigEndianUnicode;
        }
        return Encoding.UTF8;
    }

    private static string MetaPath(string key) => Path.Combine(CacheDir, key + ".json");

    /// <summary>只保留指定内容指纹的预览缓存；keepKey 为 null 时清空全部。</summary>
    private static void CleanupCache(string? keepKey)
    {
        try
        {
            var dir = CacheDir;
            Directory.CreateDirectory(dir);
            foreach (var file in Directory.GetFiles(dir))
            {
                var name = Path.GetFileName(file);
                if (keepKey is not null &&
                    (name.StartsWith(keepKey + "_", StringComparison.Ordinal) ||
                     name.StartsWith(keepKey + ".", StringComparison.Ordinal)))
                    continue;

                TryDelete(file);
            }
        }
        catch
        {
            // 缓存清理失败不影响打开文档
        }
    }

    /// <summary>清理旧版本按文件名/GUID 复制的临时副本（docs 与 shared）。</summary>
    private static void CleanupDocCopies()
    {
        foreach (var sub in new[] { "docs", "shared" })
        {
            try
            {
                var dir = Path.Combine(FileSystem.CacheDirectory, sub);
                if (!Directory.Exists(dir)) continue;
                foreach (var file in Directory.GetFiles(dir))
                {
                    var name = Path.GetFileName(file);
                    if (name.StartsWith("c_", StringComparison.Ordinal)) continue;
                    if (name.StartsWith("tmp_", StringComparison.Ordinal) ||
                        DateTime.UtcNow - File.GetLastWriteTimeUtc(file) > TimeSpan.FromHours(6))
                        TryDelete(file);
                }
            }
            catch
            {
                // ignore
            }
        }
    }

    private static void TryDelete(string path)
    {
        try { File.Delete(path); }
        catch { /* 正在使用的文件可忽略 */ }
    }

    private static async Task<string> CopyToCacheAsync(Stream stream, string fileName)
    {
        Directory.CreateDirectory(DocCopyDir);
        var tmp = Path.Combine(DocCopyDir, "tmp_" + Guid.NewGuid().ToString("N"));

        try
        {
            using (var output = new FileStream(tmp, FileMode.Create, FileAccess.Write, FileShare.None, 64 * 1024, true))
                await stream.CopyToAsync(output, 64 * 1024);

            byte[] hash;
            using (var input = File.OpenRead(tmp))
                hash = SHA256.HashData(input);

            var id = Convert.ToHexString(hash)[..16].ToLowerInvariant();
            var target = Path.Combine(DocCopyDir, "c_" + id + Path.GetExtension(fileName).ToLowerInvariant());

            if (File.Exists(target)) File.Delete(tmp);
            else File.Move(tmp, target);

            RememberFingerprint(target, id);
            return target;
        }
        finally
        {
            if (File.Exists(tmp)) TryDelete(tmp);
        }
    }

    /// <summary>包装文件流，按已读字节报告解析进度。</summary>
    private sealed class ProgressStream : Stream
    {
        private readonly Stream _inner;
        private readonly long _total;
        private readonly IProgress<double>? _progress;
        private double _last = -1;

        public ProgressStream(Stream inner, long total, IProgress<double>? progress)
        {
            _inner = inner;
            _total = total;
            _progress = progress;
        }

        private void Report()
        {
            if (_progress is null || _total <= 0) return;
            var value = 0.12 + 0.88 * Math.Clamp((double)_inner.Position / _total, 0, 1);
            if (value <= _last + 0.002) return;
            _last = value;
            _progress.Report(value);
        }

        public override bool CanRead => _inner.CanRead;
        public override bool CanSeek => _inner.CanSeek;
        public override bool CanWrite => false;
        public override long Length => _inner.Length;
        public override long Position
        {
            get => _inner.Position;
            set => _inner.Position = value;
        }

        public override void Flush() => _inner.Flush();

        public override int Read(byte[] buffer, int offset, int count)
        {
            var n = _inner.Read(buffer, offset, count);
            Report();
            return n;
        }

        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
            => ReadAsyncCore(buffer, offset, count, cancellationToken);

        private async Task<int> ReadAsyncCore(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
        {
            var n = await _inner.ReadAsync(buffer, offset, count, cancellationToken);
            Report();
            return n;
        }

        public override long Seek(long offset, SeekOrigin origin)
        {
            var position = _inner.Seek(offset, origin);
            Report();
            return position;
        }

        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

        protected override void Dispose(bool disposing)
        {
            if (disposing) _inner.Dispose();
            base.Dispose(disposing);
        }
    }

    /// <summary>从独立成行的标题中识别章节，生成目录。</summary>
    private static class ChapterDetector
    {
        private static readonly Regex Numbered = new(
            @"^第[0-9０-９零一二三四五六七八九十百千两〇○〇\s]{1,12}[章节回卷部篇集折幕](?:[\s:：、.·\-—（(\[【].*)?$");

        private static readonly Regex Volume = new(
            @"^(?:卷|部|篇|集)\s*[0-9０-９零一二三四五六七八九十百千两〇○〇]{1,8}(?:[\s:：、.·\-—（(\[【].*)?$");

        private static readonly Regex English = new(
            @"^(?:chapter|part|volume|book|vol)\s*[.#]?\s*(?:\d+|[ivxlcdm]+)(?:[\s:.\-—].*)?$",
            RegexOptions.IgnoreCase);

        private static readonly string[] Special =
        {
            "序章", "楔子", "引子", "前言", "序言", "自序", "后记", "後記",
            "尾声", "尾聲", "番外", "附录", "附錄", "终章", "終章", "目录", "目錄", "开篇"
        };

        public static string? Detect(string line)
        {
            var s = line.Trim().Trim('\uFEFF', '\u3000');
            if (s.Length is < 2 or > 60) return null;

            if (s[0] == '#')
            {
                var t = s.TrimStart('#').Trim();
                return t.Length is >= 1 and <= 60 ? t : null;
            }

            if (Numbered.IsMatch(s) || Volume.IsMatch(s) || English.IsMatch(s)) return s;

            foreach (var word in Special)
                if (s.StartsWith(word, StringComparison.Ordinal) && s.Length <= word.Length + 20)
                    return s;

            return null;
        }
    }

    /// <summary>HTML 流式转纯文本，并把 h1~h6 转成 Markdown 标题供章节识别。</summary>
    private sealed class HtmlToText
    {
        private readonly PreviewBuilder _preview;
        private readonly StringBuilder _text = new();
        private readonly StringBuilder _tag = new();
        private bool _inTag;
        private bool _skip;

        public HtmlToText(PreviewBuilder preview) => _preview = preview;

        public void Append(string chunk)
        {
            foreach (var c in chunk)
            {
                if (_inTag)
                {
                    if (c == '>')
                    {
                        _inTag = false;
                        ProcessTag(_tag.ToString());
                        _tag.Clear();
                    }
                    else
                    {
                        _tag.Append(c);
                    }
                }
                else if (c == '<')
                {
                    FlushText();
                    _inTag = true;
                    _tag.Clear();
                }
                else if (!_skip)
                {
                    _text.Append(c);
                }
            }
        }

        public void Finish()
        {
            if (!_inTag) FlushText();
        }

        private void FlushText()
        {
            if (_text.Length == 0) return;
            var text = WebUtility.HtmlDecode(_text.ToString());
            _text.Clear();
            _preview.Append(text);
        }

        private void ProcessTag(string raw)
        {
            var tag = raw.Trim();
            if (tag.Length == 0) return;

            var closing = tag.StartsWith('/');
            var body = closing ? tag[1..] : tag;
            var parts = body.Split(new[] { ' ', '\t', '\r', '\n', '/' }, StringSplitOptions.RemoveEmptyEntries);
            var name = parts.Length > 0 ? parts[0].ToLowerInvariant() : string.Empty;

            if (name is "script" or "style")
            {
                _skip = !closing;
                return;
            }
            if (_skip) return;

            var blockTag = name is "p" or "div" or "li" or "tr" or "table" or "blockquote";
            var headingTag = name is "h1" or "h2" or "h3" or "h4" or "h5" or "h6";

            if (name == "br" || name == "hr" || (closing && (blockTag || headingTag)))
            {
                _preview.Append("\n");
            }
            else if (!closing && (name == "h1" || name == "h2"))
            {
                _preview.Append("\n# ");
            }
            else if (!closing && headingTag)
            {
                _preview.Append("\n## ");
            }
            else if (!closing && blockTag)
            {
                _preview.Append("\n");
            }
        }
    }

    /// <summary>流式生成按章节切分的 HTML 预览文件，同时保留一份限长文本用于存便签。</summary>
    private sealed class PreviewBuilder
    {
        private const string Head = "<!DOCTYPE html><html><head><meta charset=\"utf-8\">"
                                    + "<meta name=\"viewport\" content=\"width=device-width, initial-scale=1\">"
                                    + "<style>body{font-size:16px;line-height:1.7;color:#222;padding:12px;word-wrap:break-word}"
                                    + "pre{white-space:pre-wrap;font-family:inherit;margin:0}"
                                    + ".chapter{font-size:19px;font-weight:bold;color:#c07a00;margin:6px 0 14px}"
                                    + ".tip{color:#999;font-size:13px;margin-bottom:6px}</style>"
                                    + "<script>(function(){"
                                    + "var cmd='',first=false,last=false,speed=0,raf=0,lastT=0,swiped=false,sx=0,sy=0;"
                                    + "function lineH(){var h=parseFloat(getComputedStyle(document.body).lineHeight);return (isNaN(h)||h<10)?27:h;}"
                                    + "function maxY(){var h=Math.max(document.body.scrollHeight,document.documentElement.scrollHeight);return Math.max(0,h-window.innerHeight);}"
                                    + "function push(c){if(!cmd)cmd=c;}"
                                    + "window.__noteTake=function(){var c=cmd;cmd='';return c;};"
                                    + "window.noteSetBounds=function(f,l){first=!!f;last=!!l;};"
                                    + "window.noteStopAuto=function(){speed=0;if(raf)cancelAnimationFrame(raf);raf=0;};"
                                    + "window.noteSetAuto=function(s){speed=s>0?s:0;if(raf)cancelAnimationFrame(raf);raf=0;"
                                    + "if(speed>0){lastT=performance.now();raf=requestAnimationFrame(step);}};"
                                    + "function step(t){raf=0;if(speed<=0)return;var dt=Math.min(200,t-lastT);lastT=t;"
                                    + "var m=maxY();if(m<=2){push('autoNext');return;}"
                                    + "window.scrollBy(0,speed*lineH()*dt/1000);"
                                    + "if(window.scrollY>=m-2){push('autoNext');return;}raf=requestAnimationFrame(step);}"
                                    + "function center(){if(speed>0){window.noteStopAuto();push('autoStop');}else{push('center');}}"
                                    + "document.addEventListener('click',function(e){if(swiped){swiped=false;return;}"
                                    + "var x=e.clientX,w=window.innerWidth;"
                                    + "if(x<w*0.28){if(!first)push('prev');}"
                                    + "else if(x>w*0.72){if(!last)push('next');}else{center();}});"
                                    + "document.addEventListener('touchstart',function(e){"
                                    + "var t=e.changedTouches[0];sx=t.clientX;sy=t.clientY;swiped=false;},{passive:true});"
                                    + "document.addEventListener('touchend',function(e){"
                                    + "var t=e.changedTouches[0],dx=t.clientX-sx,dy=t.clientY-sy;"
                                    + "if(Math.abs(dx)>55&&Math.abs(dx)>Math.abs(dy)*1.4){swiped=true;"
                                    + "if(dx<0){if(!last)push('next');}else{if(!first)push('prev');}}},{passive:true});"
                                    + "function go(){var m=/[#&]r=([0-9.]+)/.exec(location.hash||'');if(!m)return;"
                                    + "var r=parseFloat(m[1]);if(!(r>0))return;var h=maxY();"
                                    + "if(h>0)window.scrollTo(0,Math.round(h*r));}"
                                    + "window.addEventListener('load',function(){go();setTimeout(go,350);});"
                                    + "})();</script>"
                                    + "</head><body>";

        private readonly string _key;
        private readonly string _dir;
        private readonly StringBuilder _note = new();
        private readonly StringBuilder _line = new();
        private readonly List<string> _pages = new();
        private readonly List<ChapterInfo> _chapters = new();
        private StreamWriter _writer = null!;
        private ChapterInfo? _currentChapter;
        private int _page;
        private long _pageChars;
        private long _totalChars;
        private long _chapterChars;
        private bool _headerWritten;
        private bool _lastWasCR;
        private bool _wroteAnyText;

        public PreviewBuilder(string key)
        {
            _key = key;
            _dir = CacheDir;
            Directory.CreateDirectory(_dir);
            OpenPage(0);
        }

        private string PagePath(int index) => Path.Combine(_dir, $"{_key}_{index}.html");

        private void OpenPage(int index)
        {
            _page = index;
            var path = PagePath(index);
            if (index >= _pages.Count) _pages.Add(path);
            else _pages[index] = path;

            _writer = new StreamWriter(path, false, new UTF8Encoding(false));
            _writer.Write(Head);
            _headerWritten = false;
            _pageChars = 0;
        }

        private void EnsureHeader()
        {
            if (_headerWritten) return;

            var title = _currentChapter?.Title ?? "全文";
            var part = _currentChapter is not null && _page > _currentChapter.PageIndex
                ? $"（续 {_page - _currentChapter.PageIndex + 1}）"
                : string.Empty;

            _writer.Write($"<div class='tip'>第 {_page + 1} 页</div>");
            _writer.Write($"<div class='chapter'>{WebUtility.HtmlEncode(title + part)}</div><pre>");
            _headerWritten = true;
        }

        private void ClosePage()
        {
            EnsureHeader();
            _writer.Write("</pre></body></html>");
            _writer.Dispose();
        }

        private void StartChapter(string title)
        {
            if (_currentChapter is not null)
            {
                _currentChapter.CharCount = _chapterChars;
            }
            else if (_wroteAnyText)
            {
                _chapters.Add(new ChapterInfo
                {
                    Index = 1,
                    Title = "开篇",
                    PageIndex = 0,
                    CharCount = _totalChars
                });
            }

            // 每个章节从新的一页开始，页面内容为该章节的全部字符
            if (_wroteAnyText)
            {
                ClosePage();
                OpenPage(_pages.Count);
            }

            _currentChapter = new ChapterInfo
            {
                Index = _chapters.Count + 1,
                Title = title,
                PageIndex = _page
            };
            _chapters.Add(_currentChapter);
            _chapterChars = 0;
        }

        private void WriteLine(string raw)
        {
            var data = raw + "\n";

            // 极端长的章节才拆页，避免 WebView 单页过大；常规章节整章一页
            if (_pageChars > 0 && _pageChars + data.Length > MaxPageChars)
            {
                ClosePage();
                OpenPage(_pages.Count);
            }

            EnsureHeader();
            _writer.Write(WebUtility.HtmlEncode(data));
            _pageChars += data.Length;
            _totalChars += data.Length;
            _chapterChars += data.Length;
            _wroteAnyText = true;

            if (_note.Length < MaxNoteChars)
                _note.Append(data, 0, Math.Min(data.Length, MaxNoteChars - _note.Length));
        }

        private void EmitLine(bool detectChapter)
        {
            var raw = _line.ToString();
            _line.Clear();

            var title = detectChapter ? ChapterDetector.Detect(raw) : null;
            if (title is not null) StartChapter(title);

            // 忽略文档开头空行，避免生成空的「开篇」章节
            if (raw.Length == 0 && !_wroteAnyText && title is null) return;
            WriteLine(raw);
        }

        public void Append(string? text)
        {
            if (string.IsNullOrEmpty(text)) return;

            foreach (var c in text)
            {
                if (c == '\r')
                {
                    EmitLine(true);
                    _lastWasCR = true;
                }
                else if (c == '\n')
                {
                    if (!_lastWasCR) EmitLine(true);
                    _lastWasCR = false;
                }
                else
                {
                    _lastWasCR = false;
                    _line.Append(c);
                    if (_line.Length >= MaxLineChars) EmitLine(false);
                }
            }
        }

        public (string Text, List<string> Pages, List<ChapterInfo> Chapters, long TotalChars) Finish()
        {
            if (_line.Length > 0) EmitLine(true);

            if (_currentChapter is not null)
                _currentChapter.CharCount = _chapterChars;

            if (_chapters.Count == 0)
                _chapters.Add(new ChapterInfo
                {
                    Index = 1,
                    Title = "全文",
                    PageIndex = 0,
                    CharCount = _totalChars
                });

            ClosePage();
            return (_note.ToString(), _pages, _chapters, _totalChars);
        }
    }
}
