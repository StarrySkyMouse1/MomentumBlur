using System.IO;
using CommunityToolkit.Mvvm.ComponentModel;

namespace mmod_record.Models;

public enum BatchItemStatus
{
    Pending,
    Running,
    Done,
    Failed,
    Cancelled,
}

public partial class BatchVideoItem : ObservableObject
{
    public BatchVideoItem(string fullPath)
    {
        FullPath = Path.GetFullPath(fullPath);
        FileName = Path.GetFileName(FullPath);
    }

    public string FullPath { get; }

    public string FileName { get; }

    [ObservableProperty]
    private bool _isSelected = true;

    [ObservableProperty]
    private BatchItemStatus _status = BatchItemStatus.Pending;

    [ObservableProperty]
    private string _statusText = "待处理";

    [ObservableProperty]
    private string? _outputPath;

    [ObservableProperty]
    private double _progressPercent;

    public void ResetForQueue()
    {
        Status = BatchItemStatus.Pending;
        StatusText = "待处理";
        OutputPath = null;
        ProgressPercent = 0;
    }
}
