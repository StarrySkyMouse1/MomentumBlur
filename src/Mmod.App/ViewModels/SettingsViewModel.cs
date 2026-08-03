using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mmod.Core.Models;
using Mmod.Core.Services;
using System.Diagnostics;
using System.IO;

namespace Mmod.App.ViewModels;

public partial class SettingsViewModel : ObservableObject
{
    private readonly UserSettingsStore _store;
    private UserSettings _settings;
    private bool _loading;

    public SettingsViewModel(UserSettingsStore store)
    {
        _store = store;
        _settings = _store.Load();
        ApplyFrom(_settings);
    }

    [ObservableProperty] private CaptureMode captureMode;
    [ObservableProperty] private int supersamplingMultiplier;
    [ObservableProperty] private double exposure;
    [ObservableProperty] private int obsCaptureFramerate;
    [ObservableProperty] private EncoderPreference encoder;
    [ObservableProperty] private string videoOutputDirectory = string.Empty;
    [ObservableProperty] private string ramDiskWatchDirectory = string.Empty;
    [ObservableProperty] private string gameRootPath = string.Empty;
    [ObservableProperty] private string movieSequenceName = "frame";
    [ObservableProperty] private string startMovieHotkey = "[";
    [ObservableProperty] private string endMovieHotkey = "]";
    [ObservableProperty] private bool hideHudInCfg;
    [ObservableProperty] private int maxParallelJobs = 2;
    [ObservableProperty] private string statusText = string.Empty;
    [ObservableProperty] private string slowMotionBlock = string.Empty;
    [ObservableProperty] private string restoreBlock = string.Empty;
    [ObservableProperty] private string junctionState = string.Empty;
    [ObservableProperty] private string cfgCommandBlock = string.Empty;

    public IReadOnlyList<CaptureMode> CaptureModeOptions { get; } = [CaptureMode.Tga, CaptureMode.Obs];
    public IReadOnlyList<EncoderPreference> EncoderOptions { get; } =
        [EncoderPreference.Auto, EncoderPreference.Nvenc, EncoderPreference.Amf];
    public IReadOnlyList<int> ObsCaptureFramerateOptions { get; } =
        ProjectConstants.SupportedObsCaptureFramerates;

    public bool IsObsMode => CaptureMode == CaptureMode.Obs;
    public bool IsTgaMode => CaptureMode == CaptureMode.Tga;

    partial void OnCaptureModeChanged(CaptureMode value)
    {
        OnPropertyChanged(nameof(IsObsMode));
        OnPropertyChanged(nameof(IsTgaMode));
        Persist();
    }

    partial void OnSupersamplingMultiplierChanged(int value) => Persist();
    partial void OnObsCaptureFramerateChanged(int value) => Persist();
    partial void OnEncoderChanged(EncoderPreference value) => Persist();
    partial void OnVideoOutputDirectoryChanged(string value) => Persist();
    partial void OnRamDiskWatchDirectoryChanged(string value)
    {
        Persist();
        NotifyPathCommandsCanExecuteChanged();
    }

    partial void OnGameRootPathChanged(string value)
    {
        Persist();
        NotifyPathCommandsCanExecuteChanged();
    }

    partial void OnMovieSequenceNameChanged(string value)
    {
        Persist();
        CreateJunctionCommand.NotifyCanExecuteChanged();
    }

    partial void OnStartMovieHotkeyChanged(string value)
    {
        Persist();
        CreateJunctionCommand.NotifyCanExecuteChanged();
    }

    partial void OnEndMovieHotkeyChanged(string value)
    {
        Persist();
        CreateJunctionCommand.NotifyCanExecuteChanged();
    }
    partial void OnHideHudInCfgChanged(bool value) => Persist();
    partial void OnMaxParallelJobsChanged(int value) => Persist();

    partial void OnExposureChanged(double value)
    {
        var clamped = Math.Clamp(value, 0.05, 1.0);
        if (Math.Abs(clamped - value) > 0.0001)
        {
            Exposure = clamped;
            return;
        }

        Persist();
    }

    private void ApplyFrom(UserSettings s)
    {
        _loading = true;
        try
        {
            CaptureMode = s.CaptureMode;
            SupersamplingMultiplier = s.SupersamplingMultiplier;
            Exposure = Math.Clamp(s.Exposure, 0.05, 1.0);
            ObsCaptureFramerate = s.ObsCaptureFramerate;
            Encoder = s.Encoder;
            VideoOutputDirectory = s.VideoOutputDirectory ?? string.Empty;
            RamDiskWatchDirectory = s.RamDiskWatchDirectory ?? string.Empty;
            GameRootPath = s.GameRootPath ?? string.Empty;
            MovieSequenceName = s.MovieSequenceName;
            StartMovieHotkey = s.StartMovieHotkey;
            EndMovieHotkey = s.EndMovieHotkey;
            HideHudInCfg = s.HideHudInCfg;
            MaxParallelJobs = Math.Max(1, s.MaxParallelJobs);
        }
        finally
        {
            _loading = false;
        }

        RefreshDerived();
    }

    public UserSettings Snapshot()
    {
        var s = new UserSettings
        {
            CaptureMode = CaptureMode,
            SupersamplingMultiplier = Math.Clamp(SupersamplingMultiplier, 1, 64),
            Exposure = Math.Clamp(Exposure, 0.05, 1.0),
            ObsCaptureFramerate = ObsCaptureFramerate,
            Encoder = Encoder,
            VideoOutputDirectory = VideoOutputDirectory?.Trim() ?? string.Empty,
            RamDiskWatchDirectory = RamDiskWatchDirectory?.Trim() ?? string.Empty,
            GameRootPath = GameRootPath?.Trim(),
            MovieSequenceName = MovieSequenceName,
            StartMovieHotkey = StartMovieHotkey,
            EndMovieHotkey = EndMovieHotkey,
            HideHudInCfg = HideHudInCfg,
            MaxParallelJobs = Math.Max(1, MaxParallelJobs),
            RamDiskDriveLetter = _settings.RamDiskDriveLetter,
            StartmoviePathPrefix = _settings.StartmoviePathPrefix,
            PendingTgaWarningCount = _settings.PendingTgaWarningCount,
        };
        WatchDirectoryHelper.EnsureDerivedPaths(s, s.GameRootPath);
        return s;
    }

    private void Persist()
    {
        if (_loading)
            return;

        _settings = Snapshot();
        _store.Save(_settings);
        RefreshDerived();
    }

    public void RefreshDerived()
    {
        var s = Snapshot();
        SlowMotionBlock = GameSlowMotionCommandBuilder.BuildEnableSlowMotionBlock(
            s.ObsCaptureFramerate, s.SupersamplingMultiplier, s.HideHudInCfg);
        RestoreBlock = GameSlowMotionCommandBuilder.BuildRestoreBlock(s.HideHudInCfg);
        CfgCommandBlock = BuildCfgCommandBlock(s);

        if (string.IsNullOrWhiteSpace(s.GameRootPath) || string.IsNullOrWhiteSpace(s.RamDiskWatchDirectory))
        {
            JunctionState = string.Empty;
            return;
        }

        try
        {
            var paths = MomentumDirectoryLinkService.ResolvePaths(s.GameRootPath, s.RamDiskWatchDirectory);
            JunctionState = MomentumDirectoryLinkService.DescribeLinkState(paths);
        }
        catch (Exception ex)
        {
            JunctionState = ex.Message;
        }

        NotifyPathCommandsCanExecuteChanged();
    }

    private void NotifyPathCommandsCanExecuteChanged()
    {
        CreateJunctionCommand.NotifyCanExecuteChanged();
        RemoveJunctionCommand.NotifyCanExecuteChanged();
    }

    private bool CanCreateJunction() =>
        HasConfiguredPaths() &&
        !string.IsNullOrWhiteSpace(MovieSequenceName) &&
        !string.IsNullOrWhiteSpace(StartMovieHotkey) &&
        !string.IsNullOrWhiteSpace(EndMovieHotkey);

    private bool CanRemoveJunction() => HasConfiguredPaths();

    private bool HasConfiguredPaths()
    {
        var gameRoot = GameRootPath?.Trim() ?? string.Empty;
        var watch = RamDiskWatchDirectory?.Trim() ?? string.Empty;
        if (string.IsNullOrWhiteSpace(gameRoot) || string.IsNullOrWhiteSpace(watch))
            return false;

        try
        {
            return Directory.Exists(gameRoot) && Directory.Exists(watch);
        }
        catch
        {
            return false;
        }
    }

    private static string BuildCfgCommandBlock(UserSettings s)
    {
        var multiplier = Math.Clamp(s.SupersamplingMultiplier, 1, 120);
        var hostFr = multiplier * ProjectConstants.FinalOutputFramerate;
        var startMovieCmd = WatchDirectoryHelper.BuildGameStartmovieCommand(s.MovieSequenceName);
        return
            $"{CfgGeneratorService.GameExecCommand}\n" +
            $"\n" +
            $"超采样 {multiplier}x（host_framerate {hostFr} → 成片 60fps）\n" +
            $"游戏内：{startMovieCmd}\n" +
            $"快捷键：{s.StartMovieHotkey.Trim()} = startmovie，{s.EndMovieHotkey.Trim()} = endmovie";
    }

    [RelayCommand]
    private void BrowseVideoOutput() => PickFolder("选择成片输出目录", VideoOutputDirectory, v => VideoOutputDirectory = v);

    [RelayCommand]
    private void BrowseWatchDirectory() => PickFolder("选择 TGA 监视目录", RamDiskWatchDirectory, v => RamDiskWatchDirectory = v);

    [RelayCommand]
    private void BrowseGameRoot() => PickFolder("选择游戏根目录", GameRootPath, v => GameRootPath = v);

    private static void PickFolder(string title, string current, Action<string> apply)
    {
        var dialog = new Microsoft.Win32.OpenFolderDialog { Title = title };
        if (!string.IsNullOrWhiteSpace(current) && Directory.Exists(current))
            dialog.InitialDirectory = current;

        if (dialog.ShowDialog() == true)
            apply(dialog.FolderName);
    }

    [RelayCommand]
    private void CopyCfgCommand()
    {
        System.Windows.Clipboard.SetText(CfgGeneratorService.GameExecCommand);
        StatusText = "已复制 CFG 指令";
    }

    [RelayCommand]
    private void CopySlowMotion()
    {
        System.Windows.Clipboard.SetText(SlowMotionBlock);
        StatusText = "已复制慢放指令";
    }

    [RelayCommand]
    private void CopyRestore()
    {
        System.Windows.Clipboard.SetText(RestoreBlock);
        StatusText = "已复制恢复指令";
    }

    [RelayCommand(CanExecute = nameof(CanCreateJunction))]
    private void CreateJunction()
    {
        try
        {
            if (!CanCreateJunction())
                throw new InvalidOperationException("请先配置有效的游戏根目录与 TGA 监视目录，并填写序列名与快捷键。");

            var s = Snapshot();
            var paths = MomentumDirectoryLinkService.ResolvePaths(s.GameRootPath!, s.RamDiskWatchDirectory);
            MomentumDirectoryLinkService.CreateLink(paths, overwriteRamCopy: true);

            var result = CfgGeneratorService.Generate(s, s.GameRootPath!);
            _settings = s;
            _store.Save(_settings);

            System.Windows.Clipboard.SetText(CfgGeneratorService.GameExecCommand);
            StatusText = $"Junction 已创建，{Path.GetFileName(result.CfgFilePath)} 已生成，CFG 指令已复制";
            RefreshDerived();
        }
        catch (Exception ex)
        {
            StatusText = $"创建失败：{ex.Message}";
        }
    }

    [RelayCommand(CanExecute = nameof(CanRemoveJunction))]
    private void RemoveJunction()
    {
        try
        {
            if (!CanRemoveJunction())
                throw new InvalidOperationException("需要有效的游戏根目录与监视目录。");

            var s = Snapshot();
            var paths = MomentumDirectoryLinkService.ResolvePaths(s.GameRootPath!, s.RamDiskWatchDirectory);
            MomentumDirectoryLinkService.RemoveLink(paths);
            StatusText = "Junction 已取消";
            RefreshDerived();
        }
        catch (Exception ex)
        {
            StatusText = $"取消 Junction 失败：{ex.Message}";
        }
    }

    [RelayCommand]
    private void OpenImDisk()
    {
        try
        {
            var exe = Path.Combine(AppContext.BaseDirectory, "ImDisk", "RamDiskUI.exe");
            if (!File.Exists(exe))
            {
                StatusText = "未找到 ImDisk：请确认 tools/ImDisk 已复制到输出目录。";
                return;
            }

            Process.Start(new ProcessStartInfo
            {
                FileName = exe,
                UseShellExecute = true,
                WorkingDirectory = Path.GetDirectoryName(exe)!,
            });
            StatusText = "已打开 ImDisk";
        }
        catch (Exception ex)
        {
            StatusText = $"打开 ImDisk 失败：{ex.Message}";
        }
    }
}
