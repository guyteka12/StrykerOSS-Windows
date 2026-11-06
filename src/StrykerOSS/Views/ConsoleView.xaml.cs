namespace StrykerOSS.Views;

public partial class ConsoleView : UserControl
{
    public ConsoleView(ViewModels.ConsoleVm vm)
    {
        InitializeComponent();
        DataContext = vm;
    }
}
