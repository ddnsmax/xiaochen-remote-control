using RemoteControl.Shared;
using System.Globalization;
using System.Windows;
using System.Windows.Controls;

namespace ControlCenter;

public partial class RegistryValueEditorWindow : Window
{
  private readonly RegistryValuePayload? _existing;
  public RegistryValueEditResult? Result { get; private set; }

  public RegistryValueEditorWindow(
    string valueKind,
    RegistryValuePayload? existing = null)
  {
    InitializeComponent();
    _existing = existing;
    NameBox.Text = existing is null
      ? SuggestName(valueKind)
      : existing.Name;
    SelectType(existing?.Type ?? valueKind);
    TypeBox.IsEnabled = existing is null;
    DataBox.Text = FormatEditorData(existing);
    UpdateHint();
    NameBox.Focus();
    NameBox.SelectAll();
  }

  private static string SuggestName(string valueKind) =>
    valueKind switch
    {
      "String" => "新字符串值",
      "ExpandString" => "新可扩充字符串值",
      "MultiString" => "新多字符串值",
      "Binary" => "新二进制值",
      "DWord" => "新 DWORD 值",
      "QWord" => "新 QWORD 值",
      _ => "新值"
    };

  private void SelectType(string kind)
  {
    foreach (ComboBoxItem item in TypeBox.Items)
    {
      if (!string.Equals(
            Convert.ToString(item.Tag),
            kind,
            StringComparison.OrdinalIgnoreCase)) continue;
      TypeBox.SelectedItem = item;
      return;
    }
    TypeBox.SelectedIndex = 0;
  }

  private static string FormatEditorData(RegistryValuePayload? value)
  {
    if (value is null) return string.Empty;
    return value.Type switch
    {
      "MultiString" => string.Join(
        Environment.NewLine,
        value.MultiStringValue ?? []),
      "Binary" or "None" => value.BinaryValue is { Length: > 0 } bytes
        ? Convert.ToHexString(bytes).Chunk(2)
          .Select(chars => new string(chars))
          .Aggregate((left, right) => left + " " + right)
        : string.Empty,
      "DWord" => "0x" +
        unchecked((uint)(value.IntegerValue ?? 0))
          .ToString("X8", CultureInfo.InvariantCulture),
      "QWord" => "0x" +
        unchecked((ulong)(value.IntegerValue ?? 0))
          .ToString("X16", CultureInfo.InvariantCulture),
      _ => value.StringValue ?? value.Data
    };
  }

  private string SelectedKind =>
    Convert.ToString((TypeBox.SelectedItem as ComboBoxItem)?.Tag) ?? "String";

  private void TypeBox_SelectionChanged(
    object sender,
    SelectionChangedEventArgs e) =>
    UpdateHint();

  private void UpdateHint()
  {
    if (FormatHint is null) return;
    FormatHint.Text = SelectedKind switch
    {
      "MultiString" => "每行一个字符串",
      "Binary" or "None" => "十六进制字节，例如：01 FF 20 7A",
      "DWord" => "十进制或 0x 开头的十六进制，最多32位",
      "QWord" => "十进制或 0x 开头的十六进制，最多64位",
      "ExpandString" => "可包含 %PATH% 等环境变量",
      _ => "普通文本"
    };
  }

  private void Ok_Click(object sender, RoutedEventArgs e)
  {
    try
    {
      string name = NameBox.Text.Trim();
      if (name.Length == 0)
        throw new InvalidOperationException("数值名称不能为空；默认值请直接修改现有“(默认)”项。");

      string kind = SelectedKind;
      string text = DataBox.Text;
      Result = kind switch
      {
        "MultiString" => new(
          name,
          kind,
          MultiStringValue: text.Replace("\r\n", "\n")
            .Split('\n', StringSplitOptions.None)
            .ToList()),
        "Binary" or "None" => new(
          name,
          kind,
          BinaryValue: ParseBinary(text)),
        "DWord" => new(
          name,
          kind,
          IntegerValue: unchecked((int)ParseUInt32(text))),
        "QWord" => new(
          name,
          kind,
          IntegerValue: unchecked((long)ParseUInt64(text))),
        _ => new(name, kind, StringValue: text)
      };
      DialogResult = true;
    }
    catch (Exception ex)
    {
      MessageBox.Show(
        this,
        ex.Message,
        "注册表值格式错误",
        MessageBoxButton.OK,
        MessageBoxImage.Warning);
    }
  }

  private static byte[] ParseBinary(string text)
  {
    string normalized = text
      .Replace(",", " ", StringComparison.Ordinal)
      .Replace("-", " ", StringComparison.Ordinal)
      .Replace("\r", " ", StringComparison.Ordinal)
      .Replace("\n", " ", StringComparison.Ordinal)
      .Replace("\t", " ", StringComparison.Ordinal);
    string[] tokens = normalized.Split(
      ' ',
      StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
    if (tokens.Length == 0) return [];
    var bytes = new byte[tokens.Length];
    for (int index = 0; index < tokens.Length; index++)
    {
      if (tokens[index].Length != 2 ||
          !byte.TryParse(
            tokens[index],
            NumberStyles.HexNumber,
            CultureInfo.InvariantCulture,
            out bytes[index]))
        throw new InvalidOperationException(
          $"“{tokens[index]}”不是有效的十六进制字节。");
    }
    return bytes;
  }

  private static uint ParseUInt32(string text)
  {
    string value = text.Trim();
    return value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
      ? uint.Parse(value[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
      : uint.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);
  }

  private static ulong ParseUInt64(string text)
  {
    string value = text.Trim();
    return value.StartsWith("0x", StringComparison.OrdinalIgnoreCase)
      ? ulong.Parse(value[2..], NumberStyles.HexNumber, CultureInfo.InvariantCulture)
      : ulong.Parse(value, NumberStyles.Integer, CultureInfo.InvariantCulture);
  }
}

public sealed record RegistryValueEditResult(
  string Name,
  string Kind,
  string? StringValue = null,
  List<string>? MultiStringValue = null,
  byte[]? BinaryValue = null,
  long? IntegerValue = null);
