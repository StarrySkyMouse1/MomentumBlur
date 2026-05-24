using System.IO;
using System.Windows;
using System.Windows.Controls;
using mmod_record.Services;
using mmod_record.ViewModels;

namespace mmod_record.Views.Pages;

public partial class RecordComposePage : Page
{
    public RecordComposePage()
    {
        InitializeComponent();
        Loaded += OnLoaded;
    }

    private void OnLoaded(object sender, RoutedEventArgs e)
    {
        DropZone.AllowDrop = true;
        DropZone.DragOver += OnDropZoneDragOver;
        DropZone.Drop += OnDropZoneDrop;
    }

    private void OnDropZoneDragOver(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
        {
            e.Effects = DragDropEffects.None;
            e.Handled = true;
            return;
        }

        var paths = (string[])e.Data.GetData(DataFormats.FileDrop)!;
        e.Effects = paths.Any(IsVideoPath) ? DragDropEffects.Copy : DragDropEffects.None;
        e.Handled = true;
    }

    private void OnDropZoneDrop(object sender, DragEventArgs e)
    {
        if (!e.Data.GetDataPresent(DataFormats.FileDrop))
            return;

        var paths = (string[])e.Data.GetData(DataFormats.FileDrop)!;
        var videos = paths.Where(IsVideoPath).ToArray();
        if (videos.Length == 0)
            return;

        if (DataContext is MainViewModel vm)
            vm.Record.AddVideoPaths(videos, selectNew: true);
    }

    private static bool IsVideoPath(string path) =>
        File.Exists(path) && ObsOutputPathHelper.IsSupportedVideoExtension(Path.GetExtension(path));
}
