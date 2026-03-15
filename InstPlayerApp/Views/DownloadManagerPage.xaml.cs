using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using InstPlayerApp.ViewModels;

namespace InstPlayerApp.Views;

/// <summary>
/// Simple data item for the download manager list.
/// </summary>
public partial class DownloadFileItem : ObservableObject
{
    public string FilePath { get; init; } = "";
    public string Name     { get; init; } = "";
    [ObservableProperty] private bool isInPlaylist;
}

/// <summary>
/// Modal page showing downloaded files.
/// Tap a row to add/remove from playlist; ✕ button to delete from disk.
/// </summary>
public partial class DownloadManagerPage : ContentPage
{
    private readonly MainViewModel _vm;
    private readonly Func<Task<string[]>> _pickFiles;

    public string HeaderTitle { get; private set; }
    public ObservableCollection<DownloadFileItem> Items { get; } = [];

    public DownloadManagerPage(MainViewModel vm, Func<Task<string[]>> pickFiles)
    {
        _vm = vm;
        _pickFiles = pickFiles;

        var files        = vm.GetAllDownloadedFiles();
        var notInList    = new HashSet<string>(vm.GetDownloadedFilesNotInPlaylist());

        HeaderTitle = $"다운로드 파일 ({files.Length}개)";

        foreach (var f in files)
        {
            Items.Add(new DownloadFileItem
            {
                FilePath    = f,
                Name        = Path.GetFileNameWithoutExtension(f),
                IsInPlaylist = !notInList.Contains(f),
            });
        }

        InitializeComponent();
        BindingContext = this;
    }

    private void OnFileItemTapped(object? sender, TappedEventArgs e)
    {
        if (sender is View v && v.BindingContext is DownloadFileItem item)
        {
            if (item.IsInPlaylist)
            {
                _vm.RemoveFileFromPlaylist(item.FilePath);
                item.IsInPlaylist = false;
            }
            else
            {
                _vm.AddFilesToPlaylist([item.FilePath]);
                item.IsInPlaylist = true;
            }
        }
    }

    private async void OnDeleteFileTapped(object? sender, EventArgs e)
    {
        if (sender is Button btn && btn.BindingContext is DownloadFileItem item)
        {
            bool ok = await DisplayAlert("파일 삭제",
                $"'{item.Name}' 파일을 삭제하시겠습니까?", "삭제", "취소");
            if (!ok) return;

            _vm.RemoveFileFromPlaylist(item.FilePath);
            _vm.DeleteFiles([item.FilePath]);
            Items.Remove(item);

            HeaderTitle = $"다운로드 파일 ({Items.Count}개)";
            OnPropertyChanged(nameof(HeaderTitle));
        }
    }

    private async void OnOpenFileBrowser(object? sender, EventArgs e)
    {
        await Navigation.PopModalAsync();
        var picked = await _pickFiles();
        _vm.AddFilesToPlaylist(picked);
    }

    private async void OnClose(object? sender, EventArgs e)
    {
        await Navigation.PopModalAsync();
    }
}
