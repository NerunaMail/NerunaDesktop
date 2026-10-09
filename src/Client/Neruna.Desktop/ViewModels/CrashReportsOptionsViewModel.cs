using CommunityToolkit.Mvvm.ComponentModel;
using Neruna.Core.Diagnostics;

namespace Neruna.Desktop.ViewModels;

/// <summary>Settings → Info → Absturzberichte: ask (default), always send, never send.</summary>
internal sealed partial class CrashReportsOptionsViewModel : ViewModelBase
{
    private readonly CrashReportService _service;
    private bool _loaded;

    public CrashReportsOptionsViewModel(CrashReportService service)
    {
        _service = service;
        _ = LoadAsync();
    }

    /// <summary>0 = ask, 1 = always, 2 = never (order of the choices in the view).</summary>
    [ObservableProperty]
    public partial int ModeIndex { get; set; }

    private async Task LoadAsync()
    {
        ModeIndex = (int)await _service.GetModeAsync();
        _loaded = true;
    }

    partial void OnModeIndexChanged(int value)
    {
        if (_loaded && value >= 0)
        {
            _ = _service.SetModeAsync((CrashReportMode)value);
        }
    }
}
