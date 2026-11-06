namespace StrykerOSS.Views;

public partial class WhisperView : UserControl
{
    public WhisperView(ViewModels.WhisperVm vm)
    {
        InitializeComponent();
        DataContext = vm;
    }
}
