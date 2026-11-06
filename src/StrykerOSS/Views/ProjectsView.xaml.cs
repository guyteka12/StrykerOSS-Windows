namespace StrykerOSS.Views;

public partial class ProjectsView : UserControl
{
    public ProjectsView(ViewModels.ProjectsVm vm)
    {
        InitializeComponent();
        DataContext = vm;
    }
}
