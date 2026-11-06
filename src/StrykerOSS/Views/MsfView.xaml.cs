namespace StrykerOSS.Views;

public partial class MsfView : UserControl
{
    public MsfView(ViewModels.MsfVm vm)
    {
        InitializeComponent();
        DataContext = vm;
    }
}
