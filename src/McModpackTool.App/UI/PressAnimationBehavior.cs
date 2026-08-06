using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using System.Windows.Media.Animation;

namespace McModpackTool.App.UI;

public static class PressAnimationBehavior
{
    public static void OnPress(object sender, MouseButtonEventArgs e)
    {
        if (sender is Button { IsEnabled: true } button)
            Animate(button, 0.965, 70);
    }

    public static void OnRelease(object sender, RoutedEventArgs e)
    {
        if (sender is Button button)
            Animate(button, 1.0, 140);
    }

    private static void Animate(Button button, double value, int milliseconds)
    {
        ScaleTransform transform;
        if (button.RenderTransform is ScaleTransform current)
        {
            bool isLocal = button.ReadLocalValue(UIElement.RenderTransformProperty) != DependencyProperty.UnsetValue;
            if (!isLocal || current.IsFrozen)
            {
                transform = current.CloneCurrentValue();
                button.RenderTransform = transform;
            }
            else
            {
                transform = current;
            }
        }
        else
        {
            transform = new ScaleTransform(1, 1);
            button.RenderTransform = transform;
        }
        button.RenderTransformOrigin = new Point(0.5, 0.5);

        var easing = new CubicEase { EasingMode = EasingMode.EaseOut };
        var animation = new DoubleAnimation(value, TimeSpan.FromMilliseconds(milliseconds))
        {
            EasingFunction = easing,
            FillBehavior = FillBehavior.HoldEnd
        };
        transform.BeginAnimation(ScaleTransform.ScaleXProperty, animation, HandoffBehavior.SnapshotAndReplace);
        transform.BeginAnimation(ScaleTransform.ScaleYProperty, animation, HandoffBehavior.SnapshotAndReplace);
    }
}
