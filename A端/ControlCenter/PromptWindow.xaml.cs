using System.Windows;

namespace ControlCenter;

public partial class PromptWindow : Window
{
  public string ResultText => InputBox.Text;

  public PromptWindow(string title, string message, string defaultText)
  {
    InitializeComponent();
    Title = title;
    TitleText.Text = title;
    MessageText.Text = message;
    InputBox.Text = defaultText;
    Loaded += (_, _) =>
    {
      InputBox.Focus();
      InputBox.SelectAll();
    };
  }

  public static string? ShowDialog(Window owner, string title, string message, string defaultText)
  {
    var win = new PromptWindow(title, message, defaultText) { Owner = owner };
    return win.ShowDialog() == true ? win.ResultText : null;
  }

  private void Ok_Click(object sender, RoutedEventArgs e)
  {
    DialogResult = true;
    Close();
  }
}
