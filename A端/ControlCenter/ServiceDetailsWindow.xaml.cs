using RemoteControl.Shared;
using System.Windows;

namespace ControlCenter;

public partial class ServiceDetailsWindow : Window
{
  public ServiceDetailsWindow(ServiceDetailsPayload details)
  {
    InitializeComponent();
    Title = $"服务属性 - {details.DisplayName}";
    TitleText.Text = details.DisplayName;
    NameText.Text = details.ServiceName;
    DisplayNameText.Text = details.DisplayName;
    StatusValue.Text = details.Status;
    StartTypeValue.Text = details.StartType;
    PidText.Text = details.ProcessId.ToString();
    AccountText.Text = details.Account;
    PathText.Text = details.ExecutablePath;
    DescriptionText.Text = details.Description;
    DependenciesText.Text = details.Dependencies.Count == 0
      ? "无"
      : string.Join(Environment.NewLine, details.Dependencies);
    DependentsText.Text = details.DependentServices.Count == 0
      ? "无"
      : string.Join(Environment.NewLine, details.DependentServices);
  }

  private void Close_Click(object sender, RoutedEventArgs e) => Close();
}
