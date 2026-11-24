using System.Net.NetworkInformation;
using Avalonia.Threading;
using Microsoft.Extensions.Logging;

namespace Neruna.Desktop.ViewModels;

/// <summary>
/// Back from standby (or on another network, e.g. from home to the office): the connections of before are dead, but
/// nothing tells the program – waiting inboxes would only notice after minutes, and the next sync would come with the
/// timer. So the waking is noticed (the clock jumped ahead of the timer, or the network changed) and the inboxes are
/// watched anew right away, followed by a sync.
/// </summary>
internal sealed partial class MainWindowViewModel
{
    private static readonly TimeSpan WakeCheckInterval = TimeSpan.FromSeconds(15);

    // More than this between two checks: the computer was asleep.
    private static readonly TimeSpan WakeGap = TimeSpan.FromMinutes(1);

    // The network reports several changes when it comes up (adapter, address, VPN): one reaction for all.
    private static readonly TimeSpan NetworkSettle = TimeSpan.FromSeconds(3);

    private DispatcherTimer? _wakeTimer;
    private DateTime _lastWakeCheck;
    private DispatcherTimer? _networkSettle;
    private DateTime _lastWake = DateTime.MinValue;

    private void StartWakeWatch()
    {
        if (_wakeTimer is not null)
        {
            return;
        }

        _lastWakeCheck = DateTime.UtcNow;
        _wakeTimer = new DispatcherTimer { Interval = WakeCheckInterval };
        _wakeTimer.Tick += (_, _) =>
        {
            var now = DateTime.UtcNow;
            var gap = now - _lastWakeCheck;
            _lastWakeCheck = now;
            if (gap > WakeGap)
            {
                _logger.LogInformation("Back after {Gap} (standby): reconnecting", gap);
                _ = WokeAsync();
            }
        };
        _wakeTimer.Start();

        NetworkChange.NetworkAddressChanged += (_, _) => Dispatcher.UIThread.Post(NetworkChanged);
    }

    private void NetworkChanged()
    {
        if (_networkSettle is null)
        {
            _networkSettle = new DispatcherTimer { Interval = NetworkSettle };
            _networkSettle.Tick += (_, _) =>
            {
                _networkSettle.Stop();
                if (NetworkInterface.GetIsNetworkAvailable())
                {
                    _logger.LogInformation("Network changed: reconnecting");
                    _ = WokeAsync();
                }
            };
        }

        _networkSettle.Stop();
        _networkSettle.Start();
    }

    private async Task WokeAsync()
    {
        // Standby and the network coming up again report the same waking: once is enough.
        if (DateTime.UtcNow - _lastWake < TimeSpan.FromMinutes(1))
        {
            return;
        }

        _lastWake = DateTime.UtcNow;
        try
        {
            await UpdatePushAsync();
            await SyncAsync();
        }
        catch (Exception ex) when (ex is not OutOfMemoryException)
        {
            _logger.LogInformation(ex, "Reconnecting after standby failed");
        }
    }
}
