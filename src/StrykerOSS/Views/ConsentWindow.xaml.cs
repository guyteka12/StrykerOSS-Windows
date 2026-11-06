namespace StrykerOSS.Views;

public partial class ConsentWindow : Window
{
    public bool Accepted { get; private set; }

    public ConsentWindow()
    {
        InitializeComponent();
        ChkAgree.Checked += (_, _) => BtnAccept.IsEnabled = true;
        ChkAgree.Unchecked += (_, _) => BtnAccept.IsEnabled = false;
    }

    private void OnAccept(object sender, RoutedEventArgs e)
    {
        Accepted = true;
        Close();
    }

    private void OnExit(object sender, RoutedEventArgs e)
    {
        Accepted = false;
        Close();
    }
}
