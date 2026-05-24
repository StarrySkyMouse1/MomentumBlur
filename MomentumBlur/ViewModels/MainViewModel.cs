using mmod_record.Services;

namespace mmod_record.ViewModels;

public sealed class MainViewModel : IAsyncDisposable
{
    public SettingsViewModel Settings { get; }

    public RecordComposeViewModel Record { get; }

    public MainViewModel(IDialogService dialogs)
    {
        Settings = new SettingsViewModel(dialogs);
        Record = new RecordComposeViewModel(Settings, dialogs);
    }

    public async ValueTask DisposeAsync() => await Record.DisposeAsync();
}
