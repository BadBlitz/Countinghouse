using System.Windows;

namespace Countinghouse.Desktop;

public partial class MainWindow : Window
{
    public MainWindow(IServiceProvider services)
    {
        Services = services;
        InitializeComponent();
    }

    /// <summary>
    /// Bound by <c>MainWindow.xaml</c> to hand the BlazorWebView the app's service provider.
    /// Hybrid resolves components from this one provider for the life of the process.
    /// </summary>
    public IServiceProvider Services { get; }
}
