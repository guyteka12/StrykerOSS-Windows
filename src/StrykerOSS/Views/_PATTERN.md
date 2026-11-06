# Canonical View + ViewModel pattern — COPY THIS STRUCTURE EXACTLY

## ViewModels/ExampleVm.cs
```csharp
using CommunityToolkit.Mvvm.ComponentModel;
using CommunityToolkit.Mvvm.Input;

namespace StrykerOSS.ViewModels;

public partial class ExampleVm : ObservableObject
{
    private readonly ILogger _log;

    [ObservableProperty] private bool _isBusy;
    [ObservableProperty] private string _status = "Ready";
    [ObservableProperty] private string _target = "";
    public System.Collections.ObjectModel.ObservableCollection<string> Lines { get; } = new();

    public ExampleVm(ILogger log)
    {
        _log = log;
    }

    [RelayCommand]
    private async Task DoWorkAsync(CancellationToken ct)
    {
        if (IsBusy || string.IsNullOrWhiteSpace(Target)) return;
        IsBusy = true;
        try
        {
            Status = $"Working on {Target}...";
            await Task.Run(async () =>
            {
                for (int i = 0; i < 3; i++)
                {
                    ct.ThrowIfCancellationRequested();
                    var line = $"{Target} step {i}";
                    App.Current.Dispatcher.Invoke(() => Lines.Add(line));
                    await Task.Delay(200, ct);
                }
            }, ct);
            _log.Info("example", $"done: {Target}");
            Status = "Done";
        }
        catch (OperationCanceledException) { Status = "Cancelled"; }
        catch (Exception ex) { _log.Error("example", "failed", ex); Status = "Error: " + ex.Message; }
        finally { IsBusy = false; }
    }
}
```

## Views/ExampleView.xaml
```xml
<UserControl x:Class="StrykerOSS.Views.ExampleView"
             xmlns="http://schemas.microsoft.com/winfx/2006/xaml/presentation"
             xmlns:x="http://schemas.microsoft.com/winfx/2006/xaml">
    <Grid Background="{StaticResource BgBrush}" TextElement.Foreground="{StaticResource TextBrush}" Margin="18">
        <Grid.RowDefinitions>
            <RowDefinition Height="Auto"/>
            <RowDefinition Height="*"/>
        </Grid.RowDefinitions>

        <!-- page header -->
        <StackPanel Grid.Row="0" Margin="0,0,0,14">
            <TextBlock Text="Example Module" Style="{StaticResource H1}"/>
            <TextBlock Text="What this module really does on Windows." Style="{StaticResource Caption}"/>
        </StackPanel>

        <Grid Grid.Row="1">
            <Grid.ColumnDefinitions>
                <ColumnDefinition Width="*"/>
                <ColumnDefinition Width="320"/>
            </Grid.ColumnDefinitions>

            <!-- main column -->
            <Border Grid.Column="0" Style="{StaticResource Card}">
                <DockPanel>
                    <StackPanel DockPanel.Dock="Top" Orientation="Horizontal">
                        <TextBox Style="{StaticResource InputBox}" Width="260"
                                 Text="{Binding Target, UpdateSourceTrigger=PropertyChanged}"/>
                        <Button Content="Run" Style="{StaticResource BtnPrimary}" Margin="10,0,0,0"
                                Command="{Binding DoWorkCommand}"/>
                        <TextBlock Text="{Binding Status}" Style="{StaticResource Caption}" Margin="12,0,0,0"
                                   VerticalAlignment="Center"/>
                    </StackPanel>
                    <ListBox x:Name="OutList" Background="Transparent" BorderThickness="0" Margin="0,12,0,0"
                             ItemsSource="{Binding Lines}" FontFamily="Consolas" FontSize="12"
                             Foreground="{StaticResource TextBrush}"
                             VirtualizingPanel.ScrollUnit="Pixel"/>
                </DockPanel>
            </Border>

            <!-- side column -->
            <StackPanel Grid.Column="1">
                <Border Style="{StaticResource Card}">
                    <StackPanel>
                        <TextBlock Text="INFO" Style="{StaticResource Section}"/>
                        <TextBlock Style="{StaticResource Caption}" Text="Educational use only."/>
                    </StackPanel>
                </Border>
            </StackPanel>
        </Grid>
    </Grid>
</UserControl>
```

## Views/ExampleView.xaml.cs
```csharp
namespace StrykerOSS.Views;

public partial class ExampleView : UserControl
{
    public ExampleView(ViewModels.ExampleVm vm)
    {
        InitializeComponent();
        DataContext = vm;
    }
}
```

## Auto-scroll tip (for output lists)
In the VM, after adding a line you can raise: `App.Current.Dispatcher.Invoke(() => { Lines.Add(line); });`
Views should keep it simple — no code-behind logic.

## Items with templates
```xml
<ItemsControl ItemsSource="{Binding Devices}">
    <ItemsControl.ItemTemplate>
        <DataTemplate>
            <Border Background="{StaticResource Card2Brush}" CornerRadius="10" Padding="12,8" Margin="0,0,0,8">
                <StackPanel Orientation="Horizontal">
                    <TextBlock Text="{Binding Ip}" Style="{StaticResource Body}" FontWeight="SemiBold"/>
                    <Border Style="{StaticResource BadgeInfo}" Margin="10,0,0,0">
                        <TextBlock Text="ALIVE" FontSize="10" FontWeight="Bold" Foreground="#0B0B12"/>
                    </Border>
                </StackPanel>
            </Border>
        </DataTemplate>
    </ItemsControl.ItemTemplate>
</ItemsControl>
```

## ComboBox (dark)
```xml
<ComboBox Style="{StaticResource ComboDark}" Width="200" ItemsSource="{Binding Profiles}" SelectedItem="{Binding SelectedProfile}"/>
```
(For strings only; for complex items set DisplayMemberPath="Name".)
