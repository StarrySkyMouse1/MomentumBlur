using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Mmod.Core.Models;
using Mmod.Core.Services;

namespace Mmod.App.ViewModels;

/// <summary>
/// One parameter of a quality module card, bound to a NumberBox. Writes the
/// clamped value back into the module config and notifies the settings VM.
/// </summary>
public sealed partial class QualityParameterViewModel : ObservableObject
{
    private readonly VideoProcessingModuleConfig _config;
    private bool _loading;

    public QualityParameterViewModel(VideoProcessingParameterDefinition parameter, VideoProcessingModuleConfig config)
    {
        Parameter = parameter;
        _config = config;
        _loading = true;
        Value = config.Parameters.TryGetValue(parameter.Key, out var v) ? v : parameter.DefaultValue;
        _loading = false;
    }

    public VideoProcessingParameterDefinition Parameter { get; }
    public string DisplayName => Parameter.DisplayName;
    public string Description => Parameter.Description ?? string.Empty;
    public double Minimum => Parameter.Min;
    public double Maximum => Parameter.Max;
    public double Step => Parameter.Step;
    public string? UnitLabel => string.IsNullOrWhiteSpace(Parameter.Unit) ? null : Parameter.Unit;

    [ObservableProperty]
    private double value;

    partial void OnValueChanged(double value)
    {
        if (_loading)
            return;
        var clamped = Math.Clamp(value, Parameter.Min, Parameter.Max);
        if (Math.Abs(clamped - value) > 1e-9)
        {
            _loading = true;
            Value = clamped;
            _loading = false;
            return;
        }

        _config.Parameters[Parameter.Key] = clamped;
        Changed?.Invoke();
    }

    /// <summary>Restores the default without triggering change callbacks.</summary>
    public void Restore(double defaultValue)
    {
        _loading = true;
        _config.Parameters[Parameter.Key] = defaultValue;
        Value = defaultValue;
        _loading = false;
    }

    public event Action? Changed;
}

/// <summary>One dynamic quality-module card (checkbox + description + parameters).</summary>
public sealed partial class QualityModuleViewModel : ObservableObject
{
    private readonly VideoProcessingModuleConfig _config;
    private readonly Action _onAnyChange;
    private bool _loading;

    public QualityModuleViewModel(VideoProcessingModuleDefinition definition, VideoProcessingModuleConfig config, Action onAnyChange)
    {
        Definition = definition;
        _config = config;
        _onAnyChange = onAnyChange;
        _loading = true;
        IsEnabled = config.Enabled;
        foreach (var p in definition.Parameters)
        {
            var vm = new QualityParameterViewModel(p, config);
            vm.Changed += OnAnyChange;
            Parameters.Add(vm);
        }
        _loading = false;
    }

    public VideoProcessingModuleDefinition Definition { get; }
    public string DisplayName => Definition.DisplayName;
    public string Description => Definition.Description;
    public string RiskDescription => Definition.RiskDescription;
    public ObservableCollection<QualityParameterViewModel> Parameters { get; } = [];

    [ObservableProperty]
    private bool isEnabled;

    partial void OnIsEnabledChanged(bool value)
    {
        if (_loading)
            return;
        _config.Enabled = value;
        OnAnyChange();
    }

    private void OnAnyChange() => _onAnyChange();

    [RelayCommand]
    private void RestoreDefaults()
    {
        _loading = true;
        var defaults = VideoProcessorCatalog.BuildDefaultConfig(Definition);
        _config.Enabled = defaults.Enabled;
        _config.Order = defaults.Order;
        foreach (var (key, value) in defaults.Parameters)
            _config.Parameters[key] = value;

        IsEnabled = _config.Enabled;
        foreach (var vm in Parameters)
        {
            vm.Restore(defaults.Parameters[vm.Parameter.Key]);
        }
        _loading = false;
        OnAnyChange();
    }
}
