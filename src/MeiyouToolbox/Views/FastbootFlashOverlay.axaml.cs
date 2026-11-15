using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using MeiyouToolbox.ViewModels;

namespace MeiyouToolbox.Views;

/// <summary>
/// Fastboot 刷写页。只有一个输入框：payload.bin、OTA 压缩包，或已解包的文件夹。
/// </summary>
public partial class FastbootFlashOverlay : UserControl
{
    public FastbootFlashOverlay()
    {
        InitializeComponent();
    }

    private FastbootFlashViewModel? Vm => DataContext as FastbootFlashViewModel;

    /// <summary>
    /// 选择源：先弹文件夹选择（已解包镜像的常见用法），取消后再弹文件选择。
    /// </summary>
    private async void OnPickSource(object? sender, RoutedEventArgs e)
    {
        try
        {
            var folder = await PickFolderAsync("选择已解包的镜像文件夹").ConfigureAwait(true);
            if (!string.IsNullOrWhiteSpace(folder))
            {
                Vm?.SetSource(folder);
                return;
            }

            var file = await PickFileAsync("选择 payload 或镜像", "payload / 镜像",
                "*.bin", "*.zip", "*.img").ConfigureAwait(true);

            if (!string.IsNullOrWhiteSpace(file))
            {
                Vm?.SetSource(file);
            }
        }
        catch (Exception ex)
        {
            AppDiagnostics.Write("选择刷写源失败", ex);
        }
    }

    private async Task<string?> PickFileAsync(string title, string typeName, params string[] patterns)
    {
        var window = TopLevel.GetTopLevel(this);
        if (window is null)
        {
            return null;
        }

        var types = new List<FilePickerFileType>
        {
            new(typeName) { Patterns = patterns },
            FilePickerFileTypes.All,
        };

        var files = await window.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter = types,
        }).ConfigureAwait(true);

        return files.Count == 0 ? null : files[0].TryGetLocalPath();
    }

    private async Task<string?> PickFolderAsync(string title)
    {
        var window = TopLevel.GetTopLevel(this);
        if (window is null)
        {
            return null;
        }

        var folders = await window.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
        }).ConfigureAwait(true);

        return folders.Count == 0 ? null : folders[0].TryGetLocalPath();
    }
}
