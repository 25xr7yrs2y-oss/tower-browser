using System.Windows;

namespace PrivacyBrowser.App;

public partial class App : Application
{
    private Mutex? _instanceMutex;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);
        try
        {
            _instanceMutex = new Mutex(initiallyOwned: true,
                "Local\\PrivacyBrowser.NativeController", out var isFirstInstance);
            if (!isFirstInstance)
            {
                MessageBox.Show("Tower Browser is already running. Use the existing window and isolated profile.",
                    "Tower Browser", MessageBoxButton.OK, MessageBoxImage.Information);
                _instanceMutex.Dispose();
                _instanceMutex = null;
                Shutdown(0);
                return;
            }

            var options = AppOptions.Parse(e.Args);
            var window = new MainWindow(options);
            MainWindow = window;
            window.Show();
        }
        catch (Exception ex)
        {
            string? logPath = TryWriteStartupError(ex);
            string message = ex.GetBaseException().Message;
            if (logPath is not null)
            {
                message += $"{Environment.NewLine}{Environment.NewLine}Diagnostic details: {logPath}";
            }
            MessageBox.Show(message, "Tower Browser", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
        }
    }

    protected override void OnExit(ExitEventArgs e)
    {
        if (_instanceMutex is not null)
        {
            try { _instanceMutex.ReleaseMutex(); }
            catch (ApplicationException) { }
            _instanceMutex.Dispose();
        }
        base.OnExit(e);
    }

    private static string? TryWriteStartupError(Exception exception)
    {
        try
        {
            string stateDirectory = Path.Combine(AppContext.BaseDirectory, "state");
            Directory.CreateDirectory(stateDirectory);
            string path = Path.Combine(stateDirectory, "startup-error.log");
            File.WriteAllText(path, $"{DateTimeOffset.Now:O}{Environment.NewLine}{exception}");
            return path;
        }
        catch
        {
            return null;
        }
    }
}
