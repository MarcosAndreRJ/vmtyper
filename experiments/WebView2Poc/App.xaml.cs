using System.Windows;

namespace WebView2Poc;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        string? autoReportPath = null;
        bool allowSso = false;

        for (int i = 0; i < e.Args.Length; i++)
        {
            if (e.Args[i] == "--compare-json" && i + 1 < e.Args.Length)
            {
                autoReportPath = e.Args[++i];
            }
            else if (e.Args[i] == "--allow-sso")
            {
                allowSso = true;
            }
        }

        var win = new MainWindow(autoReportPath, allowSso);
        MainWindow = win;
        win.Show();
    }
}

