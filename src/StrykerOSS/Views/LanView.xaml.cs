namespace StrykerOSS.Views;

public partial class LanView : UserControl
{
    public LanView(ViewModels.LanVm vm)
    {
        InitializeComponent();
        DataContext = vm;
    }
}
