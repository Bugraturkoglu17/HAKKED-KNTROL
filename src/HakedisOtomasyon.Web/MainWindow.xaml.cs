using HakedisOtomasyon.Application.Interfaces;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.WebView;
using Microsoft.AspNetCore.Components.WebView.Wpf;
using Microsoft.Extensions.DependencyInjection;
using System.IO;
using System.Windows;

namespace HakedisOtomasyon.Web;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        RegisterBlazorRoot();

        // Yerel dosyalara guvenlı erisim icin sanal host kaydet
        blazorWebView.BlazorWebViewInitialized += OnBlazorWebViewInitialized;
    }

    private void OnBlazorWebViewInitialized(object? sender, BlazorWebViewInitializedEventArgs e)
    {
        try
        {
            var appPath = ((WpfApp)System.Windows.Application.Current).Services
                .GetRequiredService<IAppPathService>();
            Directory.CreateDirectory(appPath.DataRootPath);
            e.WebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "localfiles.hakedis",
                appPath.DataRootPath,
                Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Allow);

            // Soğutma Hakediş Kontrol modülünün veri kökü ayrı (bkz. SogutmaHakedisKontrol.Infrastructure
            // .FileStorage.AppPathService) — AI servis formu PDF'leri burada saklanıyor, "Formu Göster"
            // önizlemesi için ayrı bir sanal host gerekiyor.
            var sogutmaAppPath = ((WpfApp)System.Windows.Application.Current).Services
                .GetRequiredService<SogutmaHakedisKontrol.Application.Interfaces.IAppPathService>();
            Directory.CreateDirectory(sogutmaAppPath.DataRootPath);
            e.WebView.CoreWebView2.SetVirtualHostNameToFolderMapping(
                "localfiles.sogutma",
                sogutmaAppPath.DataRootPath,
                Microsoft.Web.WebView2.Core.CoreWebView2HostResourceAccessKind.Allow);
        }
        catch (Exception ex)
        {
            // Kritik degil — gorsel on izleme calismaz ama uygulama calismaya devam eder
            System.Diagnostics.Debug.WriteLine($"VirtualHost kayit hatasi: {ex.Message}");
        }
    }

    private void RegisterBlazorRoot()
    {
        // Tüm assembly'deki IComponent implementasyonlarını tara
        var allComponents = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a => { try { return a.GetTypes(); } catch { return []; } })
            .Where(t => !t.IsAbstract && typeof(IComponent).IsAssignableFrom(t))
            .ToList();

        // Tanımlama: önce Routes, sonra App ara
        var rootType = allComponents.FirstOrDefault(t =>
                t.Name == "Routes" && t.Namespace?.StartsWith("HakedisOtomasyon") == true)
            ?? allComponents.FirstOrDefault(t =>
                t.Name == "App" && t.Namespace?.StartsWith("HakedisOtomasyon") == true);

        // Diagnostik log — AppData fallback kullan (DataRootPath henüz hazır olmayabilir)
        var logDir = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "HakedisOtomasyon", "Logs");
        Directory.CreateDirectory(logDir);
        var logFile = Path.Combine(logDir, "blazor-types.txt");
        File.WriteAllText(logFile,
            $"Tarih: {DateTime.Now}\n" +
            $"Toplam IComponent sayısı: {allComponents.Count}\n" +
            $"Seçilen tip: {rootType?.FullName ?? "BULUNAMADI"}\n\n" +
            string.Join("\n", allComponents.Select(t => t.FullName)));

        if (rootType is null)
            throw new InvalidOperationException($"Blazor root bileşeni bulunamadı. Log: {logFile}");

        blazorWebView.RootComponents.Add(new RootComponent
        {
            Selector = "#app",
            ComponentType = rootType
        });
    }
}
