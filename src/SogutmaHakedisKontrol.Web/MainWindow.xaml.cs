using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.WebView.Wpf;
using System.Windows;

namespace SogutmaHakedisKontrol.Web;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        blazorWebView.RootComponents.Add(new RootComponent
        {
            Selector = "#app",
            ComponentType = typeof(App)
        });
    }
}
