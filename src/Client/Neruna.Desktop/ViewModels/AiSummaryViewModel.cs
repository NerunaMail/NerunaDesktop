using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neruna.Core.Ai;

namespace Neruna.Desktop.ViewModels;

/// <summary>The AI summary above a message: written while it arrives, closable, cancelled with the message.</summary>
internal sealed partial class AiSummaryViewModel(Action close) : ViewModelBase, IDisposable
{
    private readonly CancellationTokenSource _cancel = new();
    private bool _done;

    [ObservableProperty]
    public partial string Text { get; set; } = string.Empty;

    [ObservableProperty]
    public partial bool IsBusy { get; set; } = true;

    [ObservableProperty]
    public partial string? Error { get; set; }

    public async Task RunAsync(IAsyncEnumerable<string> answer)
    {
        ArgumentNullException.ThrowIfNull(answer);
        try
        {
            await foreach (var piece in answer.WithCancellation(_cancel.Token))
            {
                Text += piece;
            }
        }
        catch (AiException ex)
        {
            Error = ex.Message;
        }
        catch (HttpRequestException)
        {
            Error = Neruna.Core.Localization.Texts.T("Die KI ist gerade nicht erreichbar.");
        }
        catch (OperationCanceledException) when (_cancel.IsCancellationRequested)
        {
        }
        finally
        {
            IsBusy = false;
            Dispose();
        }
    }

    /// <summary>Stops a summary still being written (nothing to do once it is complete).</summary>
    public void Cancel()
    {
        if (!_done)
        {
            _cancel.Cancel();
        }
    }

    public void Dispose()
    {
        _done = true;
        _cancel.Dispose();
    }

    [RelayCommand]
    private void Close()
    {
        Cancel();
        close();
    }
}
