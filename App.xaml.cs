using System.Windows;

namespace ImageSorter;

public partial class App : Application
{
    protected override void OnStartup(StartupEventArgs e)
    {
        // A sorting session shouldn't die on one bad file: report it and keep going.
        DispatcherUnhandledException += (_, args) =>
        {
            MessageBox.Show(args.Exception.Message, "Image Sorter", MessageBoxButton.OK, MessageBoxImage.Warning);
            args.Handled = true;
        };
        base.OnStartup(e);
    }
}
