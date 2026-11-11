using System.Text.Json;
using Note.Models;

namespace Note.Services;

/// <summary>本地便签存储（JSON 文件保存在应用私有目录）。</summary>
public class NoteStore
{
    private static readonly Lazy<NoteStore> _instance = new(() => new NoteStore());
    public static NoteStore Instance => _instance.Value;

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private readonly string _dataFile;

    private NoteStore()
    {
        _dataFile = Path.Combine(FileSystem.AppDataDirectory, "notes.json");
    }

    public List<NoteItem> Notes { get; private set; } = new();

    public event EventHandler? Changed;

    public void Load()
    {
        try
        {
            if (!File.Exists(_dataFile))
            {
                Notes = new List<NoteItem>();
                return;
            }

            var json = File.ReadAllText(_dataFile);
            Notes = JsonSerializer.Deserialize<List<NoteItem>>(json, JsonOptions) ?? new List<NoteItem>();
        }
        catch
        {
            Notes = new List<NoteItem>();
        }
    }

    public void Save()
    {
        try
        {
            var json = JsonSerializer.Serialize(Notes, JsonOptions);
            var tmp = _dataFile + ".tmp";
            File.WriteAllText(tmp, json);
            File.Move(tmp, _dataFile, true);
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"保存便签失败: {ex}");
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public NoteItem Create()
    {
        var note = new NoteItem { CreatedAt = DateTime.Now, UpdatedAt = DateTime.Now };
        return note;
    }

    public void Add(NoteItem note)
    {
        if (Notes.All(n => n.Id != note.Id))
            Notes.Add(note);
        Save();
    }

    public void Delete(NoteItem note)
    {
        NoteImages.DeleteFor(note.Content);
        Notes.RemoveAll(n => n.Id == note.Id);
        Save();
    }

    public NoteItem? Find(string? id)
        => string.IsNullOrEmpty(id) ? null : Notes.FirstOrDefault(n => n.Id == id);

    public IEnumerable<NoteItem> Query(string? keyword)
    {
        IEnumerable<NoteItem> query = Notes;

        if (!string.IsNullOrWhiteSpace(keyword))
        {
            var kw = keyword.Trim();
            query = query.Where(n =>
                n.Title.Contains(kw, StringComparison.OrdinalIgnoreCase) ||
                n.Content.Contains(kw, StringComparison.OrdinalIgnoreCase));
        }

        return query
            .OrderByDescending(n => n.IsPinned)
            .ThenByDescending(n => n.UpdatedAt);
    }
}
