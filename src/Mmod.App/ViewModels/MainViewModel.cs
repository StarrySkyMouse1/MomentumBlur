using Mmod.Core.Services;

namespace Mmod.App.ViewModels;

public sealed class MainViewModel
{
    public MainViewModel()
    {
        var store = new UserSettingsStore();
        Settings = new SettingsViewModel(store);
        Compose = new ComposeViewModel(Settings);
    }

    public SettingsViewModel Settings { get; }
    public ComposeViewModel Compose { get; }
}
