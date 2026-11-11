using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json.Serialization;

namespace Note.Services;

/// <summary>自动生成的目录条目，对应文档中的一个章节。</summary>
public sealed class ChapterInfo : INotifyPropertyChanged
{
    private bool _isCurrent;

    public int Index { get; set; }

    public string Title { get; set; } = string.Empty;

    /// <summary>该章节首页在页面列表中的位置。</summary>
    public int PageIndex { get; set; }

    /// <summary>该章节包含的全部字符数量。</summary>
    public long CharCount { get; set; }

    [JsonIgnore]
    public bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            if (_isCurrent == value) return;
            _isCurrent = value;
            OnPropertyChanged();
            OnPropertyChanged(nameof(Marker));
        }
    }

    [JsonIgnore]
    public string Marker => IsCurrent ? "▶" : "·";

    [JsonIgnore]
    public string IndexText => Index.ToString("00");

    [JsonIgnore]
    public string CharCountText => $"{CharCount:N0} 字符";

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}
