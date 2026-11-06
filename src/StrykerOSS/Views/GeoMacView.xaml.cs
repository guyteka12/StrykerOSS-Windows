namespace StrykerOSS.Views;

public partial class GeoMacView : UserControl
{
    public GeoMacView(ViewModels.GeoMacVm vm)
    {
        InitializeComponent();
        DataContext = vm;
    }
}
