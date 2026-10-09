using Avalonia;
using Avalonia.Rendering.Composition.Animations;
using Avalonia.Animation.Easings;
using Avalonia.Controls;
using Avalonia.Rendering.Composition;
using Avalonia.Threading;
using static Neruna.Core.Localization.Texts;

namespace Neruna.Desktop.Views;

/// <summary>Shown while the database, accounts and the main window load (graphics will follow).</summary>
internal sealed partial class SplashWindow : Window
{
    public SplashWindow()
    {
        InitializeComponent();
        // The version of this build (e.g. "Beta 0.1.1"), not a fixed text.
        this.FindControl<TextBlock>("VersionText")!.Text = T("Beta ") + Infrastructure.UpdateService.AppVersion;
    }

    public string Status
    {
        set => this.FindControl<TextBlock>("StatusText")!.Text = value;
    }

    /// <summary>Shows the next step and lets the UI thread draw it before the (blocking) step starts.</summary>
    public async Task ShowStatusAsync(string text)
    {
        Status = text;
        await Dispatcher.UIThread.InvokeAsync(() => { }, DispatcherPriority.Render);
        await Task.Delay(30);
    }

    protected override void OnOpened(EventArgs e)
    {
        base.OnOpened(e);

        // Style animations run on the UI thread and froze while the main window was built; a composition
        // animation is driven by the render thread instead.
        var spinner = this.FindControl<Avalonia.Controls.Shapes.Path>("Spinner")!;
        if (ElementComposition.GetElementVisual(spinner) is not { } visual)
        {
            return;
        }

        visual.CenterPoint = new Vector3D(spinner.Width / 2, spinner.Height / 2, 0);
        var rotation = visual.Compositor.CreateScalarKeyFrameAnimation();
        rotation.Target = "RotationAngle";
        rotation.InsertKeyFrame(0f, 0f);
        rotation.InsertKeyFrame(1f, (float)(2 * Math.PI), new LinearEasing());
        rotation.Duration = TimeSpan.FromSeconds(0.9);
        rotation.IterationBehavior = AnimationIterationBehavior.Forever;
        visual.StartAnimation("RotationAngle", rotation);
    }
}
