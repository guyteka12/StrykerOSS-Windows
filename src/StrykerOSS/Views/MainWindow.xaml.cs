using Microsoft.Extensions.DependencyInjection;

namespace StrykerOSS.Views;

public partial class MainWindow : Window
{
    public MainWindow(IServiceProvider sp)
    {
        InitializeComponent();
        DataContext = sp.GetRequiredService<ViewModels.MainWindowVm>();
    }
}
