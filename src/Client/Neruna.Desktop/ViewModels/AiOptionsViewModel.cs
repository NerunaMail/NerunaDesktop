using System.Collections.ObjectModel;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neruna.Core.Ai;
using Neruna.Core.Auth;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.ViewModels;

/// <summary>
/// Settings → KI: connect an AI provider – with the user's own subscription (sign-in) or an API key – and choose the
/// model. Once connected, the AI functions (e.g. "Zusammenfassen") show in the app.
/// </summary>
internal sealed partial class AiOptionsViewModel : ViewModelBase
{
    private readonly AiController _ai;
    private Action? _cancelSignIn;
    private bool _loadingModels;

    public AiOptionsViewModel(AiController ai)
    {
        _ai = ai;
        Providers = ai.Providers;
        SelectedProvider = Providers.FirstOrDefault();
        ai.Changed += (_, _) => Avalonia.Threading.Dispatcher.UIThread.Post(Refresh);
    }

    public IReadOnlyList<IAiProvider> Providers { get; }

    /// <summary>Only shown when there is a choice.</summary>
    public bool HasProviderChoice => Providers.Count > 1;

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(SignInText), nameof(SignInHelp), nameof(CanSignIn), nameof(CanUseApiKey), nameof(PrivacyText))]
    public partial IAiProvider? SelectedProvider { get; set; }

    public bool CanSignIn => SelectedProvider?.SignInName is not null;

    public bool CanUseApiKey => SelectedProvider?.SupportsApiKey == true;

    public string SignInText => F("Mit {0} anmelden", SelectedProvider?.SignInName ?? string.Empty);

    public string SignInHelp => F("Nutzt Ihr {0}-Abo: zählt zu dessen Kontingent, ohne zusätzliche Kosten. Die Anmeldung öffnet sich im Browser.", SelectedProvider?.SignInName ?? string.Empty);

    public string PrivacyText => F("Neruna schickt nur dann Text an {0}, wenn Sie eine KI-Funktion auslösen – direkt von diesem Gerät, ohne Umweg über Neruna. Dafür gelten die Datenschutzbestimmungen von {0}.",
        (_ai.Connection is { } c ? _ai.Provider(c.ProviderId) : SelectedProvider)?.Name ?? string.Empty);

    [ObservableProperty]
    public partial string ApiKey { get; set; } = string.Empty;

    [ObservableProperty]
    [NotifyCanExecuteChangedFor(nameof(SignInCommand), nameof(ConnectApiKeyCommand), nameof(DisconnectCommand))]
    public partial bool IsBusy { get; set; }

    /// <summary>The browser sign-in is open: it can be cancelled.</summary>
    [ObservableProperty]
    public partial bool IsSigningIn { get; set; }

    [ObservableProperty]
    public partial string? Error { get; set; }

    public bool IsConnected => _ai.IsConnected;

    public bool IsNotConnected => !IsConnected;

    public string ConnectedText => _ai.Connection is not { } c || _ai.Provider(c.ProviderId) is not { } p
        ? string.Empty
        : c.Method == AiAuthMethod.SignIn
            ? F("Verbunden mit {0}{1} – nutzt Ihr Abo.", p.SignInName ?? p.Name, c.Account is { } account ? $" ({account})" : string.Empty)
            : F("Verbunden mit {0} über einen API-Schlüssel.", p.Name);

    public ObservableCollection<AiModel> Models { get; } = [];

    [ObservableProperty]
    public partial AiModel? SelectedModel { get; set; }

    public async Task ReloadAsync() => await _ai.LoadAsync();

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task SignInAsync()
    {
        if (SelectedProvider is not { } provider)
        {
            return;
        }

        using var cancel = new CancellationTokenSource();
        _cancelSignIn = cancel.Cancel;
        IsSigningIn = true;
        await RunAsync(() => _ai.SignInAsync(provider.Id, cancel.Token));
        IsSigningIn = false;
        _cancelSignIn = null;
    }

    [RelayCommand]
    private void CancelSignIn() => _cancelSignIn?.Invoke();

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private async Task ConnectApiKeyAsync()
    {
        if (SelectedProvider is { } provider)
        {
            await RunAsync(() => _ai.ConnectApiKeyAsync(provider.Id, ApiKey));
            if (Error is null)
            {
                ApiKey = string.Empty;
            }
        }
    }

    [RelayCommand(CanExecute = nameof(IsIdle))]
    private Task DisconnectAsync() => RunAsync(() => _ai.DisconnectAsync());

    [RelayCommand]
    private void OpenApiKeyHelp()
    {
        if (SelectedProvider?.ApiKeyHelp is { } url)
        {
            _ = Browser?.OpenAsync(url);
        }
    }

    /// <summary>Set by the shell: opens a page in the system browser.</summary>
    public IBrowserLauncher? Browser { get; set; }

    private bool IsIdle() => !IsBusy;

    private async Task RunAsync(Func<Task> action)
    {
        Error = null;
        IsBusy = true;
        try
        {
            await action();
        }
        catch (AiException ex)
        {
            Error = ex.Message;
        }
        catch (OAuthException ex)
        {
            Error = ex.Message;
        }
        catch (OperationCanceledException)
        {
            // Cancelled by the user: nothing to report.
        }
        catch (HttpRequestException)
        {
            Error = T("Der KI-Anbieter ist gerade nicht erreichbar.");
        }
        finally
        {
            IsBusy = false;
        }
    }

    // Connection changed: texts, and the models of the new connection.
    private void Refresh()
    {
        OnPropertyChanged(nameof(IsConnected));
        OnPropertyChanged(nameof(IsNotConnected));
        OnPropertyChanged(nameof(ConnectedText));
        OnPropertyChanged(nameof(PrivacyText));
        if (_ai.Connection is { } connection && _ai.Provider(connection.ProviderId) is { } provider)
        {
            SelectedProvider = provider;
            _ = LoadModelsAsync();
        }
        else
        {
            Models.Clear();
        }
    }

    private async Task LoadModelsAsync()
    {
        if (_loadingModels)
        {
            return;
        }

        _loadingModels = true;
        try
        {
            var models = await _ai.ListModelsAsync();
            Models.Clear();
            foreach (var model in models)
            {
                Models.Add(model);
            }

            SelectedModel = models.FirstOrDefault(m => m.Id == _ai.Connection?.Model) ?? models.FirstOrDefault();
        }
        catch (Exception ex) when (ex is AiException or HttpRequestException or OAuthException)
        {
            Error = ex.Message;
        }
        finally
        {
            _loadingModels = false;
        }
    }

    partial void OnSelectedModelChanged(AiModel? value)
    {
        if (value is not null && !_loadingModels && value.Id != _ai.Connection?.Model)
        {
            _ = _ai.SetModelAsync(value.Id);
        }
        else if (value is not null && _loadingModels && _ai.Connection is { Model: null })
        {
            _ = _ai.SetModelAsync(value.Id);
        }
    }
}
