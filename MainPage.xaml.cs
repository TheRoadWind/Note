using System.Collections.ObjectModel;
using Note.Models;
using Note.Services;

namespace Note;

public partial class MainPage : ContentPage
{
    private readonly ObservableCollection<NoteItem> _items = new();
    private bool _importing;

    public MainPage()
    {
        InitializeComponent();
        NoteList.ItemsSource = _items;
        AppState.PendingFileChanged += OnPendingFileChanged;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        NoteStore.Instance.Load();
        RefreshList();
        _ = HandlePendingFileAsync();
    }

    private void OnPendingFileChanged()
    {
        MainThread.BeginInvokeOnMainThread(async () => await HandlePendingFileAsync());
    }

    private void RefreshList()
    {
        var notes = NoteStore.Instance.Query(SearchEntry.Text).ToList();

        _items.Clear();
        foreach (var note in notes)
            _items.Add(note);

        CountLabel.Text = string.IsNullOrWhiteSpace(SearchEntry.Text)
            ? $"共 {notes.Count} 条便签"
            : $"找到 {notes.Count} 条便签";
    }

    private void OnSearchTextChanged(object? sender, TextChangedEventArgs e) => RefreshList();

    private async void OnAddClicked(object? sender, EventArgs e)
    {
        await Shell.Current.GoToAsync("edit?id=new");
    }

    private async void OnNoteTapped(object? sender, TappedEventArgs e)
    {
        if (e.Parameter is NoteItem note)
            await Shell.Current.GoToAsync($"edit?id={note.Id}");
    }

    private void OnPinInvoked(object? sender, EventArgs e)
    {
        if (sender is SwipeItem { CommandParameter: NoteItem note })
        {
            note.IsPinned = !note.IsPinned;
            note.UpdatedAt = DateTime.Now;
            NoteStore.Instance.Save();
            RefreshList();
        }
    }

    private async void OnDeleteInvoked(object? sender, EventArgs e)
    {
        if (sender is not SwipeItem { CommandParameter: NoteItem note })
            return;

        var ok = await DisplayAlert("删除便签", $"确定删除「{note.DisplayTitle}」吗？", "删除", "取消");
        if (!ok) return;

        NoteStore.Instance.Delete(note);
        RefreshList();
    }

    private async void OnOpenFileClicked(object? sender, EventArgs e)
        => await DocumentOpener.ShowOpenMenuAsync(this);

    private async Task HandlePendingFileAsync()
    {
        if (_importing) return;

        var (path, name) = AppState.TakePendingFile();
        if (string.IsNullOrEmpty(path)) return;

        _importing = true;
        try
        {
            // 外部打开时当前可见页面可能是阅读页，提示与进度都要落在它上面
            await DocumentOpener.OpenPathAsync(Shell.Current?.CurrentPage ?? this, path, name);
        }
        finally
        {
            _importing = false;
        }
    }
}
