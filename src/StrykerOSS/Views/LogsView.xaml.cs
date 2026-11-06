namespace StrykerOSS.Views;

public partial class LogsView : UserControl
{
    public LogsView(ViewModels.LogsVm vm)
    {
        InitializeComponent();
        DataContext = vm;
    }
}
