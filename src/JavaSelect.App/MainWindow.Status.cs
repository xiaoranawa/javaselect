using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Windows.UI.ViewManagement;

namespace JavaSelect.App;

public sealed partial class MainWindow
{
    private Storyboard? statusAnimation;
    private bool statusHiding, statusCanClose;
    private readonly UISettings animationSettings = new();

    private void DismissStatus()
    {
        if (statusHiding || StatusBar.Visibility != Visibility.Visible) return;
        statusDismiss.Stop(); statusHiding = true; StatusBar.IsHitTestVisible = false;
        var opacity = StatusBar.Opacity;
        var offset = ((TranslateTransform)StatusBar.RenderTransform).Y;
        StopStatusAnimation();
        AnimateStatus(opacity, 0, offset, 18, 160, () =>
        {
            statusCanClose = true;
            try { StatusBar.IsOpen = false; }
            finally { statusCanClose = false; }
            StatusBar.Visibility = Visibility.Collapsed;
            statusHiding = false; statusHovered = false;
        });
    }

    private void AnimateStatus(double fromOpacity, double toOpacity, double fromY, double toY, int milliseconds, Action completed)
    {
        if (!animationSettings.AnimationsEnabled) { completed(); return; }
        var animation = new Storyboard();
        Add("Opacity", fromOpacity, toOpacity);
        Add("(UIElement.RenderTransform).(TranslateTransform.Y)", fromY, toY);
        animation.Completed += (_, _) =>
        {
            if (!ReferenceEquals(statusAnimation, animation)) return;
            statusAnimation = null; animation.Stop(); completed();
        };
        statusAnimation = animation;
        animation.Begin();

        void Add(string property, double from, double to)
        {
            var change = new DoubleAnimation
            {
                From = from, To = to, Duration = new Duration(TimeSpan.FromMilliseconds(milliseconds)),
                EasingFunction = new CubicEase { EasingMode = EasingMode.EaseOut }
            };
            Storyboard.SetTarget(change, StatusBar); Storyboard.SetTargetProperty(change, property);
            animation.Children.Add(change);
        }
    }

    private void StopStatusAnimation()
    {
        var animation = statusAnimation; statusAnimation = null; animation?.Stop();
    }
}
