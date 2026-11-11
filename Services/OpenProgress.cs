using Microsoft.Maui.Controls.Shapes;

namespace Note.Services;

/// <summary>
/// 打开大文档时的进度遮罩。延迟 350ms 才出现，缓存命中秒开时不会闪屏；
/// 进度只增不减，避免指纹计算与解析两个阶段交替导致进度条回退。
/// </summary>
public sealed class OpenProgress : IProgress<double>, IDisposable
{
    private readonly Page _page;
    private readonly string _fileName;
    private readonly CancellationTokenSource _cts = new();
    private Grid? _overlay;
    private ProgressBar? _bar;
    private Label? _percent;
    private double _max;
    private bool _disposed;

    public OpenProgress(Page page, string fileName)
    {
        _page = page;
        _fileName = fileName;
        _ = ShowDelayedAsync();
    }

    private async Task ShowDelayedAsync()
    {
        try
        {
            await Task.Delay(350, _cts.Token);
        }
        catch (OperationCanceledException)
        {
            return;
        }

        if (_disposed) return;
        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_disposed || _overlay is not null) return;

            // 外部意图可能在阅读页之上触发，遮罩必须挂到当前可见页面
            var visible = Shell.Current?.CurrentPage ?? _page;
            if (visible is ContentPage { Content: Grid root }) Build(root);
        });
    }

    private void Build(Grid root)
    {
        var overlay = new Grid
        {
            BackgroundColor = Color.FromArgb("#88000000"),
            Padding = 28
        };
        Grid.SetRow(overlay, 0);
        Grid.SetRowSpan(overlay, Math.Max(1, root.RowDefinitions.Count));

        var card = new Border
        {
            BackgroundColor = Color.FromArgb("#FFFFFF"),
            StrokeThickness = 0,
            Padding = 22,
            HorizontalOptions = LayoutOptions.Fill,
            VerticalOptions = LayoutOptions.Center,
            StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(18) }
        };

        var stack = new VerticalStackLayout { Spacing = 12 };
        stack.Children.Add(new Label
        {
            Text = "正在打开文档",
            FontSize = 18,
            FontAttributes = FontAttributes.Bold,
            TextColor = Color.FromArgb("#1E2126")
        });
        stack.Children.Add(new Label
        {
            Text = _fileName,
            FontSize = 13,
            TextColor = Color.FromArgb("#8B9099"),
            LineBreakMode = LineBreakMode.MiddleTruncation
        });

        _bar = new ProgressBar
        {
            Progress = 0,
            ProgressColor = Color.FromArgb("#FFB000")
        };
        stack.Children.Add(_bar);

        _percent = new Label
        {
            Text = "0%",
            FontSize = 13,
            TextColor = Color.FromArgb("#FFB000"),
            HorizontalTextAlignment = TextAlignment.Center
        };
        stack.Children.Add(_percent);

        stack.Children.Add(new Label
        {
            Text = "大文档首次打开需要解析并生成目录，之后可直接秒开",
            FontSize = 12,
            TextColor = Color.FromArgb("#B4B9C0"),
            HorizontalTextAlignment = TextAlignment.Center
        });

        card.Content = stack;
        overlay.Children.Add(card);
        root.Children.Add(overlay);
        _overlay = overlay;
        Update(_max);
    }

    public void Report(double value)
    {
        if (_disposed) return;
        var next = Math.Clamp(value, 0, 1);
        if (next <= _max) return;
        _max = next;

        var snapshot = _max;
        MainThread.BeginInvokeOnMainThread(() => Update(snapshot));
    }

    private void Update(double value)
    {
        if (_bar is null || _percent is null) return;
        _bar.Progress = value;
        _percent.Text = $"{(int)(value * 100)}%";
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        try { _cts.Cancel(); }
        catch { /* ignore */ }

        MainThread.BeginInvokeOnMainThread(() =>
        {
            if (_overlay?.Parent is Grid root) root.Children.Remove(_overlay);
            _overlay = null;
            _bar = null;
            _percent = null;
        });
    }
}
