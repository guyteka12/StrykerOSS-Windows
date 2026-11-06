namespace StrykerOSS.Views;

public partial class HidView : UserControl
{
    public HidView(ViewModels.HidVm vm)
    {
        InitializeComponent();
        DataContext = vm;
    }
}
