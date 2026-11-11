using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Maui.Controls;
using Microsoft.Maui.Graphics;

namespace Note.Models;

/// <summary>一条本地便签。</summary>
public class NoteItem : INotifyPropertyChanged
{
    private static readonly Regex ImageMarker = new(@"\[img:[^\]\r\n]+\]");

    private string _title = string.Empty;
    private string _content = string.Empty;
    private bool _isPinned;
    private DateTime _updatedAt = DateTime.Now;
    private string _backgroundHex = string.Empty;
    private int _fontSizeLevel = 1;
    private bool _isBold;
    private bool _isItalic;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public DateTime CreatedAt { get; set; } = DateTime.Now;

    public string Title
    {
        get => _title;
        set
        {
            if (_title == value) return;
            _title = value ?? string.Empty;
            OnPropertyChanged();
            OnPropertyChanged(nameof(DisplayTitle));
        }
    }

    public string Content
    {
        get => _content;
        set
        {
            if (_content == value) return;
            _content = value ?? string.Empty;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Preview));
        }
    }

    public bool IsPinned
    {
        get => _isPinned;
        set
        {
            if (_isPinned == value) return;
            _isPinned = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(PinActionText));
        }
    }

    public DateTime UpdatedAt
    {
        get => _updatedAt;
        set
        {
            if (_updatedAt == value) return;
            _updatedAt = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(TimeText));
        }
    }

    /// <summary>单独设置的卡片背景色（十六进制，空表示默认白色）。</summary>
    public string BackgroundHex
    {
        get => _backgroundHex;
        set
        {
            if (_backgroundHex == value) return;
            _backgroundHex = value ?? string.Empty;
            OnPropertyChanged();
            OnPropertyChanged(nameof(CardBackground));
        }
    }

    /// <summary>0 小、1 标准、2 大。</summary>
    public int FontSizeLevel
    {
        get => _fontSizeLevel;
        set
        {
            var level = Math.Clamp(value, 0, 2);
            if (_fontSizeLevel == level) return;
            _fontSizeLevel = level;
            OnPropertyChanged();
            OnPropertyChanged(nameof(TitleFontSize));
            OnPropertyChanged(nameof(PreviewFontSize));
            OnPropertyChanged(nameof(BodyFontSize));
        }
    }

    public bool IsBold
    {
        get => _isBold;
        set
        {
            if (_isBold == value) return;
            _isBold = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(BodyFontAttributes));
        }
    }

    public bool IsItalic
    {
        get => _isItalic;
        set
        {
            if (_isItalic == value) return;
            _isItalic = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(BodyFontAttributes));
        }
    }

    [JsonIgnore]
    public Color CardBackground
    {
        get
        {
            if (string.IsNullOrWhiteSpace(_backgroundHex)) return Color.FromArgb("#FFFFFF");
            try { return Color.FromArgb(_backgroundHex); }
            catch { return Color.FromArgb("#FFFFFF"); }
        }
    }

    [JsonIgnore]
    public double TitleFontSize => _fontSizeLevel switch { 0 => 15, 2 => 20, _ => 17 };

    [JsonIgnore]
    public double PreviewFontSize => _fontSizeLevel switch { 0 => 12, 2 => 16, _ => 14 };

    [JsonIgnore]
    public double BodyFontSize => _fontSizeLevel switch { 0 => 14, 2 => 21, _ => 16 };

    [JsonIgnore]
    public FontAttributes BodyFontAttributes
    {
        get
        {
            var attributes = FontAttributes.None;
            if (_isBold) attributes |= FontAttributes.Bold;
            if (_isItalic) attributes |= FontAttributes.Italic;
            return attributes;
        }
    }

    [JsonIgnore]
    public string DisplayTitle => string.IsNullOrWhiteSpace(Title) ? "无标题" : Title.Trim();

    [JsonIgnore]
    public string Preview
    {
        get
        {
            if (string.IsNullOrWhiteSpace(Content)) return "暂无内容";
            var text = ImageMarker.Replace(Content, "［图片］")
                .Replace("\r\n", " ").Replace('\n', ' ').Replace('\r', ' ').Replace('\t', ' ');
            while (text.Contains("  ")) text = text.Replace("  ", " ");
            return text.Trim();
        }
    }

    [JsonIgnore]
    public string TimeText => FormatTime(UpdatedAt);

    [JsonIgnore]
    public string PinActionText => IsPinned ? "取消置顶" : "置顶";

    [JsonIgnore]
    public string WordCountText
    {
        get
        {
            var text = (Title + "\n" + Content).Trim();
            return $"{text.Length} 字符";
        }
    }

    private static string FormatTime(DateTime time)
    {
        var local = time.ToLocalTime();
        var today = DateTime.Today;
        if (local.Date == today) return $"今天 {local:HH:mm}";
        if (local.Date == today.AddDays(-1)) return $"昨天 {local:HH:mm}";
        if (local.Year == today.Year) return $"{local:M月d日 HH:mm}";
        return local.ToString("yyyy/M/d HH:mm");
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
