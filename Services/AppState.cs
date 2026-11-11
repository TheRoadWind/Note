namespace Note.Services;

/// <summary>页面之间传递的临时状态。</summary>
public static class AppState
{
    public static string? PendingFilePath { get; private set; }

    public static string? PendingFileName { get; private set; }

    public static ImportedDocument? SelectedDocument { get; set; }

    /// <summary>外部应用（文件管理器/分享）打开文件时通知界面。</summary>
    public static event Action? PendingFileChanged;

    public static void SetPendingFile(string path, string? displayName = null)
    {
        PendingFilePath = path;
        PendingFileName = displayName;
        PendingFileChanged?.Invoke();
    }

    public static (string? Path, string? Name) TakePendingFile()
    {
        var path = PendingFilePath;
        var name = PendingFileName;
        PendingFilePath = null;
        PendingFileName = null;
        return (path, name);
    }
}
