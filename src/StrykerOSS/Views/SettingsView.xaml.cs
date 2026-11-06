namespace StrykerOSS.Views;

public partial class SettingsView : UserControl
{
    public SettingsView(ViewModels.SettingsVm vm)
    {
        InitializeComponent();
        DataContext = vm;
    }
}
