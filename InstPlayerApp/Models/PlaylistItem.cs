using System.ComponentModel;

namespace InstPlayerApp.Models;

public class PlaylistItem : INotifyPropertyChanged
{
    public string FilePath { get; set; } = string.Empty;
    public string FileName => Path.GetFileName(FilePath);
    public string DisplayName
    {
        get
        {
            if (FilePath.StartsWith("content://"))
            {
                // content:// URI에서 파일명 추출 (예: primary%3AMusic%2Fsong.mp3 → song)
                var decoded = Uri.UnescapeDataString(FilePath);
                var lastSlash = decoded.LastIndexOf('/');
                var name = lastSlash >= 0 ? decoded[(lastSlash + 1)..] : decoded;
                return Path.GetFileNameWithoutExtension(name);
            }
            return Path.GetFileNameWithoutExtension(FilePath);
        }
    }

    private bool _isSelected;
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (_isSelected == value) return;
            _isSelected = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsSelected)));
        }
    }

    // 지금 재생 중(또는 정지된 현재) 곡 — 선택(IsSelected)과 별개로 ▶ 표시
    private bool _isCurrent;
    public bool IsCurrent
    {
        get => _isCurrent;
        set
        {
            if (_isCurrent == value) return;
            _isCurrent = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsCurrent)));
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
}
