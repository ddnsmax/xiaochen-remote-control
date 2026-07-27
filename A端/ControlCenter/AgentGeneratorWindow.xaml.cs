using Microsoft.Win32;
using RemoteControl.Shared;
using System.IO;
using System.Windows;
using System.Windows.Media;

namespace ControlCenter;

public partial class AgentGeneratorWindow : Window
{
  private readonly string _settingsPath = Path.Combine(
    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
    "XiaoChenRemote",
    "agent-generator-host.txt");
  private string? _savedHost;
  private bool _loading;

  public AgentGeneratorWindow()
  {
    InitializeComponent();
    _loading = true;
    try
    {
      if (File.Exists(_settingsPath))
      {
        string stored = GeneratedAgentConfiguration.NormalizeHost(
          File.ReadAllText(_settingsPath));
        DomainBox.Text = stored;
        _savedHost = stored;
        GenerateButton.IsEnabled = true;
        SetStatus($"已保存域名：{stored}", success: true);
      }
    }
    catch { }
    finally
    {
      _loading = false;
    }
  }

  private void DomainBox_TextChanged(object sender, RoutedEventArgs e)
  {
    if (_loading) return;
    GenerateButton.IsEnabled =
      _savedHost is not null &&
      string.Equals(
        DomainBox.Text.Trim().TrimEnd('.'),
        _savedHost,
        StringComparison.OrdinalIgnoreCase);
    if (!GenerateButton.IsEnabled)
      SetStatus("域名已修改，请先点击“保存”。", success: false);
  }

  private void Save_Click(object sender, RoutedEventArgs e)
  {
    try
    {
      string host = GeneratedAgentConfiguration.NormalizeHost(DomainBox.Text);
      string? directory = Path.GetDirectoryName(_settingsPath);
      if (!string.IsNullOrWhiteSpace(directory))
        Directory.CreateDirectory(directory);
      File.WriteAllText(_settingsPath, host);
      _savedHost = host;
      _loading = true;
      DomainBox.Text = host;
      _loading = false;
      GenerateButton.IsEnabled = true;
      SetStatus($"已保存域名：{host}，现在可以生成被控端。", success: true);
    }
    catch (Exception ex)
    {
      GenerateButton.IsEnabled = false;
      MessageBox.Show(
        this,
        ex.Message,
        "域名无效",
        MessageBoxButton.OK,
        MessageBoxImage.Warning);
    }
  }

  private void Generate_Click(object sender, RoutedEventArgs e)
  {
    if (_savedHost is null || !GenerateButton.IsEnabled) return;
    var dialog = new SaveFileDialog
    {
      Title = "保存生成的B端",
      FileName = "B端.exe",
      DefaultExt = ".exe",
      Filter = "Windows 程序 (*.exe)|*.exe",
      AddExtension = true,
      OverwritePrompt = true
    };
    if (dialog.ShowDialog(this) != true) return;

    try
    {
      AgentPackageGenerator.Generate(_savedHost, dialog.FileName);
      SetStatus($"生成完成：{dialog.FileName}", success: true);
      MessageBox.Show(
        this,
        $"B端生成完成。\n域名：{_savedHost}\n端口：{NetworkDefaults.Port}",
        "生成成功",
        MessageBoxButton.OK,
        MessageBoxImage.Information);
      Close();
    }
    catch (Exception ex)
    {
      MessageBox.Show(
        this,
        $"生成失败：{ex.Message}",
        "生成被控端",
        MessageBoxButton.OK,
        MessageBoxImage.Error);
    }
  }

  private void Cancel_Click(object sender, RoutedEventArgs e) => Close();

  private void SetStatus(string message, bool success)
  {
    StatusText.Text = message;
    StatusText.Foreground = new SolidColorBrush(
      success
        ? Color.FromRgb(0x08, 0x7A, 0x45)
        : Color.FromRgb(0x98, 0x5D, 0x00));
  }
}
