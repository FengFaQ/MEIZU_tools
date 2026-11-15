using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using MeiyouToolbox.ViewModels;

namespace MeiyouToolbox.Views;

/// <summary>
/// 9008 刷写页。全窗口遮罩式，由刷机页的「选择刷写通路」按钮打开。
/// <para>
/// 代码后置只做一件事：文件 / 目录选择。其余全部在 <see cref="EdlFlashViewModel"/> 里。
/// </para>
/// </summary>
public partial class EdlFlashOverlay : UserControl
{
    public EdlFlashOverlay()
    {
        InitializeComponent();
    }

    private EdlFlashViewModel? Vm => DataContext as EdlFlashViewModel;

    /// <summary>救砖包位置：目录优先，其次才是单个文件。</summary>
    private async void OnPickPackage(object? sender, RoutedEventArgs e)
    {
        try
        {
            var folder = await PickFolderAsync("选择线刷包目录");
            if (folder is not null)
            {
                if (Vm is { } folderVm)
                {
                    folderVm.PackagePath = folder;
                }

                return;
            }

            // 用户取消目录选择时，再让他选单个镜像文件。
            var file = await PickFileAsync("选择镜像文件", "镜像", "*.img", "*.bin");
            if (file is not null && Vm is { } fileVm)
            {
                fileVm.PackagePath = file;
            }
        }
        catch (Exception ex)
        {
            AppDiagnostics.Write("选择救砖包失败", ex);
        }
    }

    private async void OnPickProgrammer(object? sender, RoutedEventArgs e)
    {
        try
        {
            var file = await PickFileAsync("选择 9008 引导文件", "引导文件",
                "*.mbn", "*.elf", "*.melf", "*.bin");
            if (file is not null && Vm is { } vm)
            {
                vm.ProgrammerPath = file;
            }
        }
        catch (Exception ex)
        {
            AppDiagnostics.Write("选择引导文件失败", ex);
        }
    }

    private async void OnPickPartitionImage(object? sender, RoutedEventArgs e)
    {
        try
        {
            var file = await PickFileAsync("选择分区镜像", "镜像", "*.img", "*.bin");
            if (file is not null)
            {
                Vm?.AttachImageToSelected(file);
            }
        }
        catch (Exception ex)
        {
            AppDiagnostics.Write("选择分区镜像失败", ex);
        }
    }

    private async Task<string?> PickFileAsync(string title, string typeName, params string[] patterns)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null)
        {
            return null;
        }

        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType(typeName) { Patterns = patterns },
                FilePickerFileTypes.All,
            ],
        });

        return files.Count > 0 ? files[0].TryGetLocalPath() : null;
    }

    private async Task<string?> PickFolderAsync(string title)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null)
        {
            return null;
        }

        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = title,
            AllowMultiple = false,
        });

        return folders.Count > 0 ? folders[0].TryGetLocalPath() : null;
    }
}
