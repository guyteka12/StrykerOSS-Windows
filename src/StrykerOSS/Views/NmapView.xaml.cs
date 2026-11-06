namespace StrykerOSS.Views;

public partial class NmapView : UserControl
{
    public NmapView(ViewModels.NmapVm vm)
    {
        InitializeComponent();
        DataContext = vm;
    }
}
