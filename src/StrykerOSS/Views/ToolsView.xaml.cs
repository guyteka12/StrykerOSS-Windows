namespace StrykerOSS.Views;

public partial class ToolsView : UserControl
{
    public ToolsView(ViewModels.ToolsVm vm)
    {
        InitializeComponent();
        DataContext = vm;
    }
}
