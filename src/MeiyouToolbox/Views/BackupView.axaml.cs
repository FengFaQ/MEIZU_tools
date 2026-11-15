using Avalonia;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using Microsoft.Extensions.DependencyInjection;
using MeiyouToolbox.Services;
using MeiyouToolbox.ViewModels;

namespace MeiyouToolbox.Views;

/// <summary>
/// 备份页。目录与文件选择必须由视图完成 —— 只有 TopLevel 能拿到 StorageProvider。
/// </summary>
public partial class BackupView : UserControl
{
    private bool _initialized;

    public BackupView()
    {
        InitializeComponent();
        AttachedToVisualTree += OnAttached;
    }

    private BackupViewModel? Vm => DataContext as BackupViewModel;

    private async void OnAttached(object? sender, VisualTreeAttachmentEventArgs e)
    {
        // 每次切回本页都刷新一次连接状态；机型表只灌一次。
        if (Vm is not { } vm)
        {
            return;
        }

        try
        {
            if (!_initialized)
            {
                _initialized = true;
                await vm.InitializeAsync().ConfigureAwait(true);
            }
            else
            {
                await vm.RefreshConnectionAsync().ConfigureAwait(true);
            }
        }
        catch (Exception ex)
        {
            AppDiagnostics.Write("BackupView 初始化", ex);
        }
    }

    private async void OnPickDirectory(object? sender, RoutedEventArgs e)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null || Vm is not { } vm)
        {
            return;
        }

        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择备份保存目录",
            AllowMultiple = false,
        });

        if (folders.Count > 0 && folders[0].TryGetLocalPath() is { } path)
        {
            vm.SetTargetDirectory(path);
        }
    }

    private void OnOpenTargetDirectory(object? sender, RoutedEventArgs e)
    {
        if (Vm is not { } vm || string.IsNullOrWhiteSpace(vm.TargetDirectory))
        {
            return;
        }

        try
        {
            var launcher = App.Services.GetService<IShellLauncher>();
            launcher?.OpenFolder(vm.TargetDirectory);
        }
        catch (Exception ex)
        {
            AppDiagnostics.Write("打开备份目录", ex);
        }
    }

    private async void OnPickQcnFile(object? sender, RoutedEventArgs e)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null || Vm is not { } vm)
        {
            return;
        }

        var files = await top.StorageProvider.OpenFilePickerAsync(new FilePickerOpenOptions
        {
            Title = "选择 QCN 备份文件",
            AllowMultiple = false,
            FileTypeFilter =
            [
                new FilePickerFileType("QCN 基带备份") { Patterns = ["*.qcn"] },
                FilePickerFileTypes.All,
            ],
        });

        if (files.Count > 0 && files[0].TryGetLocalPath() is { } path)
        {
            vm.SetQcnFile(path);
        }
    }
}
