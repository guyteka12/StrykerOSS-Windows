namespace StrykerOSS.Views;

public partial class NucleiView : UserControl
{
    public NucleiView(ViewModels.NucleiVm vm)
    {
        InitializeComponent();
        DataContext = vm;
    }
}
