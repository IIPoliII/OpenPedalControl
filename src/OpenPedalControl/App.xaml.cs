using System;
using System.Linq;
using System.Threading;
using System.Windows;
using System.Windows.Threading;

namespace OpenPedalControl;

public partial class App : Application
{
    /// <summary>Also checked by the installer (AppMutex) so it never replaces the exe while it runs.</summary>
    public const string InstanceMutexName = "OpenPedalControl.Instance";
    private const string ShowEventName = "OpenPedalControl.Show";

    private Mutex? _instanceMutex;
    private EventWaitHandle? _showEvent;
    private MainWindow? _window;

    protected override void OnStartup(StartupEventArgs e)
    {
        DispatcherUnhandledException += OnUnhandledException;
        base.OnStartup(e);

        // "--startup on|off": used by the installer to set "Start with Windows" for the signed-in user.
        int startupIndex = Array.FindIndex(e.Args, a => a.Equals("--startup", StringComparison.OrdinalIgnoreCase));
        if (startupIndex >= 0)
        {
            bool enable = e.Args.Length > startupIndex + 1 && e.Args[startupIndex + 1].Equals("on", StringComparison.OrdinalIgnoreCase);
            try { AutoStart.SetEnabled(enable); } catch (Exception) { }
            Shutdown(0);
            return;
        }

        _instanceMutex = new Mutex(true, InstanceMutexName, out bool firstInstance);
        _showEvent = new EventWaitHandle(false, EventResetMode.AutoReset, ShowEventName);
        if (!firstInstance)
        {
            // Already running (maybe in the tray): bring that one up instead.
            _showEvent.Set();
            Shutdown(0);
            return;
        }

        AutoStart.RepairPath();
        bool startHidden = e.Args.Any(a => a.Equals(AutoStart.TrayArgument, StringComparison.OrdinalIgnoreCase));

        _window = new MainWindow();
        MainWindow = _window;
        _window.Start(startHidden);

        var listener = new Thread(ListenForShowRequests) { IsBackground = true, Name = "Show requests" };
        listener.Start();
    }

    private void ListenForShowRequests()
    {
        while (_showEvent != null && _showEvent.WaitOne())
        {
            Dispatcher.BeginInvoke(new Action(() => _window?.ShowFromTray()));
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        _instanceMutex?.Dispose();
        base.OnExit(e);
    }

    private void OnUnhandledException(object sender, DispatcherUnhandledExceptionEventArgs e)
    {
        MessageBox.Show(e.Exception.Message, "OpenPedalControl", MessageBoxButton.OK, MessageBoxImage.Error);
        e.Handled = true;
        if (_window == null || !_window.IsInitializedForUse) Shutdown(1);
    }
}
