using System.Collections.Specialized;
using System.Windows;
using Microsoft.Win32;
using ProxyManager.ViewModels;

namespace ProxyManager;

public partial class MainWindow : Window
{
    public MainWindow()
    {
        InitializeComponent();
        // Keep the log scrolled to the newest line.
        ((INotifyCollectionChanged)LogList.Items).CollectionChanged += (_, e) =>
        {
            if (e.Action == NotifyCollectionChangedAction.Add && LogList.Items.Count > 0)
                LogList.ScrollIntoView(LogList.Items[^1]);
        };
    }

    private MainViewModel ViewModel => (MainViewModel)DataContext;

    private void AddExe_Click(object sender, RoutedEventArgs e)
    {
        var dialog = new OpenFileDialog
        {
            Title = "Выберите программу",
            Filter = "Программы (*.exe)|*.exe",
            Multiselect = true,
        };
        if (dialog.ShowDialog(this) == true)
            ViewModel.AddRules(dialog.FileNames);
    }

    private void AddFromRunning_Click(object sender, RoutedEventArgs e)
    {
        var picker = new ProcessPickerWindow { Owner = this };
        if (picker.ShowDialog() == true)
            ViewModel.AddRules(picker.SelectedPaths);
    }
}
