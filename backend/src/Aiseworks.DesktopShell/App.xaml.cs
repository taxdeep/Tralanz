using System.Windows;
using Aiseworks.ApiClient;
using Aiseworks.DesktopShell.Services;
using Citus.Ui.Shared.DesktopHybrid;
using Microsoft.AspNetCore.Components.WebView.Wpf;
using Microsoft.Extensions.DependencyInjection;

namespace Aiseworks.DesktopShell;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        var services = new ServiceCollection();
        services.AddWpfBlazorWebView();
#if DEBUG
        services.AddBlazorWebViewDeveloperTools();
#endif
        services.AddAiseworksApiClient(options =>
        {
            options.AccountingApiBaseUrl = new Uri("http://localhost:15088", UriKind.Absolute);
            options.BusinessBaseUrl = new Uri("http://localhost:18080", UriKind.Absolute);
            options.SysAdminApiBaseUrl = new Uri("http://localhost:15089", UriKind.Absolute);
        });
        services.AddSingleton<IDesktopFilePicker, WpfDesktopFilePicker>();
        services.AddSingleton<IDesktopPrintService, WpfDesktopPrintService>();
        services.AddSingleton<IDesktopNotificationService, WpfDesktopNotificationService>();
        services.AddSingleton<IDesktopLocalCache, WpfDesktopLocalCache>();
        services.AddSingleton<IDesktopUpdateService, WpfDesktopUpdateService>();
        services.AddSingleton<IDesktopHostBridge, WpfDesktopHostBridge>();

        Resources.Add("services", services.BuildServiceProvider());
        base.OnStartup(e);
    }
}
