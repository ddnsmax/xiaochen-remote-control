using System.Windows;

namespace ControlCenter;

public partial class ConfirmationWindow : Window
{
  private ConfirmationWindow(
    Window owner,
    string title,
    string message)
  {
    InitializeComponent();
    Owner = owner;
    Title = title;
    TitleText.Text = title;
    MessageText.Text = message;
  }

  public static bool Show(
    Window owner,
    string title,
    string message) =>
    new ConfirmationWindow(owner, title, message).ShowDialog() == true;

  private void Confirm_Click(object sender, RoutedEventArgs e)
  {
    DialogResult = true;
  }

  private void Cancel_Click(object sender, RoutedEventArgs e)
  {
    DialogResult = false;
  }
}
