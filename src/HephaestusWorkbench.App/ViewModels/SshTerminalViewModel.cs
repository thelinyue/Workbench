using System.IO;
using System.Collections.ObjectModel;
using System.Windows.Input;
using HephaestusWorkbench.App.Ssh;
using HephaestusWorkbench.Core.Models;
using HephaestusWorkbench.Core.Repositories;
using HephaestusWorkbench.Core.Services;

namespace HephaestusWorkbench.App.ViewModels;

/// <summary>
/// 固定 SSH 页面模型，负责设备表单、凭据保存选择和独立终端标签。
/// 保存设备只持久化非敏感连接元数据；只有用户单独勾选“保存凭据”时才调用 Credential Manager。
/// </summary>
public sealed class SshTerminalViewModel : ViewModelBase, IAsyncDisposable, IDisposable
{
    private readonly ISshDeviceRepository _devices;
    private readonly ICredentialStore _credentials;
    private readonly SshConnectionCoordinator _connections;
    private readonly AppSettingsConfig _settings;
    private readonly string _cacheRoot;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private string? _editingDeviceId;
    private string _name = string.Empty;
    private string _host = string.Empty;
    private int _port;
    private string _username = string.Empty;
    private SshAuthenticationMethod _authenticationMethod;
    private string _password = string.Empty;
    private string _privateKeyPath = string.Empty;
    private string _privateKeyPassphrase = string.Empty;
    private bool _saveDevice;
    private bool _saveCredential;
    private bool _isConnecting;
    private string _statusMessage = string.Empty;
    private TerminalTabViewModel? _selectedTab;
    private int _disposed;

    internal SshTerminalViewModel(
        ISshTerminalService terminalService,
        ISshDeviceRepository devices,
        ISshHostKeyRepository hostKeys,
        ISshConnectionHistoryRepository history,
        ICredentialStore credentials,
        IHostKeyConfirmationService confirmation,
        AppSettingsConfig settings,
        string cacheRoot,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _devices = devices;
        _credentials = credentials;
        _settings = settings;
        _cacheRoot = cacheRoot;
        _delay = delay ?? Task.Delay;
        _connections = new SshConnectionCoordinator(terminalService, hostKeys, history, confirmation, () => DateTime.Now);
        _port = settings.Ssh.DefaultPort;
        ConnectCommand = new DelegateCommand(() => _ = ConnectAsync(), () => !IsConnecting);
        ConnectDeviceCommand = new DelegateCommand(parameter =>
        {
            if (parameter is SshDevice device) _ = ConnectDeviceAsync(device);
        });
        CloseTabCommand = new DelegateCommand(parameter =>
        {
            if (parameter is TerminalTabViewModel tab) _ = CloseTabAsync(tab);
        });
    }

    public ObservableCollection<SshDevice> SavedDevices { get; } = [];
    public ObservableCollection<TerminalTabViewModel> Tabs { get; } = [];
    public IReadOnlyList<SshAuthenticationOption> AuthenticationOptions { get; } =
    [
        new(SshAuthenticationMethod.Password, "密码认证"),
        new(SshAuthenticationMethod.PrivateKey, "私钥认证")
    ];
    public ICommand ConnectCommand { get; }
    public ICommand ConnectDeviceCommand { get; }
    public ICommand CloseTabCommand { get; }

    public string Name { get => _name; set => SetProperty(ref _name, value); }
    public string Host { get => _host; set => SetProperty(ref _host, value); }
    public int Port { get => _port; set => SetProperty(ref _port, value); }
    public string Username { get => _username; set => SetProperty(ref _username, value); }
    public string Password { get => _password; set => SetProperty(ref _password, value); }
    public string PrivateKeyPath { get => _privateKeyPath; set => SetProperty(ref _privateKeyPath, value); }
    public string PrivateKeyPassphrase { get => _privateKeyPassphrase; set => SetProperty(ref _privateKeyPassphrase, value); }
    public bool SaveDevice { get => _saveDevice; set { if (SetProperty(ref _saveDevice, value) && !value) SaveCredential = false; } }
    public bool SaveCredential { get => _saveCredential; set => SetProperty(ref _saveCredential, value); }
    public bool IsConnecting
    {
        get => _isConnecting;
        private set
        {
            if (!SetProperty(ref _isConnecting, value)) return;
            (ConnectCommand as DelegateCommand)?.RaiseCanExecuteChanged();
        }
    }
    public string StatusMessage { get => _statusMessage; private set => SetProperty(ref _statusMessage, value); }
    public bool HasStatusMessage => !string.IsNullOrWhiteSpace(StatusMessage);
    public bool HasTabs => Tabs.Count > 0;
    public bool HasSavedDevices => SavedDevices.Count > 0;
    public TerminalTabViewModel? SelectedTab { get => _selectedTab; set => SetProperty(ref _selectedTab, value); }

    public SshAuthenticationMethod AuthenticationMethod
    {
        get => _authenticationMethod;
        set
        {
            if (!SetProperty(ref _authenticationMethod, value)) return;
            OnPropertyChanged(nameof(IsPasswordAuthentication));
            OnPropertyChanged(nameof(IsPrivateKeyAuthentication));
        }
    }
    public bool IsPasswordAuthentication => AuthenticationMethod == SshAuthenticationMethod.Password;
    public bool IsPrivateKeyAuthentication => AuthenticationMethod == SshAuthenticationMethod.PrivateKey;

    internal void ApplyPreferences(SshTerminalPreferences preferences)
    {
        _settings.Ssh.DefaultPort = preferences.DefaultPort;
        _settings.Terminal.FontFamily = preferences.FontFamily;
        _settings.Terminal.FontSize = preferences.FontSize;
        _settings.ReconnectBehavior = preferences.ReconnectBehavior;
        Port = preferences.DefaultPort;
    }

    internal async Task InitializeAsync(CancellationToken cancellationToken = default)
    {
        SavedDevices.Clear();
        foreach (var device in await _devices.ListAsync(cancellationToken)) SavedDevices.Add(device);
        OnPropertyChanged(nameof(HasSavedDevices));
    }

    internal async Task ConnectAsync(CancellationToken cancellationToken = default)
    {
        if (IsConnecting) return;
        IsConnecting = true;
        ITerminalSession? unownedSession = null;
        try
        {
            ValidateForm();
            var existingId = _editingDeviceId;
            var deviceId = SaveDevice ? existingId ?? Guid.NewGuid().ToString("N") : null;
            var credentialTarget = SaveDevice && SaveCredential ? BuildCredentialTarget(deviceId!, AuthenticationMethod) : null;
            var initialRequest = new SshConnectionRequest(
                existingId,
                Host.Trim(),
                Port,
                Username.Trim(),
                AuthenticationMethod,
                AuthenticationMethod == SshAuthenticationMethod.PrivateKey ? PrivateKeyPath.Trim() : null,
                existingId is not null ? credentialTarget ?? FindExistingCredentialTarget(existingId) : null);
            var suppliedCredential = BuildSuppliedCredential();
            var session = await _connections.ConnectAsync(initialRequest, suppliedCredential, cancellationToken);
            unownedSession = session;

            var reconnectRequest = initialRequest;
            if (SaveDevice)
            {
                var timestamp = DateTime.Now;
                var existing = existingId is null ? null : SavedDevices.FirstOrDefault(item => item.Id == existingId);
                var device = new SshDevice
                {
                    Id = deviceId!,
                    Name = string.IsNullOrWhiteSpace(Name) ? $"{Username.Trim()}@{Host.Trim()}" : Name.Trim(),
                    Host = Host.Trim(),
                    Port = Port,
                    Username = Username.Trim(),
                    AuthenticationMethod = AuthenticationMethod,
                    PrivateKeyPath = AuthenticationMethod == SshAuthenticationMethod.PrivateKey ? PrivateKeyPath.Trim() : null,
                    CredentialTarget = null,
                    CreatedAt = existing?.CreatedAt ?? timestamp,
                    UpdatedAt = timestamp
                };
                // 先保存不含凭据引用的设备，再写 Credential Manager，避免凭据写入失败时留下悬空 target。
                await _devices.UpsertAsync(device, cancellationToken);
                if (SaveCredential && suppliedCredential is not null)
                {
                    await _credentials.WriteAsync(credentialTarget!, device.Username, suppliedCredential, cancellationToken);
                    device = device with { CredentialTarget = credentialTarget, UpdatedAt = DateTime.Now };
                    try { await _devices.UpsertAsync(device, cancellationToken); }
                    catch
                    {
                        await _credentials.DeleteAsync(credentialTarget!, cancellationToken);
                        throw;
                    }
                }
                ReplaceSavedDevice(device);
                reconnectRequest = initialRequest with { DeviceId = device.Id, CredentialTarget = device.CredentialTarget };
                _editingDeviceId = device.Id;
            }

            var tab = new TerminalTabViewModel(
                string.IsNullOrWhiteSpace(Name) ? $"{Username.Trim()}@{Host.Trim()}" : Name.Trim(),
                session,
                token => _connections.ConnectAsync(reconnectRequest, suppliedCredential, token),
                _settings,
                Path.Combine(_cacheRoot, Guid.NewGuid().ToString("N")),
                _delay);
            Tabs.Add(tab);
            unownedSession = null;
            SelectedTab = tab;
            OnPropertyChanged(nameof(HasTabs));
            StatusMessage = $"已连接到 {reconnectRequest.Host}:{reconnectRequest.Port}。";
            OnPropertyChanged(nameof(HasStatusMessage));
        }
        catch (OperationCanceledException)
        {
            StatusMessage = "SSH 连接已取消。";
            OnPropertyChanged(nameof(HasStatusMessage));
        }
        catch (SshHostKeyValidationException exception) when (exception.Reason == SshHostKeyFailureReason.Changed)
        {
            StatusMessage = $"SSH Host Key 已变化，已拒绝连接。当前指纹：{exception.Observation.Fingerprint}";
            OnPropertyChanged(nameof(HasStatusMessage));
        }
        catch (Exception exception)
        {
            StatusMessage = $"SSH 连接失败：{SafeUiError(exception)}";
            OnPropertyChanged(nameof(HasStatusMessage));
        }
        finally
        {
            if (unownedSession is not null) await unownedSession.DisposeAsync();
            IsConnecting = false;
        }
    }

    internal async Task CloseTabAsync(TerminalTabViewModel tab)
    {
        if (!Tabs.Remove(tab)) return;
        await tab.DisposeAsync();
        SelectedTab = Tabs.LastOrDefault();
        OnPropertyChanged(nameof(HasTabs));
    }

    private async Task ConnectDeviceAsync(SshDevice device)
    {
        _editingDeviceId = device.Id;
        Name = device.Name;
        Host = device.Host;
        Port = device.Port;
        Username = device.Username;
        AuthenticationMethod = device.AuthenticationMethod;
        PrivateKeyPath = device.PrivateKeyPath ?? string.Empty;
        Password = string.Empty;
        PrivateKeyPassphrase = string.Empty;
        SaveDevice = true;
        SaveCredential = device.CredentialTarget is not null;
        await ConnectAsync();
    }

    private string? FindExistingCredentialTarget(string id) =>
        SavedDevices.FirstOrDefault(item => item.Id == id)?.CredentialTarget;

    private SshCredentialSecret? BuildSuppliedCredential()
    {
        var value = AuthenticationMethod == SshAuthenticationMethod.Password ? Password : PrivateKeyPassphrase;
        return string.IsNullOrEmpty(value) ? null : new SshCredentialSecret(value);
    }

    private void ValidateForm()
    {
        if (string.IsNullOrWhiteSpace(Host)) throw new InvalidOperationException("请输入 SSH 主机。");
        if (Port is < 1 or > 65535) throw new InvalidOperationException("SSH 端口必须在 1 到 65535 之间。");
        if (string.IsNullOrWhiteSpace(Username)) throw new InvalidOperationException("请输入 SSH 用户名。");
        if (AuthenticationMethod == SshAuthenticationMethod.Password && string.IsNullOrEmpty(Password) &&
            (_editingDeviceId is null || string.IsNullOrWhiteSpace(FindExistingCredentialTarget(_editingDeviceId))))
            throw new InvalidOperationException("请输入 SSH 密码，或使用已保存凭据的设备。");
        if (AuthenticationMethod == SshAuthenticationMethod.PrivateKey && string.IsNullOrWhiteSpace(PrivateKeyPath))
            throw new InvalidOperationException("请选择 SSH 私钥文件。");
        if (SaveCredential && !SaveDevice)
            throw new InvalidOperationException("保存凭据前必须同时勾选保存设备。");
    }

    private void ReplaceSavedDevice(SshDevice device)
    {
        var existing = SavedDevices.FirstOrDefault(item => item.Id == device.Id);
        if (existing is not null) SavedDevices.Remove(existing);
        SavedDevices.Insert(0, device);
        OnPropertyChanged(nameof(HasSavedDevices));
    }

    private static string BuildCredentialTarget(string deviceId, SshAuthenticationMethod method) =>
        $"HephaestusWorkbench/ssh/{deviceId}/{(method == SshAuthenticationMethod.Password ? "password" : "private-key-passphrase")}";

    private static string SafeUiError(Exception exception)
    {
        var name = exception.GetType().Name;
        if (name.Contains("Authentication", StringComparison.OrdinalIgnoreCase)) return "认证失败，请检查用户名和凭据。";
        if (exception is TimeoutException) return "连接超时，请检查主机、端口和网络。";
        return exception.Message;
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        foreach (var tab in Tabs.ToArray()) await tab.DisposeAsync();
        Tabs.Clear();
        SelectedTab = null;
        OnPropertyChanged(nameof(HasTabs));
    }

    public void Dispose() => DisposeAsync().AsTask().GetAwaiter().GetResult();
}

public sealed record SshAuthenticationOption(SshAuthenticationMethod Value, string DisplayName);

/// <summary>一个标签只拥有一个 SSH 会话和一个浏览器表面；关闭标签不会影响其他标签。</summary>
public sealed class TerminalTabViewModel : ViewModelBase, IAsyncDisposable
{
    private readonly ITerminalSession _session;
    private readonly Func<CancellationToken, Task<ITerminalSession>> _reconnect;
    private readonly TerminalReconnectOptions _reconnectOptions;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private TerminalSessionController? _controller;
    internal ITerminalSurface? Surface { get; private set; }
    private int _disposed;

    internal TerminalTabViewModel(
        string title,
        ITerminalSession session,
        Func<CancellationToken, Task<ITerminalSession>> reconnect,
        AppSettingsConfig settings,
        string cacheDirectory,
        Func<TimeSpan, CancellationToken, Task> delay)
    {
        Title = title;
        _session = session;
        _reconnect = reconnect;
        _reconnectOptions = TerminalReconnectOptions.From(settings);
        _delay = delay;
        CacheDirectory = cacheDirectory;
        FontFamily = settings.Terminal.FontFamily;
        FontSize = settings.Terminal.FontSize;
    }

    public string Title { get; }
    public string CacheDirectory { get; }
    public string FontFamily { get; }
    public double FontSize { get; }
    public string ConnectionDescription => $"{_session.ConnectionIdentity.Username}@{_session.ConnectionIdentity.Host}:{_session.ConnectionIdentity.Port}";

    internal async Task AttachSurfaceAsync(ITerminalSurface surface)
    {
        if (_controller is not null)
        {
            if (!ReferenceEquals(surface, Surface)) await surface.DisposeAsync();
            return;
        }
        Surface = surface;
        _controller = new TerminalSessionController(_session, _reconnect, surface, _reconnectOptions, _delay);
        await _controller.StartAsync();
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_controller is not null) await _controller.DisposeAsync();
        else await _session.DisposeAsync();
        Surface = null;
    }
}
