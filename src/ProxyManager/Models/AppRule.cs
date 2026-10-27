using System.Text.Json.Serialization;
using System.Windows.Media;
using ProxyManager.ViewModels;

namespace ProxyManager.Models;

/// <summary>Routes all TCP traffic of one program through one proxy.</summary>
public sealed class AppRule : ObservableObject
{
    private string _exePath = "";
    private string? _proxyId;
    private bool _enabled = true;
    private bool _includeChildren = true;

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string ExePath
    {
        get => _exePath;
        set
        {
            if (!Set(ref _exePath, value)) return;
            OnPropertyChanged(nameof(ProcessName));
            OnPropertyChanged(nameof(DisplayName));
            OnPropertyChanged(nameof(Icon));
        }
    }

    public string? ProxyId
    {
        get => _proxyId;
        set => Set(ref _proxyId, value);
    }

    public bool Enabled
    {
        get => _enabled;
        set => Set(ref _enabled, value);
    }

    /// <summary>Also route processes started by this program (and their descendants).</summary>
    public bool IncludeChildren
    {
        get => _includeChildren;
        set => Set(ref _includeChildren, value);
    }

    /// <summary>Executable file name, which is what sing-box matches against.</summary>
    [JsonIgnore]
    public string ProcessName => Path.GetFileName(ExePath);

    [JsonIgnore]
    public string DisplayName => Services.ExeInfo.GetDescription(ExePath);

    [JsonIgnore]
    public ImageSource? Icon => Services.ExeInfo.GetIcon(ExePath);
}
