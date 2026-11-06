namespace StrykerOSS.Views;

public partial class DashboardView : UserControl
{
    public DashboardView(ViewModels.DashboardVm vm)
    {
        InitializeComponent();
        DataContext = vm;
    }
}
