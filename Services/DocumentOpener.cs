using Note.Services;

namespace Note;

/// <summary>统一的文档打开入口：记录浏览历史并跳转到阅读页。</summary>
public static class DocumentOpener
{
    public static async Task<bool> EnsureStorageAsync()
    {
#if ANDROID
        var status = await Permissions.CheckStatusAsync<Permissions.StorageRead>();
        if (status != PermissionStatus.Granted)
            status = await Permissions.RequestAsync<Permissions.StorageRead>();
        return status == PermissionStatus.Granted;
#else
        await Task.CompletedTask;
        return true;
#endif
    }

    public static async Task ShowOpenMenuAsync(Page page)
    {
        var source = await page.DisplayActionSheet("打开文件", "取消", null, "浏览手机存储", "系统文件选择器");
        if (string.IsNullOrEmpty(source) || source == "取消") return;

        if (!await EnsureStorageAsync())
        {
            await page.DisplayAlert("提示", "需要存储权限才能打开本地文件", "知道了");
            return;
        }

        if (source == "浏览手机存储")
        {
            await Shell.Current.GoToAsync("browser");
            return;
        }

        await PickAndOpenAsync(page);
    }

    public static async Task PickAndOpenAsync(Page page)
    {
        try
        {
            var file = await FilePicker.Default.PickAsync(new PickOptions
            {
                PickerTitle = "选择 TXT / HTML / DOC / DOCX 文件",
                FileTypes = DocumentReader.PickTypes
            });

            if (file is null) return;

            using var progress = new OpenProgress(page, file.FileName);
            var doc = await Task.Run(async () => await DocumentReader.ReadAsync(file, progress));
            await ShowAsync(page, doc);
        }
        catch (Exception ex)
        {
            await page.DisplayAlert("打开失败", ex.Message, "知道了");
        }
    }

    public static async Task OpenPathAsync(Page page, string path, string? displayName = null)
    {
        try
        {
            if (!File.Exists(path))
            {
                await page.DisplayAlert("无法打开", "文件不存在或已被移动。", "知道了");
                return;
            }

            var name = string.IsNullOrWhiteSpace(displayName) ? Path.GetFileName(path) : displayName!;
            using var progress = new OpenProgress(page, name);

            // 指纹计算与章节解析都放在后台线程，UI 线程只负责刷新进度
            var doc = await Task.Run(async () =>
            {
                // 缓存目录里的临时副本按内容哈希规范化，避免同一文档留下多份副本
                var source = await DocumentReader.NormalizeTransientAsync(path, name, progress);
                return await DocumentReader.ReadAsync(source, name, progress);
            });
            await ShowAsync(page, doc);
        }
        catch (Exception ex)
        {
            await page.DisplayAlert("打开失败", $"没有权限或文件损坏：{ex.Message}", "知道了");
        }
    }

    private static async Task ShowAsync(Page page, ImportedDocument doc)
    {
        if (doc.Pages.Count == 0 && !string.IsNullOrEmpty(doc.Warning))
        {
            await page.DisplayAlert("打开失败", doc.Warning!, "知道了");
            return;
        }

        doc.RecordId = await HistoryStore.Instance.RecordOpenAsync(doc);
        AppState.SelectedDocument = doc;
        await Shell.Current.GoToAsync("viewer");
    }
}
