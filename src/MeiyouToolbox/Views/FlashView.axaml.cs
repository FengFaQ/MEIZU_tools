using System.Collections.Generic;
using System.Threading.Tasks;
using Avalonia.Controls;
using Avalonia.Interactivity;
using Avalonia.Platform.Storage;
using MeiyouToolbox.ViewModels;

namespace MeiyouToolbox.Views;

/// <summary>
/// 刷机页。文件选择必须由视图完成 —— 只有 TopLevel 才能拿到 StorageProvider，
/// ViewModel 里做这件事会把它和窗口生命周期绑死。
/// </summary>
public partial class FlashView : UserControl
{
    public FlashView()
    {
        InitializeComponent();
    }

    private FlashViewModel? Vm => DataContext as FlashViewModel;

    // ------------------------------------------------------------------
    // 文件选择
    // ------------------------------------------------------------------

    private async void OnPickMzkv(object? sender, RoutedEventArgs e)
    {
        var path = await PickFileAsync("选择解锁 mzkv 镜像", "镜像文件", "img", "bin");
        if (path is not null)
        {
            Vm?.ImportMzkvCommand.Execute(path);
        }
    }

    private async void OnPickRecoveryImage(object? sender, RoutedEventArgs e)
    {
        var path = await PickFileAsync("选择 Recovery 镜像", "镜像文件", "img");
        if (path is not null && Vm is { } vm)
        {
            vm.RecoveryImagePath = path;
        }
    }

    private async void OnPickPartitionImage(object? sender, RoutedEventArgs e)
    {
        var path = await PickFileAsync("选择分区镜像", "镜像文件", "img", "bin");
        if (path is not null && Vm is { } vm)
        {
            vm.PartitionImagePath = path;
        }
    }

    private async void OnPickPackageDirectory(object? sender, RoutedEventArgs e)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null)
        {
            return;
        }

        var folders = await top.StorageProvider.OpenFolderPickerAsync(new FolderPickerOpenOptions
        {
            Title = "选择 9008 线刷包目录（含 rawprogram*.xml）",
            AllowMultiple = false,
        });

        if (folders.Count > 0 && Vm is { } vm)
        {
            vm.PackageDirectory = folders[0].TryGetLocalPath();
        }
    }

    private async void OnPickProgrammer(object? sender, RoutedEventArgs e)
    {
        var path = await PickFileAsync("选择 Firehose 引导文件", "引导文件", "elf", "mbn", "bin");
        if (path is not null && Vm is { } vm)
        {
            vm.ProgrammerPath = path;
        }
    }

    private async Task<string?> PickFileAsync(string title, string typeName, params string[] extensions)
    {
        var top = TopLevel.GetTopLevel(this);
        if (top is null)
        {
            return null;
        }

        var patterns = new List<string>(extensions.Length);
        foreach (var ext in extensions)
        {
            patterns.Add($"*.{ext}");
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
}
