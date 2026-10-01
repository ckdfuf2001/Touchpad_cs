using System;
using System.Threading;
using System.Windows;

namespace TouchPadRaw;

public partial class App : Application
{
    private static Mutex? _mutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        _mutex = new Mutex(true, "TouchPadRawSingleInstance", out bool owned);
        if (!owned)
        {
            Environment.Exit(0);
            return;
        }
        AppDomain.CurrentDomain.UnhandledException += (_, a) =>
        {
            try
            {
                Core.Log.Write("FATAL " + (a.ExceptionObject as Exception)?.ToString());
            }
            catch { }
        };
        base.OnStartup(e);
    }
}
