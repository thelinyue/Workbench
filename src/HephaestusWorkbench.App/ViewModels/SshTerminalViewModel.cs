using System.Collections.ObjectModel;
using System.IO;
using System.Windows;
using System.Windows.Input;
using HephaestusWorkbench.App.Ssh;
using HephaestusWorkbench.Core.Models;
using HephaestusWorkbench.Core.Repositories;
using HephaestusWorkbench.Core.Services;

namespace HephaestusWorkbench.App.ViewModels;

/// <summary>
/// SSH 工作区的状态中枢：设备配置、非敏感最近连接投影和终端会话彼此分离。
/// 设备只持久化非敏感元数据；密码与私钥口令仅在当前连接动作中使用，只有用户明确勾选后才写入 Windows Credential Manager。
/// </summary>
public sealed class SshTerminalViewModel : ViewModelBase, IAsyncDisposable, IDisposable
{
    private readonly ISshDeviceRepository _devices;
    private readonly ISshConnectionHistoryRepository _history;
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
    private string _connectionTemplateJson = string.Empty;
    private string _connectionTemplateError = string.Empty;
    private TerminalTabViewModel? _selectedTab;
    private TerminalTabViewModel? _pendingReconnectTab;
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
        Action<SshDevice>? openMaintenance = null,
        Func<TimeSpan, CancellationToken, Task>? delay = null)
    {
        _devices = devices;
        _history = history;
        _credentials = credentials;
        _settings = settings;
        _cacheRoot = cacheRoot;
        _delay = delay ?? Task.Delay;
        _connections = new SshConnectionCoordinator(terminalService, hostKeys, history, confirmation, () => DateTime.Now);
        _port = settings.Ssh.DefaultPort;

        ConnectCommand = new DelegateCommand(() => _ = ConnectAsync(), () => !IsConnecting);
        ApplyConnectionTemplateCommand = new DelegateCommand(ApplyConnectionTemplate);
        ConnectDeviceCommand = new DelegateCommand(parameter =>
        {
            if (parameter is SshDevice device) _ = ConnectDeviceAsync(device, forceNewTab: false);
        });
        ConnectDeviceInNewTabCommand = new DelegateCommand(parameter =>
        {
            if (parameter is SshDevice device) _ = ConnectDeviceAsync(device, forceNewTab: true);
        });
        EditDeviceCommand = new DelegateCommand(parameter =>
        {
            if (parameter is SshDevice device) BeginEditDevice(device);
        });
        CopyDeviceAddressCommand = new DelegateCommand(parameter =>
        {
            if (parameter is SshDevice device) CopyDeviceAddress(device);
        });
        DeleteDeviceCommand = new DelegateCommand(parameter =>
        {
            if (parameter is SshDevice device) _ = DeleteDeviceAsync(device);
        });
        ConnectRecentCommand = new DelegateCommand(parameter =>
        {
            if (parameter is SshRecentConnection recent) _ = ConnectRecentAsync(recent);
        });
        CloseTabCommand = new DelegateCommand(parameter =>
        {
            if (parameter is TerminalTabViewModel tab) _ = CloseTabAsync(tab);
        });
        ReconnectTabCommand = new DelegateCommand(parameter =>
        {
            if (parameter is TerminalTabViewModel tab) _ = ReconnectTabAsync(tab);
        });
        OpenMaintenanceCommand = new DelegateCommand(
            parameter =>
            {
                if (parameter is SshDevice device) openMaintenance?.Invoke(device);
            },
            parameter => parameter is SshDevice && openMaintenance is not null);
    }

    /// <summary>由页面订阅，用于在需要重新录入敏感凭据或编辑设备时显示连接模态框。</summary>
    internal event EventHandler? ConnectionDialogRequested;

    public ObservableCollection<SshDevice> SavedDevices { get; } = [];
    public ObservableCollection<SshRecentConnection> RecentConnections { get; } = [];
    public ObservableCollection<TerminalTabViewModel> Tabs { get; } = [];
    public IReadOnlyList<SshAuthenticationOption> AuthenticationOptions { get; } =
    [
        new(SshAuthenticationMethod.Password, "密码认证"),
        new(SshAuthenticationMethod.PrivateKey, "私钥认证")
    ];

    public ICommand ConnectCommand { get; }
    public ICommand ApplyConnectionTemplateCommand { get; }
    public ICommand ConnectDeviceCommand { get; }
    public ICommand ConnectDeviceInNewTabCommand { get; }
    public ICommand EditDeviceCommand { get; }
    public ICommand CopyDeviceAddressCommand { get; }
    public ICommand DeleteDeviceCommand { get; }
    public ICommand ConnectRecentCommand { get; }
    public ICommand CloseTabCommand { get; }
    public ICommand ReconnectTabCommand { get; }
    /// <summary>从 SSH 设备上下文打开宿主维护窗口，不增加新的一级导航。</summary>
    public ICommand OpenMaintenanceCommand { get; }

    public string Name { get => _name; set => SetProperty(ref _name, value); }
    public string Host { get => _host; set => SetProperty(ref _host, value); }
    public int Port { get => _port; set => SetProperty(ref _port, value); }
    public string Username { get => _username; set => SetProperty(ref _username, value); }
    public string Password { get => _password; set => SetProperty(ref _password, value); }
    /// <summary>用户手动粘贴的非敏感连接模板 JSON，仅在当前连接窗口内保留。</summary>
    public string ConnectionTemplateJson { get => _connectionTemplateJson; set => SetProperty(ref _connectionTemplateJson, value); }
    public string ConnectionTemplateError { get => _connectionTemplateError; private set => SetProperty(ref _connectionTemplateError, value); }
    public bool HasConnectionTemplateError => !string.IsNullOrWhiteSpace(ConnectionTemplateError);
    public string PrivateKeyPath { get => _privateKeyPath; set => SetProperty(ref _privateKeyPath, value); }
    public string PrivateKeyPassphrase { get => _privateKeyPassphrase; set => SetProperty(ref _privateKeyPassphrase, value); }
    public bool SaveDevice { get => _saveDevice; set { if (SetProperty(ref _saveDevice, value) && !value) SaveCredential = false; } }
    public bool SaveCredential { get => _saveCredential; set => SetProperty(ref _saveCredential, value && SaveDevice); }
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
    public bool HasRecentConnections => RecentConnections.Count > 0;
    public TerminalTabViewModel? SelectedTab
    {
        get => _selectedTab;
        set
        {
            if (!SetProperty(ref _selectedTab, value) || value is null) return;
            value.MarkActivated();
        }
    }

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

    /// <summary>应用主机和端口模板，不覆盖名称、用户名、认证方式或任何凭据字段。</summary>
    internal void ApplyConnectionTemplate()
    {
        try
        {
            var template = SshConnectionTemplate.Parse(ConnectionTemplateJson);
            Host = template.Host;
            Port = template.Port;
            ConnectionTemplateError = string.Empty;
            OnPropertyChanged(nameof(HasConnectionTemplateError));
        }
        catch (InvalidDataException exception)
        {
            ConnectionTemplateError = exception.Message;
            OnPropertyChanged(nameof(HasConnectionTemplateError));
        }
    }

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
        await RefreshRecentConnectionsAsync(cancellationToken);
    }

    /// <summary>打开全新的连接表单，避免上一次临时密码、口令或模板残留到下一次连接。</summary>
    internal void BeginNewConnection()
    {
        _editingDeviceId = null;
        _pendingReconnectTab = null;
        Name = string.Empty;
        Host = string.Empty;
        Port = _settings.Ssh.DefaultPort;
        Username = string.Empty;
        AuthenticationMethod = SshAuthenticationMethod.Password;
        Password = string.Empty;
        PrivateKeyPath = string.Empty;
        PrivateKeyPassphrase = string.Empty;
        SaveDevice = false;
        ConnectionTemplateJson = string.Empty;
        ConnectionTemplateError = string.Empty;
        OnPropertyChanged(nameof(HasConnectionTemplateError));
        ConnectionDialogRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>把已保存设备的非敏感字段带入表单，密码和私钥口令始终清空。</summary>
    internal void BeginEditDevice(SshDevice device)
    {
        _pendingReconnectTab = null;
        PopulateDeviceForm(device);
        ConnectionDialogRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>关闭连接窗口时删除当前表单的敏感输入，并取消尚未提交的标签重连意图。</summary>
    internal void CancelConnectionDialog()
    {
        Password = string.Empty;
        PrivateKeyPassphrase = string.Empty;
        _pendingReconnectTab = null;
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
            var credentialTarget = SaveCredential && deviceId is not null
                ? BuildCredentialTarget(deviceId, AuthenticationMethod)
                : existingId is null ? null : FindExistingCredentialTarget(existingId);
            var suppliedCredential = BuildSuppliedCredential();
            var initialRequest = new SshConnectionRequest(
                deviceId,
                Host.Trim(),
                Port,
                Username.Trim(),
                AuthenticationMethod,
                AuthenticationMethod == SshAuthenticationMethod.PrivateKey ? PrivateKeyPath.Trim() : null,
                credentialTarget);

            unownedSession = await _connections.ConnectAsync(initialRequest, suppliedCredential, cancellationToken);
            var session = unownedSession;
            var reconnectRequest = initialRequest;

            if (SaveDevice && deviceId is not null)
            {
                var timestamp = DateTime.Now;
                var device = new SshDevice
                {
                    Id = deviceId,
                    Name = string.IsNullOrWhiteSpace(Name) ? $"{Username.Trim()}@{Host.Trim()}" : Name.Trim(),
                    Host = Host.Trim(),
                    Port = Port,
                    Username = Username.Trim(),
                    AuthenticationMethod = AuthenticationMethod,
                    PrivateKeyPath = AuthenticationMethod == SshAuthenticationMethod.PrivateKey ? PrivateKeyPath.Trim() : null,
                    CredentialTarget = FindExistingCredentialTarget(deviceId),
                    CreatedAt = SavedDevices.FirstOrDefault(item => item.Id == deviceId)?.CreatedAt ?? timestamp,
                    UpdatedAt = timestamp
                };
                // 先保存不含凭据引用的设备，再写 Credential Manager；这样凭据写入失败不会留下悬空 target。
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

            TerminalTabViewModel tab;
            Func<CancellationToken, Task<ITerminalSession>> reconnect = reconnectRequest.CredentialTarget is null
                ? _ => Task.FromException<ITerminalSession>(new InvalidOperationException("未保存 SSH 凭据，已停止自动重连，请重新输入凭据。"))
                : token => _connections.ConnectAsync(reconnectRequest, null, token);
            var reconnectingTab = _pendingReconnectTab;
            if (reconnectingTab is not null && Tabs.Contains(reconnectingTab) && reconnectingTab.IsDisconnected)
            {
                await reconnectingTab.RestartAsync(session, reconnect);
                tab = reconnectingTab;
                _pendingReconnectTab = null;
            }
            else
            {
                tab = new TerminalTabViewModel(
                    string.IsNullOrWhiteSpace(Name) ? $"{Username.Trim()}@{Host.Trim()}" : Name.Trim(),
                    reconnectRequest.DeviceId,
                    session,
                    reconnect,
                    _settings,
                    Path.Combine(_cacheRoot, Guid.NewGuid().ToString("N")),
                    _delay);
                tab.ConnectionStateChanged += OnTabConnectionStateChanged;
                Tabs.Add(tab);
            }
            unownedSession = null;
            SelectedTab = tab;
            OnPropertyChanged(nameof(HasTabs));
            StatusMessage = $"已连接到 {reconnectRequest.Host}:{reconnectRequest.Port}。";
            OnPropertyChanged(nameof(HasStatusMessage));
            // 密码与私钥口令只留在当前打开的连接表单内；页面关闭表单时会主动清空它们。
            await RefreshRecentConnectionsAsync(cancellationToken);
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
        tab.ConnectionStateChanged -= OnTabConnectionStateChanged;
        await tab.DisposeAsync();
        SelectedTab = Tabs.LastOrDefault();
        OnPropertyChanged(nameof(HasTabs));
    }

    /// <summary>连接已保存设备；默认复用最近激活的活动会话，显式新标签时才建立独立物理连接。</summary>
    internal async Task<bool> ConnectDeviceAsync(SshDevice device, bool forceNewTab, CancellationToken cancellationToken = default)
    {
        if (!forceNewTab)
        {
            var active = Tabs.Where(tab => tab.MatchesDevice(device.Id) && !tab.IsDisconnected)
                .OrderByDescending(tab => tab.LastActivatedAt)
                .FirstOrDefault();
            if (active is not null)
            {
                SelectedTab = active;
                return true;
            }
        }

        PopulateDeviceForm(device);
        if (!await HasReadableCredentialWhenRequiredAsync(device))
        {
            StatusMessage = "该设备没有可用的已保存凭据，请重新输入敏感凭据后再连接。";
            OnPropertyChanged(nameof(HasStatusMessage));
            ConnectionDialogRequested?.Invoke(this, EventArgs.Empty);
            return false;
        }
        var tabCount = Tabs.Count;
        await ConnectAsync(cancellationToken);
        return Tabs.Count > tabCount || (SelectedTab is not null && SelectedTab.MatchesDevice(device.Id));
    }

    /// <summary>
    /// 已断开标签的显式重连入口。仅当保存设备的 Credential Manager 凭据仍可读取时才自动重连；
    /// 其他情况只打开预填表单，保留当前标签和已有终端输出。
    /// </summary>
    internal async Task ReconnectTabAsync(TerminalTabViewModel tab, CancellationToken cancellationToken = default)
    {
        if (!tab.IsDisconnected) return;
        var device = tab.DeviceId is null ? null : SavedDevices.FirstOrDefault(item => item.Id == tab.DeviceId);
        if (device is null || !await HasReadableCredentialWhenRequiredAsync(device))
        {
            _pendingReconnectTab = tab;
            if (device is not null) PopulateDeviceForm(device);
            else PopulateReconnectForm(tab);
            StatusMessage = "请重新输入凭据后重新连接，当前终端输出会保留在此标签中。";
            OnPropertyChanged(nameof(HasStatusMessage));
            ConnectionDialogRequested?.Invoke(this, EventArgs.Empty);
            return;
        }

        try
        {
            var request = new SshConnectionRequest(
                device.Id, device.Host, device.Port, device.Username, device.AuthenticationMethod,
                device.PrivateKeyPath, device.CredentialTarget);
            var session = await _connections.ConnectAsync(request, null, cancellationToken);
            await tab.RestartAsync(session, token => _connections.ConnectAsync(request, null, token));
            SelectedTab = tab;
            StatusMessage = $"已重新连接到 {device.Host}:{device.Port}。";
            OnPropertyChanged(nameof(HasStatusMessage));
            await RefreshRecentConnectionsAsync(cancellationToken);
        }
        catch (Exception exception)
        {
            StatusMessage = $"重新连接失败：{SafeUiError(exception)}";
            OnPropertyChanged(nameof(HasStatusMessage));
        }
    }

    private void PopulateReconnectForm(TerminalTabViewModel tab)
    {
        _editingDeviceId = null;
        Name = tab.Title;
        Host = tab.ConnectionIdentity.Host;
        Port = tab.ConnectionIdentity.Port;
        Username = tab.ConnectionIdentity.Username;
        AuthenticationMethod = SshAuthenticationMethod.Password;
        Password = string.Empty;
        PrivateKeyPath = string.Empty;
        PrivateKeyPassphrase = string.Empty;
        SaveDevice = false;
    }

    private async Task ConnectRecentAsync(SshRecentConnection recent)
    {
        if (recent.DeviceId is not null && SavedDevices.FirstOrDefault(item => item.Id == recent.DeviceId) is { } saved)
        {
            await ConnectDeviceAsync(saved, forceNewTab: false);
            return;
        }

        // 未保存目标只允许预填非敏感信息，并强制用户重新输入密码或私钥口令。
        _editingDeviceId = null;
        Name = string.Empty;
        Host = recent.Host;
        Port = recent.Port;
        Username = recent.Username;
        AuthenticationMethod = SshAuthenticationMethod.Password;
        Password = string.Empty;
        PrivateKeyPath = string.Empty;
        PrivateKeyPassphrase = string.Empty;
        SaveDevice = false;
        ConnectionTemplateJson = string.Empty;
        ConnectionTemplateError = string.Empty;
        OnPropertyChanged(nameof(HasConnectionTemplateError));
        StatusMessage = "请重新输入此未保存目标的敏感凭据。";
        OnPropertyChanged(nameof(HasStatusMessage));
        ConnectionDialogRequested?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// 删除设备配置及其 Credential Manager 凭据，但绝不操作现有标签持有的 SSH 会话。
    /// 调用方可等待此方法，以便在菜单交互中给出准确的中文结果反馈。
    /// </summary>
    internal async Task DeleteSavedDeviceAsync(SshDevice device, CancellationToken cancellationToken = default)
    {
        try
        {
            // 先删除凭据，再删除设备记录；若系统拒绝删除凭据，设备仍保留，避免留下难以发现的敏感数据。
            if (!string.IsNullOrWhiteSpace(device.CredentialTarget))
                await _credentials.DeleteAsync(device.CredentialTarget, cancellationToken);
            await _devices.DeleteAsync(device.Id, cancellationToken);
            SavedDevices.Remove(device);
            OnPropertyChanged(nameof(HasSavedDevices));
            StatusMessage = $"已删除设备“{device.Name}”及其已保存凭据。活动终端会话保持不变。";
            OnPropertyChanged(nameof(HasStatusMessage));
        }
        catch (Exception exception)
        {
            StatusMessage = $"删除设备失败：{SafeUiError(exception)}";
            OnPropertyChanged(nameof(HasStatusMessage));
        }
    }

    private Task DeleteDeviceAsync(SshDevice device) => DeleteSavedDeviceAsync(device);

    private void CopyDeviceAddress(SshDevice device)
    {
        try
        {
            System.Windows.Clipboard.SetText($"{device.Username}@{device.Host}:{device.Port}");
            StatusMessage = "已复制设备地址。";
        }
        catch (Exception exception)
        {
            StatusMessage = $"复制设备地址失败：{SafeUiError(exception)}";
        }
        OnPropertyChanged(nameof(HasStatusMessage));
    }

    private void PopulateDeviceForm(SshDevice device)
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
        ConnectionTemplateJson = string.Empty;
        ConnectionTemplateError = string.Empty;
        OnPropertyChanged(nameof(HasConnectionTemplateError));
    }

    private async Task<bool> HasReadableCredentialWhenRequiredAsync(SshDevice device)
    {
        if (device.AuthenticationMethod != SshAuthenticationMethod.Password)
            return true;
        if (string.IsNullOrWhiteSpace(device.CredentialTarget))
            return false;
        return await _credentials.ReadAsync(device.CredentialTarget) is not null;
    }

    private async Task RefreshRecentConnectionsAsync(CancellationToken cancellationToken = default)
    {
        RecentConnections.Clear();
        foreach (var recent in await _history.ListRecentSuccessfulAsync(12, cancellationToken)) RecentConnections.Add(recent);
        OnPropertyChanged(nameof(HasRecentConnections));
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

    private void OnTabConnectionStateChanged(object? sender, TerminalConnectionState state)
    {
        if (sender is not TerminalTabViewModel tab || state != TerminalConnectionState.Disconnected) return;
        RunOnUi(() =>
        {
            StatusMessage = $"SSH 会话“{tab.Title}”已断开。请点击重新连接并按需重新输入凭据。";
            OnPropertyChanged(nameof(HasStatusMessage));
        });
    }

    private static void RunOnUi(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action(); else _ = dispatcher.InvokeAsync(action);
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

/// <summary>
/// 一个标签只拥有一个 SSH 会话和一个浏览器表面。会话身份、活动时间和外置状态只存在内存中，
/// 既不写入连接历史，也不会携带密码、私钥口令或 Credential Manager target。
/// </summary>
public sealed class TerminalTabViewModel : ViewModelBase, IAsyncDisposable
{
    private ITerminalSession _session;
    private readonly string? _deviceId;
    private Func<CancellationToken, Task<ITerminalSession>> _reconnect;
    private readonly TerminalReconnectOptions _reconnectOptions;
    private readonly Func<TimeSpan, CancellationToken, Task> _delay;
    private TerminalSessionController? _controller;
    private bool _isDetached;
    private bool _isDisconnected;
    private DateTime _lastActivatedAt = DateTime.UtcNow;
    internal ITerminalSurface? Surface { get; private set; }
    private int _disposed;

    /// <summary>仅向拥有此标签的工作区报告连接状态；不会传递终端输入、输出或凭据。</summary>
    internal event EventHandler<TerminalConnectionState>? ConnectionStateChanged;

    internal TerminalTabViewModel(
        string title,
        string? deviceId,
        ITerminalSession session,
        Func<CancellationToken, Task<ITerminalSession>> reconnect,
        AppSettingsConfig settings,
        string cacheDirectory,
        Func<TimeSpan, CancellationToken, Task> delay)
    {
        Title = title;
        _deviceId = deviceId;
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
    /// <summary>创建标签时记录的设备标识；不依赖底层驱动是否在会话身份中回填该字段。</summary>
    public string? DeviceId => _deviceId;
    public SshConnectionIdentity ConnectionIdentity => _session.ConnectionIdentity;
    public string ConnectionDescription => $"{ConnectionIdentity.Username}@{ConnectionIdentity.Host}:{ConnectionIdentity.Port}";
    public bool IsDetached { get => _isDetached; private set => SetProperty(ref _isDetached, value); }
    public bool IsDisconnected { get => _isDisconnected; private set => SetProperty(ref _isDisconnected, value); }
    public DateTime LastActivatedAt { get => _lastActivatedAt; private set => SetProperty(ref _lastActivatedAt, value); }
    public string SessionState => IsDetached ? "已在独立窗口" : IsDisconnected ? "已断开" : "已连接";

    internal bool MatchesDevice(string deviceId) => string.Equals(DeviceId, deviceId, StringComparison.Ordinal);
    internal void MarkActivated() => LastActivatedAt = DateTime.UtcNow;
    internal void SetDetached(bool detached)
    {
        IsDetached = detached;
        OnPropertyChanged(nameof(SessionState));
    }
    internal void MarkDisconnected()
    {
        IsDisconnected = true;
        OnPropertyChanged(nameof(SessionState));
    }

    internal async Task AttachSurfaceAsync(ITerminalSurface surface)
    {
        if (_controller is not null)
        {
            if (!ReferenceEquals(surface, Surface)) await surface.DisposeAsync();
            return;
        }
        Surface = surface;
        _controller = new TerminalSessionController(_session, _reconnect, surface, _reconnectOptions, _delay);
        _controller.ConnectionStateChanged += OnControllerConnectionStateChanged;
        await _controller.StartAsync();
    }

    /// <summary>
    /// 用新会话替换已断开的底层连接，但继续使用原有浏览器终端表面，
    /// 因而保留屏幕上已有的终端输出与标签位置。
    /// </summary>
    internal async Task RestartAsync(
        ITerminalSession session,
        Func<CancellationToken, Task<ITerminalSession>> reconnect)
    {
        if (_controller is not null)
        {
            _controller.ConnectionStateChanged -= OnControllerConnectionStateChanged;
            await _controller.StopSessionAsync();
            _controller = null;
        }
        else
        {
            await _session.DisposeAsync();
        }

        _session = session;
        _reconnect = reconnect;
        IsDisconnected = false;
        OnPropertyChanged(nameof(ConnectionIdentity));
        OnPropertyChanged(nameof(ConnectionDescription));
        OnPropertyChanged(nameof(SessionState));
        if (Surface is not null)
            await AttachSurfaceAsync(Surface);
    }

    private void OnControllerConnectionStateChanged(TerminalConnectionState state)
    {
        RunOnUi(() =>
        {
            IsDisconnected = state == TerminalConnectionState.Disconnected;
            OnPropertyChanged(nameof(SessionState));
            ConnectionStateChanged?.Invoke(this, state);
        });
    }

    private static void RunOnUi(Action action)
    {
        var dispatcher = System.Windows.Application.Current?.Dispatcher;
        if (dispatcher is null || dispatcher.CheckAccess()) action(); else _ = dispatcher.InvokeAsync(action);
    }

    public async ValueTask DisposeAsync()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0) return;
        if (_controller is not null) await _controller.DisposeAsync();
        else await _session.DisposeAsync();
        Surface = null;
    }
}
