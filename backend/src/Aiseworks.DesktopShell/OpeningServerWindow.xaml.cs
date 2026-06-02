using System.Windows;

namespace Aiseworks.DesktopShell;

public partial class OpeningServerWindow : Window
{
    public OpeningServerWindow(string server)
    {
        InitializeComponent();
        ServerTextBlock.Text = server;
    }
}
