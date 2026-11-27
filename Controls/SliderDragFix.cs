using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;

namespace HwPulse;

/// <summary>
/// En WPF, al hacer clic en la pista de un slider (fuera del círculo) el valor salta a ese punto, pero no se
/// puede seguir arrastrando hasta soltar y volver a agarrar el círculo. Aquí, para TODOS los sliders de la app:
/// el clic en la pista coloca el círculo bajo el ratón y empieza el arrastre en el mismo gesto.
/// </summary>
public static class SliderDragFix
{
    public static void Install() =>
        EventManager.RegisterClassHandler(typeof(Slider), UIElement.PreviewMouseLeftButtonDownEvent,
            new MouseButtonEventHandler(OnPreviewMouseDown), handledEventsToo: true);

    private static void OnPreviewMouseDown(object sender, MouseButtonEventArgs e)
    {
        if (sender is not Slider slider || !slider.IsEnabled) return;
        // Clic sobre el propio círculo: WPF ya lo arrastra bien
        if (e.OriginalSource is DependencyObject d && FindAncestor<Thumb>(d) != null) return;
        if (slider.Template?.FindName("PART_Track", slider) is not Track track || track.Thumb is not Thumb thumb) return;

        slider.Focus();
        slider.Value = track.ValueFromPoint(e.GetPosition(track));
        slider.UpdateLayout(); // el círculo ya está bajo el ratón antes de empezar a arrastrarlo
        thumb.RaiseEvent(new MouseButtonEventArgs(e.MouseDevice, e.Timestamp, MouseButton.Left)
        {
            RoutedEvent = UIElement.MouseLeftButtonDownEvent,
            Source = thumb,
        });
        e.Handled = true;
    }

    private static T? FindAncestor<T>(DependencyObject? d) where T : DependencyObject
    {
        while (d != null)
        {
            if (d is T t) return t;
            d = d is System.Windows.Media.Visual or System.Windows.Media.Media3D.Visual3D
                ? System.Windows.Media.VisualTreeHelper.GetParent(d) : LogicalTreeHelper.GetParent(d);
        }
        return null;
    }
}
