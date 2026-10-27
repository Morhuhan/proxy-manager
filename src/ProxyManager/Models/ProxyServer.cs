using System.Text.Json.Serialization;
using ProxyManager.ViewModels;

namespace ProxyManager.Models;

public enum ProxyType
{
    Socks5,
    Http,
}

public enum CheckState
{
    None,
    Checking,
    Ok,
    Failed,
}

public sealed class ProxyServer : ObservableObject
{
    private string _name = "";
    private ProxyType _type = ProxyType.Socks5;
    private string _host = "";
    private int _port = 1080;
    private string _username = "";
    private string _password = "";
    private CheckState _checkState;
    private string _checkResult = "";

    public string Id { get; set; } = Guid.NewGuid().ToString("N");

    public string Name
    {
        get => _name;
        set { if (Set(ref _name, value)) OnPropertyChanged(nameof(DisplayName)); }
    }

    public ProxyType Type
    {
        get => _type;
        set => Set(ref _type, value);
    }

    public string Host
    {
        get => _host;
        set { if (Set(ref _host, value.Trim())) OnPropertyChanged(nameof(DisplayName)); }
    }

    public int Port
    {
        get => _port;
        set { if (Set(ref _port, value)) OnPropertyChanged(nameof(DisplayName)); }
    }

    public string Username
    {
        get => _username;
        set => Set(ref _username, value);
    }

    /// <summary>Plain-text password; persisted only in DPAPI-protected form.</summary>
    [JsonIgnore]
    public string Password
    {
        get => _password;
        set => Set(ref _password, value);
    }

    [JsonPropertyName("password")]
    public string ProtectedPassword
    {
        get => Services.Secrets.Protect(Password);
        set => Password = Services.Secrets.Unprotect(value);
    }

    [JsonIgnore]
    public string DisplayName => string.IsNullOrWhiteSpace(Name) ? Address : Name;

    [JsonIgnore]
    public string Address => $"{Host}:{Port}";

    [JsonIgnore]
    public bool IsValid => Host.Length > 0 && Port is > 0 and <= 65535;

    [JsonIgnore]
    public bool HasCredentials => Username.Length > 0;

    [JsonIgnore]
    public CheckState CheckState
    {
        get => _checkState;
        set => Set(ref _checkState, value);
    }

    [JsonIgnore]
    public string CheckResult
    {
        get => _checkResult;
        set => Set(ref _checkResult, value);
    }

    public override string ToString() => DisplayName;
}
