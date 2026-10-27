using System.ComponentModel;
using System.Diagnostics;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Data;
using System.Windows.Input;
using System.Windows.Media;
using ProxyManager.Services;

namespace ProxyManager;

public sealed record RunningProgram(string Path, string Description, ImageSource? Icon);

public partial class ProcessPickerWindow : Window
{
    public ProcessPickerWindow()
    {
        InitializeComponent();
        Loaded += async (_, _) =>
        {
            ProcessList.ItemsSource = await Task.Run(FindPrograms);
            LoadingText.Visibility = Visibility.Collapsed;
            SearchBox.Focus();
        };
    }

    public IReadOnlyList<string> SelectedPaths { get; private set; } = [];

    private static List<RunningProgram> FindPrograms()
    {
        var windowsDir = Environment.GetFolderPath(Environment.SpecialFolder.Windows);
        var ownDir = AppContext.BaseDirectory;
        var paths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        foreach (var process in Process.GetProcesses())
        {
            using (process)
            {
                try
                {
                    if (process.MainModule?.FileName is { } path)
                        paths.Add(path);
                }
                catch (Exception e) when (e is Win32Exception or InvalidOperationException)
                {
                    // Protected or already exited.
                }
            }
        }

        return paths
            .Where(p => !p.StartsWith(windowsDir, StringComparison.OrdinalIgnoreCase)
                        && !p.StartsWith(ownDir, StringComparison.OrdinalIgnoreCase))
            .Select(p => new RunningProgram(p, ExeInfo.GetDescription(p), ExeInfo.GetIcon(p)))
            .OrderBy(p => p.Description, StringComparer.CurrentCultureIgnoreCase)
            .ToList();
    }

    private void SearchBox_TextChanged(object sender, TextChangedEventArgs e)
    {
        var query = SearchBox.Text.Trim();
        var view = CollectionViewSource.GetDefaultView(ProcessList.ItemsSource);
        if (view is null) return;
        view.Filter = query.Length == 0
            ? null
            : item => item is RunningProgram p
                      && (p.Description.Contains(query, StringComparison.CurrentCultureIgnoreCase)
                          || p.Path.Contains(query, StringComparison.OrdinalIgnoreCase));
    }

    private void ProcessList_SelectionChanged(object sender, SelectionChangedEventArgs e) =>
        AddButton.IsEnabled = ProcessList.SelectedItems.Count > 0;

    private void ProcessList_MouseDoubleClick(object sender, MouseButtonEventArgs e)
    {
        if (ProcessList.SelectedItems.Count > 0)
            Accept();
    }

    private void Add_Click(object sender, RoutedEventArgs e) => Accept();

    private void Accept()
    {
        SelectedPaths = ProcessList.SelectedItems.Cast<RunningProgram>().Select(p => p.Path).ToList();
        DialogResult = true;
    }
}
