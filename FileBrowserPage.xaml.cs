using System.Collections.ObjectModel;
using Note.Services;

namespace Note;

/// <summary>文件浏览器中的一个条目。</summary>
public class FileEntry
{
    public string Name { get; set; } = string.Empty;
    public string FullPath { get; set; } = string.Empty;
    public bool IsFolder { get; set; }
    public string IconText { get; set; } = string.Empty;
    public string DetailText { get; set; } = string.Empty;
}

[QueryProperty(nameof(Path), "path")]
public partial class FileBrowserPage : ContentPage
{
    private static readonly string[] SupportedExtensions = { ".txt", ".html", ".htm", ".doc", ".docx" };

    private readonly ObservableCollection<FileEntry> _entries = new();

    public string? Path { get; set; }

    public FileBrowserPage()
    {
        InitializeComponent();
        EntryList.ItemsSource = _entries;
    }

    private static string RootPath
    {
        get
        {
#if ANDROID
            var external = Android.OS.Environment.ExternalStorageDirectory?.AbsolutePath;
            if (!string.IsNullOrEmpty(external)) return external;
#endif
            return FileSystem.AppDataDirectory;
        }
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();

        var target = Path;
        if (!string.IsNullOrWhiteSpace(target))
        {
            try { target = Uri.UnescapeDataString(target); }
            catch { /* 保持原样 */ }
        }

        if (string.IsNullOrWhiteSpace(target) || !Directory.Exists(target))
            target = RootPath;

        LoadDirectory(target);
    }

    private void LoadDirectory(string path)
    {
        _entries.Clear();

        try
        {
            var dir = new DirectoryInfo(path);
            TitleLabel.Text = string.Equals(dir.FullName, RootPath, StringComparison.OrdinalIgnoreCase)
                ? "内部存储"
                : dir.Name;
            PathLabel.Text = dir.FullName;

            var folders = dir.EnumerateDirectories()
                .Where(d => !d.Name.StartsWith('.'))
                .OrderBy(d => d.Name, StringComparer.OrdinalIgnoreCase)
                .Select(d => new FileEntry
                {
                    Name = d.Name,
                    FullPath = d.FullName,
                    IsFolder = true,
                    IconText = "📁",
                    DetailText = "文件夹"
                });

            var files = dir.EnumerateFiles()
                .Where(f => SupportedExtensions.Contains(f.Extension.ToLowerInvariant()))
                .OrderBy(f => f.Name, StringComparer.OrdinalIgnoreCase)
                .Select(f => new FileEntry
                {
                    Name = f.Name,
                    FullPath = f.FullName,
                    IsFolder = false,
                    IconText = "📄",
                    DetailText = $"{FormatSize(f.Length)} · {f.Extension.Trim('.').ToUpperInvariant()}"
                });

            foreach (var entry in folders.Concat(files))
                _entries.Add(entry);
        }
        catch (Exception ex)
        {
            _ = DisplayAlert("无法访问目录", ex.Message, "知道了");
        }
    }

    private static string FormatSize(long bytes)
    {
        if (bytes < 1024) return $"{bytes} B";
        if (bytes < 1024 * 1024) return $"{bytes / 1024.0:F1} KB";
        return $"{bytes / 1024.0 / 1024.0:F1} MB";
    }

    private async void OnEntryTapped(object? sender, TappedEventArgs e)
    {
        if (e.Parameter is not FileEntry entry) return;

        if (entry.IsFolder)
        {
            await Shell.Current.GoToAsync($"browser?path={Uri.EscapeDataString(entry.FullPath)}");
            return;
        }

        await DocumentOpener.OpenPathAsync(this, entry.FullPath, entry.Name);
    }

    private async void OnUpClicked(object? sender, TappedEventArgs e)
    {
        var current = PathLabel.Text;
        if (string.IsNullOrEmpty(current)) return;

        if (string.Equals(current, RootPath, StringComparison.OrdinalIgnoreCase))
        {
            await DisplayAlert("提示", "已经是内部存储根目录了", "好的");
            return;
        }

        var parent = Directory.GetParent(current)?.FullName;
        if (string.IsNullOrEmpty(parent))
        {
            await DisplayAlert("提示", "已经是根目录了", "好的");
            return;
        }

        await Shell.Current.GoToAsync($"browser?path={Uri.EscapeDataString(parent)}");
    }

    private async void OnBackClicked(object? sender, TappedEventArgs e)
    {
        await Shell.Current.GoToAsync("..");
    }
}
