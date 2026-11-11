using Microsoft.Maui.Controls.Shapes;
using Microsoft.Maui.Graphics;
using Path = System.IO.Path;
using Note.Models;
using Note.Services;

namespace Note;

[QueryProperty(nameof(NoteId), "id")]
public partial class NoteEditPage : ContentPage
{
    private static readonly string[] BackgroundHexes =
    {
        "", "#FFF6DE", "#FFE9EC", "#E7F1FF", "#E7F7EC", "#F0EAFF", "#ECEFF2"
    };

    private static readonly Color Accent = Color.FromArgb("#FFB000");
    private static readonly Color Faint = Color.FromArgb("#B4B9C0");
    private static readonly Color Divider = Color.FromArgb("#EDEFF2");
    private static readonly Color NeutralBg = Color.FromArgb("#F4F5F7");
    private static readonly Color Text = Color.FromArgb("#1E2126");

    private readonly List<Border> _colorSwatches = new();
    private readonly List<Button> _fontButtons = new();
    private NoteItem? _note;
    private bool _isNew;
    private bool _loaded;
    private bool _applyingText;
    private int _hintSeq;
    private int _imageMarkerCount;

    public string? NoteId { get; set; }

    public NoteEditPage()
    {
        InitializeComponent();
        BuildStylePanel();
    }

    protected override void OnAppearing()
    {
        base.OnAppearing();
        if (_loaded) return;
        _loaded = true;

        if (string.Equals(NoteId, "new", StringComparison.OrdinalIgnoreCase))
        {
            _note = NoteStore.Instance.Create();
            _isNew = true;
            SavedHint.Text = "新便签";
            TitleEntry.Focus();
        }
        else
        {
            _note = NoteStore.Instance.Find(NoteId);
            SavedHint.Text = _note is null ? string.Empty : "已保存";
        }

        if (_note is null) return;

        _applyingText = true;
        TitleEntry.Text = _note.Title;
        BodyEditor.Text = _note.Content;
        _applyingText = false;

        UpdatePinUi();
        UpdateStatus();
        _imageMarkerCount = NoteImages.FileNames(BodyEditor.Text).Count();
        ApplyStyle();
        RefreshImages();
    }

    protected override void OnDisappearing()
    {
        Persist();
        base.OnDisappearing();
    }

    protected override bool OnBackButtonPressed()
    {
        Persist();
        return base.OnBackButtonPressed();
    }

    // ---------- 样式面板 ----------

    private void BuildStylePanel()
    {
        foreach (var hex in BackgroundHexes)
        {
            var swatch = new Border
            {
                WidthRequest = 32,
                HeightRequest = 32,
                BackgroundColor = string.IsNullOrEmpty(hex) ? Color.FromArgb("#FFFFFF") : Color.FromArgb(hex),
                Stroke = Divider,
                StrokeThickness = 1,
                Padding = 0,
                StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(16) }
            };

            var tap = new TapGestureRecognizer { CommandParameter = hex };
            tap.Tapped += OnColorTapped;
            swatch.GestureRecognizers.Add(tap);
            ColorRow.Children.Add(swatch);
            _colorSwatches.Add(swatch);
        }

        var fontOptions = new[] { ("小", "size0"), ("标准", "size1"), ("大", "size2"), ("B", "bold"), ("I", "italic") };
        foreach (var (label, key) in fontOptions)
        {
            var button = new Button
            {
                Text = label,
                BackgroundColor = NeutralBg,
                TextColor = Text,
                FontSize = 12,
                Padding = new Thickness(10, 4),
                CornerRadius = 14,
                HeightRequest = 32,
                MinimumHeightRequest = 32
            };
            var captured = key;
            button.Clicked += (_, _) => OnFontClicked(captured);
            FontRow.Children.Add(button);
            _fontButtons.Add(button);
        }
    }

    private void OnColorTapped(object? sender, TappedEventArgs e)
    {
        if (_note is null || e.Parameter is not string hex) return;
        _note.BackgroundHex = hex;
        MarkEditing();
        ApplyStyle();
    }

    private void OnFontClicked(string key)
    {
        if (_note is null) return;

        switch (key)
        {
            case "size0": _note.FontSizeLevel = 0; break;
            case "size1": _note.FontSizeLevel = 1; break;
            case "size2": _note.FontSizeLevel = 2; break;
            case "bold": _note.IsBold = !_note.IsBold; break;
            case "italic": _note.IsItalic = !_note.IsItalic; break;
        }

        MarkEditing();
        ApplyStyle();
    }

    private void OnStyleClicked(object? sender, EventArgs e)
        => StylePanel.IsVisible = !StylePanel.IsVisible;

    private void ApplyStyle()
    {
        if (_note is null) return;

        BodyEditor.FontSize = _note.BodyFontSize;
        BodyEditor.FontAttributes = _note.BodyFontAttributes;
        TitleEntry.FontSize = _note.TitleFontSize;
        BackgroundColor = _note.CardBackground;

        for (var i = 0; i < _colorSwatches.Count; i++)
        {
            var selected = string.Equals(BackgroundHexes[i], _note.BackgroundHex, StringComparison.OrdinalIgnoreCase);
            _colorSwatches[i].Stroke = selected ? Accent : Divider;
            _colorSwatches[i].StrokeThickness = selected ? 3 : 1;
        }

        for (var i = 0; i < _fontButtons.Count; i++)
        {
            var active = i switch
            {
                0 => _note.FontSizeLevel == 0,
                1 => _note.FontSizeLevel == 1,
                2 => _note.FontSizeLevel == 2,
                3 => _note.IsBold,
                _ => _note.IsItalic
            };
            _fontButtons[i].BackgroundColor = active ? Accent : NeutralBg;
            _fontButtons[i].TextColor = active ? Color.FromArgb("#FFFFFF") : Text;
        }
    }

    // ---------- 内嵌图片 ----------

    private async void OnInsertImageClicked(object? sender, EventArgs e)
    {
        try
        {
            var file = await FilePicker.Default.PickAsync(new PickOptions
            {
                PickerTitle = "选择图片",
                FileTypes = FilePickerFileType.Images
            });
            if (file is null) return;

            var dir = NoteImages.Directory;
            System.IO.Directory.CreateDirectory(dir);
            var ext = Path.GetExtension(file.FileName).ToLowerInvariant();
            if (string.IsNullOrWhiteSpace(ext)) ext = ".jpg";
            var name = Guid.NewGuid().ToString("N") + ext;
            var target = Path.Combine(dir, name);

            using (var input = await file.OpenReadAsync())
            using (var output = File.Create(target))
                await input.CopyToAsync(output);

            var text = BodyEditor.Text ?? string.Empty;
            var separator = text.Length > 0 && !text.EndsWith('\n') ? "\n" : string.Empty;
            BodyEditor.Text = text + separator + NoteImages.Marker(name) + "\n";
            RefreshImages();
            ShowHint("已插入图片");
        }
        catch (Exception ex)
        {
            System.Diagnostics.Debug.WriteLine($"插入图片失败: {ex}");
            ShowHint("插入图片失败");
        }
    }

    private void RefreshImages()
    {
        ImageStrip.Children.Clear();

        foreach (var path in NoteImages.Paths(BodyEditor.Text).Distinct(StringComparer.OrdinalIgnoreCase))
        {
            if (!File.Exists(path)) continue;
            var captured = path;

            var frame = new Border
            {
                WidthRequest = 80,
                HeightRequest = 80,
                StrokeThickness = 0,
                Padding = 0,
                StrokeShape = new RoundRectangle { CornerRadius = new CornerRadius(10) },
                Content = new Image
                {
                    Source = ImageSource.FromStream(() => File.OpenRead(captured)),
                    WidthRequest = 80,
                    HeightRequest = 80,
                    Aspect = Aspect.AspectFill
                }
            };

            var tap = new TapGestureRecognizer { CommandParameter = Path.GetFileName(captured) };
            tap.Tapped += OnRemoveImageTapped;
            frame.GestureRecognizers.Add(tap);
            ImageStrip.Children.Add(frame);
        }

        ImageScroller.IsVisible = ImageStrip.Children.Count > 0;
    }

    private void OnRemoveImageTapped(object? sender, TappedEventArgs e)
    {
        if (e.Parameter is not string name) return;

        BodyEditor.Text = (BodyEditor.Text ?? string.Empty).Replace(NoteImages.Marker(name), string.Empty);
        try
        {
            var path = Path.Combine(NoteImages.Directory, name);
            if (File.Exists(path)) File.Delete(path);
        }
        catch
        {
            // ignore
        }

        RefreshImages();
        ShowHint("已移除图片");
    }

    // ---------- 复制 ----------

    private async void OnCopyClicked(object? sender, EventArgs e)
    {
        var text = NoteImages.StripMarkers(BodyEditor.Text).Trim();
        if (string.IsNullOrEmpty(text))
        {
            ShowHint("没有可复制的文字");
            return;
        }

        try
        {
            await Microsoft.Maui.ApplicationModel.DataTransfer.Clipboard.Default.SetTextAsync(text);
            ShowHint("已复制");
        }
        catch
        {
            ShowHint("复制失败");
        }
    }

    /// <summary>顶部状态文字做短暂提示，不弹窗。</summary>
    private async void ShowHint(string message)
    {
        var seq = ++_hintSeq;
        SavedHint.Text = message;
        SavedHint.TextColor = Accent;

        await Task.Delay(1800);
        if (seq != _hintSeq) return;

        SavedHint.Text = _note is null ? string.Empty : "已保存";
        SavedHint.TextColor = Faint;
    }

    // ---------- 编辑状态 ----------

    private void MarkEditing()
    {
        _hintSeq++;
        SavedHint.Text = "编辑中…";
        SavedHint.TextColor = Faint;
    }

    private void OnTextChanged(object? sender, TextChangedEventArgs e)
    {
        if (_applyingText) return;
        MarkEditing();
        UpdateStatus();

        var count = NoteImages.FileNames(BodyEditor.Text).Count();
        if (count != _imageMarkerCount)
        {
            _imageMarkerCount = count;
            RefreshImages();
        }
    }

    private void UpdateStatus()
    {
        var length = (TitleEntry.Text?.Length ?? 0) + (BodyEditor.Text?.Length ?? 0);
        StatusLabel.Text = $"{length} 字符";
    }

    private void UpdatePinUi()
    {
        if (_note is null) return;
        PinLabel.Text = _note.IsPinned ? "已置顶" : "置顶";
        PinLabel.TextColor = _note.IsPinned
            ? (Color)Application.Current!.Resources["AppAccent"]
            : (Color)Application.Current!.Resources["AppText"];
        PinButton.BackgroundColor = _note.IsPinned
            ? (Color)Application.Current!.Resources["AppAccentSoft"]
            : (Color)Application.Current!.Resources["AppBg"];
    }

    private void Persist()
    {
        if (_note is null) return;

        var title = TitleEntry.Text ?? string.Empty;
        var content = BodyEditor.Text ?? string.Empty;

        if (string.IsNullOrWhiteSpace(title) && string.IsNullOrWhiteSpace(content))
        {
            // 空白便签不保存
            _note = null;
            return;
        }

        _note.Title = title;
        _note.Content = content;
        _note.UpdatedAt = DateTime.Now;

        if (_isNew)
        {
            NoteStore.Instance.Add(_note);
            _isNew = false;
        }
        else
        {
            NoteStore.Instance.Save();
        }

        SavedHint.Text = "已保存";
    }

    private async void OnBackClicked(object? sender, TappedEventArgs e)
    {
        Persist();
        await Shell.Current.GoToAsync("..");
    }

    private void OnPinClicked(object? sender, TappedEventArgs e)
    {
        if (_note is null) return;

        _note.IsPinned = !_note.IsPinned;
        _note.UpdatedAt = DateTime.Now;

        if (!_isNew)
            NoteStore.Instance.Save();

        UpdatePinUi();
        ShowHint(_note.IsPinned ? "已置顶" : "已取消置顶");
    }

    private async void OnDeleteClicked(object? sender, TappedEventArgs e)
    {
        if (_note is null)
        {
            await Shell.Current.GoToAsync("..");
            return;
        }

        var ok = await DisplayAlert("删除便签", $"确定删除「{_note.DisplayTitle}」吗？", "删除", "取消");
        if (!ok) return;

        NoteImages.DeleteFor(BodyEditor.Text);
        if (!_isNew)
            NoteStore.Instance.Delete(_note);

        _note = null;
        await Shell.Current.GoToAsync("..");
    }
}
