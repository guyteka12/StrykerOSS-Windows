namespace StrykerOSS.Views;

public partial class SessionsView : UserControl
{
    public SessionsView(ViewModels.SessionsVm vm)
    {
        InitializeComponent();
        DataContext = vm;
    }
}
