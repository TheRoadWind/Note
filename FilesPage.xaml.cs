using System.Collections.ObjectModel;
using Note.Models;
using Note.Services;

namespace Note;

public partial class FilesPage : ContentPage
{
    private readonly ObservableCollection<DocumentRecord> _items = new();

    public FilesPage()
    {
        InitializeComponent();
        HistoryList.ItemsSource = _items;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        HistoryStore.Instance.Load();
        Refresh();
    }

    private void Refresh()
    {
        var records = HistoryStore.Instance.GetAll();
        _items.Clear();
        foreach (var record in records)
            _items.Add(record);

        CountLabel.Text = $"共 {records.Count} 条浏览记录";
    }

    private async void OnBrowseClicked(object? sender, TappedEventArgs e)
    {
        if (!await DocumentOpener.EnsureStorageAsync())
        {
            await DisplayAlert("提示", "需要存储权限才能浏览本地文件", "知道了");
            return;
        }
        await Shell.Current.GoToAsync("browser");
    }

    private async void OnRecordTapped(object? sender, TappedEventArgs e)
    {
        if (e.Parameter is not DocumentRecord record) return;

        if (!File.Exists(record.FilePath))
        {
            var remove = await DisplayAlert("文件不可用", "原文件已不存在或被移动，是否删除这条浏览记录？", "删除记录", "保留");
            if (remove)
            {
                HistoryStore.Instance.Delete(record);
                Refresh();
            }
            return;
        }

        await DocumentOpener.OpenPathAsync(this, record.FilePath, record.FileName);
    }

    private void OnDeleteInvoked(object? sender, EventArgs e)
    {
        if (sender is not SwipeItem { CommandParameter: DocumentRecord record }) return;
        HistoryStore.Instance.Delete(record);
        Refresh();
    }

    private async void OnClearClicked(object? sender, TappedEventArgs e)
    {
        if (_items.Count == 0) return;
        var ok = await DisplayAlert("清空记录", "确定清空全部浏览记录吗？文档本身不会被删除，阅读位置也会重置。", "清空", "取消");
        if (!ok) return;

        HistoryStore.Instance.Clear();
        Refresh();
    }
}
