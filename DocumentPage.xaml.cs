using System.Collections.ObjectModel;
using System.Globalization;
using Note.Services;

namespace Note;

public partial class DocumentPage : ContentPage
{
    private static readonly string[] SpeedOptions = { "1 行/秒", "2 行/秒", "3 行/秒", "5 行/秒", "8 行/秒" };

    private ImportedDocument? _document;
    private bool _loaded;
    private List<string> _pages = new();
    private List<ChapterInfo> _chapters = new();
    private readonly ObservableCollection<ChapterInfo> _tocItems = new();
    private readonly SemaphoreSlim _jsLock = new(1, 1);
    private Microsoft.Maui.Dispatching.IDispatcherTimer? _pollTimer;
    private int _pageIndex;
    private double _pendingScrollRatio;
    private int _autoSpeed;
    private int _navSeq;
    private bool _polling;

    public DocumentPage()
    {
        InitializeComponent();
        TocList.ItemsSource = _tocItems;
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();

        if (_document is not null && _document.UseWebView)
        {
            StartPolling();
            SetKeepScreenOn(_autoSpeed > 0);
        }
        if (_loaded) return;
        _loaded = true;

        _document = AppState.SelectedDocument;
        if (_document is null)
        {
            _ = Shell.Current.GoToAsync("..");
            return;
        }

        FileLabel.Text = _document.FileName;

        _pages = _document.Pages.Count > 0
            ? _document.Pages.ToList()
            : (!string.IsNullOrEmpty(_document.PreviewPath)
                ? new List<string> { _document.PreviewPath! }
                : new List<string>());

        _chapters = _document.Chapters.Count > 0
            ? _document.Chapters.ToList()
            : new List<ChapterInfo>
            {
                new() { Index = 1, Title = "全文", PageIndex = 0, CharCount = _document.TotalChars }
            };

        foreach (var chapter in _chapters) _tocItems.Add(chapter);
        TocCountLabel.Text = $"共 {_chapters.Count} 章 · {_document.TotalChars:N0} 字符";
        TocButton.IsVisible = _chapters.Count > 1;

        if (_document.UseWebView && _pages.Count > 0)
        {
            TextScroll.IsVisible = false;
            DocWebView.IsVisible = true;
            PagerBar.IsVisible = _pages.Count > 1;
            StartPolling();

            // 读取记忆：再次打开同一文档时回到上次的页与页内位置
            var record = HistoryStore.Instance.Find(_document.RecordId);
            var startPage = record is null ? 0 : Math.Clamp(record.LastPageIndex, 0, _pages.Count - 1);
            var ratio = record?.ScrollRatio ?? 0;
            ShowPage(startPage, ratio, restored: true);
        }
        else
        {
            TextScroll.IsVisible = true;
            DocWebView.IsVisible = false;
            PagerBar.IsVisible = false;
            AutoHint.IsVisible = false;
            TapHint.IsVisible = false;
            DocLabel.Text = _document.Text;
        }

        if (!string.IsNullOrEmpty(_document.Warning))
            _ = DisplayAlert("提示", _document.Warning, "知道了");
    }

    protected override void OnDisappearing()
    {
        StopPolling();
        _autoSpeed = 0;
        SetKeepScreenOn(false);
        _ = PersistPositionAsync();
        base.OnDisappearing();
    }

    private void ShowPage(int index, double ratio = 0, bool restored = false)
    {
        if (_pages.Count == 0) return;

        _pageIndex = Math.Clamp(index, 0, _pages.Count - 1);
        _pendingScrollRatio = ratio;

        var url = new Uri(Path.GetFullPath(_pages[_pageIndex])).AbsoluteUri;
        if (ratio > 0.001)
            url += "#r=" + ratio.ToString("F4", CultureInfo.InvariantCulture);

        DocWebView.Source = new UrlWebViewSource { Url = url };

        var chapter = ChapterForPage(_pageIndex);
        PageLabel.Text = $"第 {_pageIndex + 1}/{_pages.Count} 页 · {chapter.Title} · {chapter.CharCount:N0} 字符"
                         + (restored && (_pageIndex > 0 || ratio > 0.01) ? " · 已恢复" : "");

        PrevButton.TextColor = _pageIndex > 0
            ? (Color)Application.Current!.Resources["AppText"]
            : (Color)Application.Current!.Resources["AppTextFaint"];
        NextButton.TextColor = _pageIndex < _pages.Count - 1
            ? (Color)Application.Current!.Resources["AppAccent"]
            : (Color)Application.Current!.Resources["AppTextFaint"];

        foreach (var item in _tocItems)
            item.IsCurrent = item.PageIndex == chapter.PageIndex;

        UpdateAutoHint();
    }

    private ChapterInfo ChapterForPage(int page)
    {
        var chapter = _chapters[0];
        foreach (var item in _chapters)
        {
            if (item.PageIndex > page) break;
            chapter = item;
        }
        return chapter;
    }

    // ---------- WebView 与 HTML 交互 ----------

    private async void OnWebViewNavigated(object? sender, WebNavigatedEventArgs e)
        => await ApplyPageStateAsync();

    /// <summary>页面加载完成后下发首/末页边界、恢复阅读位置、续上自动滑动。</summary>
    private async Task ApplyPageStateAsync()
    {
        var seq = ++_navSeq;
        var ratio = _pendingScrollRatio;
        _pendingScrollRatio = 0;

        try
        {
            await Task.Delay(220);
            if (seq != _navSeq) return;

            var first = _pageIndex == 0;
            var last = _pageIndex == _pages.Count - 1;
            await EvalAsync("window.noteSetBounds&&window.noteSetBounds("
                            + (first ? "true" : "false") + "," + (last ? "true" : "false") + ");");

            if (ratio > 0.001)
            {
                await EvalAsync("window.scrollTo(0,(document.body.scrollHeight-window.innerHeight)*"
                                + ratio.ToString("F4", CultureInfo.InvariantCulture) + ");");
            }

            if (_autoSpeed > 0)
                await EvalAsync("window.noteSetAuto&&window.noteSetAuto(" + _autoSpeed + ");");
        }
        catch
        {
            // JS 不可用时，HTML 内的脚本仍会按 URL 片段恢复位置
        }
    }

    /// <summary>串行执行 JS，避免 Android WebView 并发求值互相覆盖。</summary>
    private async Task<string?> EvalAsync(string script)
    {
        if (!await _jsLock.WaitAsync(TimeSpan.FromMilliseconds(500))) return null;
        try
        {
            return await DocWebView.EvaluateJavaScriptAsync(script);
        }
        catch
        {
            return null;
        }
        finally
        {
            _jsLock.Release();
        }
    }

    private void StartPolling()
    {
        if (_pollTimer is not null) return;
        _pollTimer = Dispatcher.CreateTimer();
        _pollTimer.Interval = TimeSpan.FromMilliseconds(120);
        _pollTimer.Tick += (_, _) => _ = PollCommandAsync();
        _pollTimer.Start();
    }

    private void StopPolling()
    {
        if (_pollTimer is null) return;
        _pollTimer.Stop();
        _pollTimer = null;
    }

    private async Task PollCommandAsync()
    {
        if (_polling || _document is null || !_document.UseWebView || _pages.Count == 0) return;
        _polling = true;
        try
        {
            var raw = await EvalAsync("window.__noteTake?window.__noteTake():''");
            var command = raw?.Trim().Trim('"');
            if (string.IsNullOrEmpty(command)) return;
            await HandleCommandAsync(command);
        }
        finally
        {
            _polling = false;
        }
    }

    private async Task HandleCommandAsync(string command)
    {
        switch (command)
        {
            case "prev":
                await ChangePageAsync(_pageIndex - 1);
                break;
            case "next":
                await ChangePageAsync(_pageIndex + 1);
                break;
            case "center":
                await ChooseAutoSpeedAsync();
                break;
            case "autoStop":
                _autoSpeed = 0;
                UpdateAutoHint();
                break;
            case "autoNext":
                await AutoAdvanceAsync();
                break;
        }
    }

    /// <summary>自动滑动到当页底部时自动切换下一页；末页到底则停止。</summary>
    private async Task AutoAdvanceAsync()
    {
        if (_pageIndex < _pages.Count - 1)
        {
            await ChangePageAsync(_pageIndex + 1);
            return;
        }

        _autoSpeed = 0;
        await EvalAsync("window.noteStopAuto&&window.noteStopAuto();");
        UpdateAutoHint();
    }

    private async Task ChooseAutoSpeedAsync()
    {
        var choice = await DisplayActionSheet("自动滑动（点屏幕中间可停止）", "取消", null, SpeedOptions);
        if (string.IsNullOrEmpty(choice) || choice == "取消") return;

        var digits = new string(choice.TakeWhile(char.IsDigit).ToArray());
        if (!int.TryParse(digits, out var speed) || speed <= 0) return;

        _autoSpeed = speed;
        UpdateAutoHint();
        await EvalAsync("window.noteSetAuto&&window.noteSetAuto(" + speed + ");");
    }

    private void UpdateAutoHint()
    {
        AutoHint.IsVisible = _autoSpeed > 0;
        AutoHintLabel.Text = _autoSpeed > 0 ? $"自动 {_autoSpeed} 行/秒 · 点中间停止" : string.Empty;
        TapHint.IsVisible = _autoSpeed == 0 && _document is not null && _document.UseWebView;
        SetKeepScreenOn(_autoSpeed > 0);
    }

    /// <summary>自动滑动期间保持屏幕常亮；停止或离开页面立即释放。</summary>
    private static void SetKeepScreenOn(bool on)
    {
        try
        {
            var display = Microsoft.Maui.Devices.DeviceDisplay.Current;
            if (display.KeepScreenOn != on)
                display.KeepScreenOn = on;
        }
        catch
        {
            // 部分平台不支持时不影响阅读
        }
    }

    private async Task ChangePageAsync(int index)
    {
        if (index < 0 || index >= _pages.Count) return;
        await PersistPositionAsync();
        ShowPage(index);
    }

    // ---------- 阅读位置记忆 ----------

    private async Task<double> ReadScrollRatioAsync()
    {
        var raw = await EvalAsync(
            "(function(){var h=document.body.scrollHeight-window.innerHeight;"
            + "return h>0?(window.scrollY/h).toFixed(4):'0';})()");

        var value = raw?.Trim().Trim('"');
        if (double.TryParse(value, NumberStyles.Float, CultureInfo.InvariantCulture, out var ratio)
            && ratio >= 0 && ratio <= 1)
            return ratio;
        return 0;
    }

    private async Task PersistPositionAsync()
    {
        if (_document is null || !_document.UseWebView || _pages.Count == 0) return;

        var ratio = await ReadScrollRatioAsync();
        var chapter = ChapterForPage(_pageIndex);
        HistoryStore.Instance.UpdatePosition(_document.RecordId, _pageIndex, ratio, chapter.Title, _pages.Count);
    }

    // ---------- 界面操作 ----------

    private async void OnPrevPage(object? sender, EventArgs e) => await ChangePageAsync(_pageIndex - 1);

    private async void OnNextPage(object? sender, EventArgs e) => await ChangePageAsync(_pageIndex + 1);

    private void OnTocClicked(object? sender, TappedEventArgs e) => TocOverlay.IsVisible = true;

    private void OnCloseTocClicked(object? sender, TappedEventArgs e) => TocOverlay.IsVisible = false;

    private async void OnChapterTapped(object? sender, TappedEventArgs e)
    {
        if (e.Parameter is not ChapterInfo chapter) return;
        TocOverlay.IsVisible = false;
        await ChangePageAsync(Math.Clamp(chapter.PageIndex, 0, _pages.Count - 1));
    }

    private void OnRestartClicked(object? sender, EventArgs e)
    {
        TocOverlay.IsVisible = false;
        _autoSpeed = 0;
        ShowPage(0);
        if (_document is not null)
            HistoryStore.Instance.UpdatePosition(_document.RecordId, 0, 0, ChapterForPage(0).Title, _pages.Count);
    }

    private async void OnBackClicked(object? sender, TappedEventArgs e)
    {
        StopPolling();
        _autoSpeed = 0;
        SetKeepScreenOn(false);
        await PersistPositionAsync();
        AppState.SelectedDocument = null;
        await Shell.Current.GoToAsync("..");
    }

    private async void OnSaveAsNoteClicked(object? sender, TappedEventArgs e)
    {
        if (_document is null) return;

        var note = NoteStore.Instance.Create();
        note.Title = _document.Title;
        note.Content = _document.Text;
        NoteStore.Instance.Add(note);

        var message = $"「{_document.Title}」已存为便签";
        if (_document.TotalChars >= DocumentReader.MaxNoteChars)
            message += $"\n文件较大，便签仅保留前 {DocumentReader.MaxNoteChars:N0} 个字符（阅读页仍可浏览全文）。";

        var goBack = await DisplayAlert("已保存", message, "返回列表", "继续查看");
        if (goBack)
        {
            AppState.SelectedDocument = null;
            await Shell.Current.GoToAsync("..");
        }
    }
}
