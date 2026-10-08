using System.Text.Json;
using Avalonia;
using Avalonia.Controls;
using Avalonia.Threading;
using Neruna.Core;

namespace Neruna.Desktop.Infrastructure;

/// <summary>
/// Remembers window placement and column widths across restarts (setting "ui.layout"). Loaded once at startup, so
/// views can read synchronously; changes are written a moment after the last one (dragging a splitter changes the
/// width many times a second).
/// </summary>
internal sealed class UiLayout(ISettingsStore settings)
{
    private const string Key = "ui.layout";
    private static readonly TimeSpan SaveDelay = TimeSpan.FromSeconds(1);

    private Dictionary<string, double[]> _values = [];
    private DispatcherTimer? _timer;
    private bool _dirty;

    public async Task LoadAsync()
    {
        try
        {
            _values = JsonSerializer.Deserialize<Dictionary<string, double[]>>(await settings.GetAsync(Key) ?? "{}") ?? [];
        }
        catch (JsonException)
        {
            _values = [];
        }
    }

    public double[]? Get(string name) => _values.TryGetValue(name, out var values) ? values : null;

    public void Set(string name, params double[] values)
    {
        if (_values.TryGetValue(name, out var old) && old.SequenceEqual(values))
        {
            return;
        }

        _values[name] = values;
        _dirty = true;
        _timer ??= CreateTimer();
        _timer.Stop();
        _timer.Start();
    }

    /// <summary>Writes pending changes now (closing the window). Safe to call from the UI thread.</summary>
    public void Flush()
    {
        _timer?.Stop();
        if (!_dirty)
        {
            return;
        }

        _dirty = false;
        var json = JsonSerializer.Serialize(_values);

        // Off the UI thread: waiting there for the database would block its own continuations.
        Task.Run(() => settings.SetAsync(Key, json)).Wait(TimeSpan.FromSeconds(3));
    }

    private DispatcherTimer CreateTimer()
    {
        var timer = new DispatcherTimer { Interval = SaveDelay };
        timer.Tick += async (_, _) =>
        {
            timer.Stop();
            if (_dirty)
            {
                _dirty = false;
                await settings.SetAsync(Key, JsonSerializer.Serialize(_values));
            }
        };
        return timer;
    }

    /// <summary>
    /// Restores a window's size (and position, for the main window) and keeps track of changes. A position that is no
    /// longer on any screen (monitor unplugged) is ignored – the window is centred instead.
    /// </summary>
    public void TrackWindow(Window window, string name, bool withPosition)
    {
        ArgumentNullException.ThrowIfNull(window);
        RestoreWindow(window, name, withPosition);

        // Size and position of the normal (not maximized) window, so un-maximizing later returns there.
        var (x, y) = (window.Position.X, window.Position.Y);
        var (width, height) = (double.IsNaN(window.Width) ? window.ClientSize.Width : window.Width, double.IsNaN(window.Height) ? window.ClientSize.Height : window.Height);

        void Save() => Set(name, x, y, Math.Round(width), Math.Round(height), window.WindowState == WindowState.Maximized ? 1 : 0);

        window.PositionChanged += (_, e) =>
        {
            if (window.WindowState == WindowState.Normal && window.IsVisible)
            {
                (x, y) = (e.Point.X, e.Point.Y);
                Save();
            }
        };
        window.SizeChanged += (_, e) =>
        {
            if (window.WindowState == WindowState.Normal && window.IsVisible)
            {
                (width, height) = (e.NewSize.Width, e.NewSize.Height);
                Save();
            }
        };
        window.PropertyChanged += (_, e) =>
        {
            if (e.Property == Window.WindowStateProperty && window.WindowState != WindowState.Minimized)
            {
                Save();
            }
        };
        window.Closing += (_, _) => Flush();
    }

    private void RestoreWindow(Window window, string name, bool withPosition)
    {
        if (Get(name) is not [var x, var y, var width, var height, var maximized])
        {
            return;
        }

        var position = new PixelPoint((int)x, (int)y);
        var screens = window.Screens.All;
        var scale = screens.FirstOrDefault(s => s.Bounds.Contains(position))?.Scaling ?? 1;

        // Never larger than the screen it opens on, never smaller than the window allows.
        var area = screens.FirstOrDefault(s => s.Bounds.Contains(position))?.WorkingArea ?? screens.FirstOrDefault()?.WorkingArea;
        var maxWidth = area is { } a ? a.Width / scale : double.MaxValue;
        var maxHeight = area is { } b ? b.Height / scale : double.MaxValue;
        window.Width = Math.Clamp(width, window.MinWidth, Math.Max(window.MinWidth, maxWidth));
        window.Height = Math.Clamp(height, window.MinHeight, Math.Max(window.MinHeight, maxHeight));

        // The title bar must be reachable: at least a corner of 100 × 40 px on a connected screen.
        var titleBar = new PixelRect(position, new PixelSize((int)(100 * scale), (int)(40 * scale)));
        if (withPosition && screens.Any(s => s.WorkingArea.Intersects(titleBar)))
        {
            window.WindowStartupLocation = WindowStartupLocation.Manual;
            window.Position = position;
        }

        if (maximized > 0)
        {
            window.WindowState = WindowState.Maximized;
        }
    }

    /// <summary>Restores the widths of resizable grid columns (with a splitter) and remembers them when dragged.</summary>
    public void TrackColumns(Grid grid, string name, params int[] columns)
    {
        ArgumentNullException.ThrowIfNull(grid);
        if (Get(name) is { } widths && widths.Length == columns.Length)
        {
            for (var i = 0; i < columns.Length; i++)
            {
                grid.ColumnDefinitions[columns[i]].Width = new GridLength(Math.Clamp(widths[i], 120, 1200));
            }
        }

        foreach (var column in columns)
        {
            grid.ColumnDefinitions[column].PropertyChanged += (_, e) =>
            {
                if (e.Property == ColumnDefinition.WidthProperty)
                {
                    // The splitter sets pixel widths; ActualWidth would still be the old value at this moment.
                    Set(name, columns.Select(c => grid.ColumnDefinitions[c] is { Width.IsAbsolute: true } d ? Math.Round(d.Width.Value) : Math.Round(grid.ColumnDefinitions[c].ActualWidth)).ToArray());
                }
            };
        }
    }
}
