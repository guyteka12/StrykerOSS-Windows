namespace StrykerOSS.Views;

public partial class ArsenalView : UserControl
{
    public ArsenalView(ViewModels.ArsenalVm vm)
    {
        InitializeComponent();
        DataContext = vm;
    }
}
