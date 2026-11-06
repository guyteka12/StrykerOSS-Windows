namespace StrykerOSS.Views;

public partial class WifiView : UserControl
{
    public WifiView(ViewModels.WifiVm vm)
    {
        InitializeComponent();
        DataContext = vm;
    }
}
