using Mmod.Core.Services;

namespace Mmod.App.ViewModels;

/// <summary>
/// 壳层 ViewModel：持有设置、合成与任务三个页面 VM，并把「顶层工作模式」的
/// 唯一真相源（<see cref="SettingsViewModel.CaptureMode"/>）与运行态安全门连起来。
/// 不持有第二份模式状态。
/// </summary>
public sealed class MainViewModel
{
    public MainViewModel()
    {
        var store = new UserSettingsStore();
        Settings = new SettingsViewModel(store);
        Compose = new ComposeViewModel(Settings);
        Tasks = new TasksViewModel(Settings);

        // 模式切换安全门：OBS 批处理 / TGA 手工管线 / 无人值守任务处于运行或收尾时，
        // 只读汇总出阻塞原因交给设置 VM，由它拒绝切换并给出可见原因。
        Settings.CaptureModeSwitchBlocker = () =>
            Compose.DescribeCaptureModeSwitchBlock()
            ?? Tasks.DescribeCaptureModeSwitchBlock();
    }

    public SettingsViewModel Settings { get; }
    public ComposeViewModel Compose { get; }
    public TasksViewModel Tasks { get; }
}
