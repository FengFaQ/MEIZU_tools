using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using MeiyouToolbox.Models;
using MeiyouToolbox.ViewModels;

namespace MeiyouToolbox.Views;

/// <summary>
/// 刷机页。文件选择全部走 Windows 原生对话框（Avalonia StorageProvider），
/// 选择结果直接写回视图模型，不在这里做业务判断。
/// </summary>
public partial class FlashView : UserControl
{
    private bool _initialized;

    public FlashView()
    {
        InitializeComponent();
        AttachedToVisualTree += OnAttached;
    }

    private FlashViewModel? Vm => DataContext as FlashViewModel;

    private async void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        if (Vm is not { } vm)
        {
            return;
        }

        try
        {
            if (!_initialized)
            {
                _initialized = true;
                await vm.RefreshConnectionAsync();
            }
            else
            {
                await vm.RefreshConnectionAsync();
            }
        }
        catch (Exception ex)
        {
            AppDiagnostics.Write("刷机页初始化失败", ex);
        }
    }

    // ----------------------------------------------------------------
    // 文件 / 目录选择
    // ----------------------------------------------------------------

    private async void OnPickProgrammer(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm)
        {
            return;
        }

        var path = await PickFileAsync("选择 9008 引导文件", "引导文件", "*.elf", "*.mbn", "*.bin", "*.*");
        if (!string.IsNullOrWhiteSpace(path))
        {
            vm.ProgrammerPath = path;
        }
    }

    /// <summary>救砖包位置：既可能是目录（9008 线刷包）也可能是单个镜像。</summary>
    private async void OnPickPackagePath(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm)
        {
            return;
        }

        // 9008 通路要目录，Fastboot 通路要文件，按当前通路决定弹哪种选择器。
        if (vm.SelectedTarget == FlashTarget.Edl)
        {
            var folder = await PickFolderAsync("选择 9008 线刷包目录");
            if (!string.IsNullOrWhiteSpace(folder))
            {
                vm.PackagePath = folder;
            }

            return;
        }

        var file = await PickFileAsync("选择要刷入的镜像", "镜像文件", "*.img", "*.bin", "*.*");
        if (!string.IsNullOrWhiteSpace(file))
        {
            vm.PackagePath = file;
            vm.PartitionName ??= Path.GetFileNameWithoutExtension(file);
        }
    }

    private async void OnPickRecoveryImage(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm)
        {
            return;
        }

        var path = await PickFileAsync("选择 Recovery 镜像", "镜像文件", "*.img", "*.*");
        if (!string.IsNullOrWhiteSpace(path))
        {
            vm.RecoveryImagePath = path;
        }
    }

    private async void OnPickPartitionImage(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm)
        {
            return;
        }

        var path = await PickFileAsync("选择分区镜像", "镜像文件", "*.img", "*.bin", "*.*");
        if (!string.IsNullOrWhiteSpace(path))
        {
            vm.PartitionImagePath = path;
        }
    }

    /// <summary>选择 mzkv 解锁镜像。</summary>
    public async void OnPickMzkv(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm)
        {
            return;
        }

        var path = await PickFileAsync("选择解锁用 mzkv 镜像", "镜像文件", "*.img", "*.bin", "*.*");
        if (!string.IsNullOrWhiteSpace(path))
        {
            vm.ImportMzkvCommand.Execute(path);
        }
    }

    private async Task<string?> PickFileAsync(
        string title,
        string typeName,
        params string[] patterns)
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
