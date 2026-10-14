using Avalonia.Media.Imaging;
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;
using Neruna.Core.Accounts;
using Neruna.Core.Cloud;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.ViewModels;

/// <summary>
/// "Aufs Handy übertragen" for one account: after the computer login a QR code with the PIN next to it, for the
/// Neruna app (Einstellungen → Konten → «Konto vom Computer übernehmen»).
/// </summary>
internal sealed partial class AccountTransferDialogViewModel(AccountTransferService transfer, Account account) : ViewModelBase
{
    public event EventHandler? Finished;

    public string Title { get; } = account.Title;

    public Infrastructure.LoginGate Login { get; } = new();

    [ObservableProperty]
    [NotifyPropertyChangedFor(nameof(HasCode))]
    public partial Bitmap? QrCode { get; set; }

    public bool HasCode => QrCode is not null;

    [ObservableProperty]
    public partial string? Pin { get; set; }

    /// <summary>Asks for the computer login, then shows the code (or closes when the login was not confirmed).</summary>
    public async Task StartAsync()
    {
        if (!await Login.VerifyAsync(T("Zum Übertragen eines Kontos aufs Handy bitte mit dem Login dieses Computers bestätigen.")))
        {
            if (Login.Error is null)
            {
                Finished?.Invoke(this, EventArgs.Empty); // cancelled
            }

            return;
        }

        var code = await transfer.CreateCodeAsync(account);
        // Error correction L (a screen does not get dirty) keeps the code coarse; whole pixels per module, about 500 pixels
        // wide and shown unscaled, keep it sharp enough for a phone camera.
        var data = new QRCoder.QRCodeGenerator().CreateQrCode(code.Code, QRCoder.QRCodeGenerator.ECCLevel.L);
        var png = new QRCoder.PngByteQRCode(data).GetGraphic(Math.Max(4, 500 / data.ModuleMatrix.Count));
        using var stream = new MemoryStream(png);
        QrCode = new Bitmap(stream);
        Pin = code.Pin;
    }

    [RelayCommand]
    private void Close() => Finished?.Invoke(this, EventArgs.Empty);
}
