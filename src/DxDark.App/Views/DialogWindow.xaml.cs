using System.Windows;
using System.Windows.Input;

namespace DxDark.App.Views;

/// <summary>DX Dark's own message, question and text-entry dialog (see <see cref="Infrastructure.Dialogs"/>).</summary>
public partial class DialogWindow : Window
{
    public DialogWindow(string title, string message, string okText, string? cancelText, bool destructive, string? input)
    {
        InitializeComponent();
        Title = title;
        TitleText.Text = title;
        MessageText.Text = message;
        MessageText.Visibility = string.IsNullOrEmpty(message) ? Visibility.Collapsed : Visibility.Visible;
        OkButton.Content = okText;
        if (destructive)
        {
            OkButton.Style = (Style)FindResource("DangerButton");
        }

        if (cancelText is null)
        {
            CancelButton.Visibility = Visibility.Collapsed;
            OkButton.IsCancel = true; // Esc closes a plain message too
        }
        else
        {
            CancelButton.Content = cancelText;
        }

        if (input is not null)
        {
            InputBox.Text = input;
            InputBox.Visibility = Visibility.Visible;
        }

        Loaded += (_, _) =>
        {
            if (input is not null)
            {
                InputBox.Focus();
                InputBox.SelectAll();
            }
            else
            {
                OkButton.Focus();
            }
        };
    }

    public string InputText => InputBox.Text;

    /// <summary>Covers <paramref name="owner"/>'s content (dimming it), or shows just the card when there is no owner.</summary>
    public void PlaceOver(Window? owner) => Infrastructure.ModalPlacement.Cover(this, Scrim, owner);

    private void OnOk(object sender, RoutedEventArgs e) => DialogResult = true;

    private void OnScrimClick(object sender, MouseButtonEventArgs e) => DialogResult = false;

    private void OnCardClick(object sender, MouseButtonEventArgs e) => e.Handled = true; // clicks inside the card keep it open
}
