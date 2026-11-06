namespace StrykerOSS.Views;

public partial class MacChangerView : UserControl
{
    public MacChangerView(ViewModels.MacChangerVm vm)
    {
        InitializeComponent();
        DataContext = vm;
    }
}
