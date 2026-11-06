namespace StrykerOSS.Views;

public partial class UsbView : UserControl
{
    public UsbView(ViewModels.UsbVm vm)
    {
        InitializeComponent();
        DataContext = vm;
    }
}
