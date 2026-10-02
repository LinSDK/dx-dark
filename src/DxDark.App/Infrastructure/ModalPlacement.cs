using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;

namespace DxDark.App.Infrastructure;

/// <summary>Places DX Dark's modal dialogs over the window they belong to.</summary>
public static class ModalPlacement
{
    /// <summary>
    /// Makes <paramref name="modal"/> cover <paramref name="owner"/>'s content, so its
    /// <paramref name="scrim"/> dims the window behind the card. Without a visible owner (e.g. when
    /// opened from the tray) only the card is shown, centered on the screen.
    /// </summary>
    public static void Cover(Window modal, Panel scrim, Window? owner)
    {
        if (owner is { IsVisible: true } && owner.WindowState != WindowState.Minimized
            && owner.Content is FrameworkElement content && PresentationSource.FromVisual(owner) is { CompositionTarget: { } target })
        {
            modal.Owner = owner;
            Matrix fromDevice = target.TransformFromDevice;
            Point topLeft = fromDevice.Transform(content.PointToScreen(new Point(0, 0)));
            Point bottomRight = fromDevice.Transform(content.PointToScreen(new Point(content.ActualWidth, content.ActualHeight)));
            modal.Left = topLeft.X;
            modal.Top = topLeft.Y;
            modal.Width = Math.Max(200, bottomRight.X - topLeft.X);
            modal.Height = Math.Max(150, bottomRight.Y - topLeft.Y);
            return;
        }

        scrim.Background = Brushes.Transparent;
        modal.SizeToContent = SizeToContent.WidthAndHeight;
        modal.WindowStartupLocation = WindowStartupLocation.CenterScreen;
        modal.Topmost = true;
    }

    /// <summary>The window a new dialog belongs to: the active DX Dark window (possibly another dialog), else the visible control panel.</summary>
    public static Window? OwnerWindow() =>
        Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w.IsActive && w.IsVisible)
        ?? Application.Current.Windows.OfType<Window>().FirstOrDefault(w => w is Views.MainWindow { IsVisible: true });
}
