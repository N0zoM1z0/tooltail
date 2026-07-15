using System.Windows;
using Tooltail.Application.Abstractions;

namespace Tooltail.Desktop;

public partial class MainWindow : Window
{
    public MainWindow(IClock clock)
    {
        InitializeComponent();
        StatusText.Text = $"M0 host ready at {clock.UtcNow:O}. No user resource is open.";
    }
}
