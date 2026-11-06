namespace StrykerOSS.Views;

public partial class ReportsView : UserControl
{
    public ReportsView(ViewModels.ReportsVm vm)
    {
        InitializeComponent();
        DataContext = vm;
    }
}
