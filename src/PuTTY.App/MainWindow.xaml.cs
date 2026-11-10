using Microsoft.UI.Xaml;
using PuTTY.App.ViewModels;
using Windows.Graphics;

namespace PuTTY.App;

public sealed partial class MainWindow : Window
{
    public MainViewModel ViewModel { get; } = new();

    public MainWindow()
    {
        InitializeComponent();

        ExtendsContentIntoTitleBar = true;
        SetTitleBar(AppTitleBar);

        AppWindow.SetIcon("Assets/AppIcon.ico");
        AppWindow.Resize(new SizeInt32(1440, 940));   // size of the Figma sketch frame
    }
}
