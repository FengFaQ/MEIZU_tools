using Avalonia.Controls;
using Avalonia.Platform.Storage;
using MeiyouToolbox.ViewModels;

namespace MeiyouToolbox.Views;

public partial class CredentialEditorOverlay : UserControl
{
    public CredentialEditorOverlay()
    {
        InitializeComponent();
    }

    private FlashViewModel? Vm => DataContext as FlashViewModel;

    private async void OnPickMzkv(object? sender, Avalonia.Interactivity.RoutedEventArgs e)
    {
        try
        {
            if (Vm is not { } vm)
            {
                return;
            }

            var storage = TopLevel.GetTopLevel(this)?.StorageProvider;
            if (storage is null)
            {
                return;
            }

            var files = await storage.OpenFilePickerAsync(new FilePickerOpenOptions
            {
                Title = "选择解锁版 mzkv 镜像",
                AllowMultiple = false,
                FileTypeFilter =
                [
                    new FilePickerFileType("mzkv 镜像") { Patterns = ["*.img", "*.bin"] },
                    FilePickerFileTypes.All,
                ],
            });

            if (files.Count == 0)
            {
                return;
            }

            var path = files[0].TryGetLocalPath();
            if (!string.IsNullOrWhiteSpace(path))
            {
                vm.ImportMzkvCommand.Execute(path);
            }
        }
        catch (Exception ex)
        {
            AppDiagnostics.Write("选择 mzkv 镜像失败", ex);
        }
    }
}
