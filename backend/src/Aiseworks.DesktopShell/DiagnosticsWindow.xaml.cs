using System.IO;
using System.Windows;
using Microsoft.Win32;

namespace Aiseworks.DesktopShell;

public partial class DiagnosticsWindow : Window
{
    public DiagnosticsWindow(string heading, string diagnostics)
    {
        InitializeComponent();
        HeadingTextBlock.Text = heading;
        DiagnosticsTextBox.Text = diagnostics;
    }

    private void CopyButton_Click(object sender, RoutedEventArgs e)
    {
        Clipboard.SetText(DiagnosticsTextBox.Text);
        StatusTextBlock.Text = "Copied to clipboard";
    }

    private void SaveButton_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new SaveFileDialog
        {
            Title = "Save Aiseworks diagnostics",
            FileName = $"aiseworks-diagnostics-{DateTimeOffset.Now:yyyyMMdd-HHmmss}.txt",
            Filter = "Text files (*.txt)|*.txt|All files (*.*)|*.*"
        };

        if (dialog.ShowDialog(this) != true)
        {
            return;
        }

        File.WriteAllText(dialog.FileName, DiagnosticsTextBox.Text);
        StatusTextBlock.Text = $"Saved to {dialog.FileName}";
    }

    private void CloseButton_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }
}
