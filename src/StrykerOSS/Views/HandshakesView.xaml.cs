namespace StrykerOSS.Views;

public partial class HandshakesView : UserControl
{
    public HandshakesView(ViewModels.HandshakesVm vm)
    {
        InitializeComponent();
        DataContext = vm;
    }
}
