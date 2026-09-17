using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Runtime.CompilerServices;
using System.Windows;
using System.Windows.Input;
using System.Windows.Threading;
using Microsoft.Win32;
using Mercury;

namespace Mercury.Catcher;

public sealed class CatcherViewModel : INotifyPropertyChanged, IAsyncDisposable
{
    private readonly AppPaths _paths;
    private readonly Dispatcher _dispatcher;
    private CatcherHttpsServer? _server;
    private CatcherUnwrapped? _listening;
    private string _passphrase = "";
    private string _destinationFolder = "";
    private string _statusText = "Import a Catcher file (or pick a template), enter the passphrase, and Listen.";
    private string _listenLabel = "Not listening";
    private string _lastTransfer = "No transfers yet.";
    private string _hint = "";
    private bool _isListening;
    private bool _readyToReceive;
    private CatcherEnvelope? _selectedTemplate;

    public CatcherViewModel() : this(new AppPaths(), Application.Current.Dispatcher)
    {
    }

    public CatcherViewModel(AppPaths paths, Dispatcher dispatcher)
    {
        _paths = paths;
        _dispatcher = dispatcher;
        var settings = CatcherStore.LoadSettings(paths);
        DestinationFolder = string.IsNullOrWhiteSpace(settings.DestinationFolder)
            ? CatcherStore.DefaultReceiveFolder(paths)
            : settings.DestinationFolder;
        Directory.CreateDirectory(DestinationFolder);

        ImportCommand = new RelayCommand(Import, () => !IsListening);
        BrowseDestCommand = new RelayCommand(BrowseDest, () => !IsListening);
        ListenCommand = new RelayCommand(Listen, () => !IsListening && SelectedTemplate is not null);
        StopCommand = new RelayCommand(() => _ = StopAsync(), () => IsListening);

        ReloadTemplates();
        if (!string.IsNullOrWhiteSpace(settings.LastTemplateId))
        {
            SelectedTemplate = Templates.FirstOrDefault(t => t.Id == settings.LastTemplateId);
        }

        StatusText = Templates.Count == 0
            ? "Import a .mercury-catch file exported from Mercury, then Listen."
            : "Select a template, enter the passphrase, and Listen.";
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    public ObservableCollection<CatcherEnvelope> Templates { get; } = [];

    public ICommand ImportCommand { get; }
    public ICommand BrowseDestCommand { get; }
    public ICommand ListenCommand { get; }
    public ICommand StopCommand { get; }

    public string DataPathLabel =>
        _paths.IsPortable
            ? $"Data (portable): {_paths.DataRoot}"
            : $"Data: {_paths.DataRoot}";

    public string DestinationFolder
    {
        get => _destinationFolder;
        set
        {
            if (SetField(ref _destinationFolder, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanMarkReady)));
                PersistSettings();
            }
        }
    }

    public CatcherEnvelope? SelectedTemplate
    {
        get => _selectedTemplate;
        set
        {
            if (SetField(ref _selectedTemplate, value))
            {
                Hint = value?.PortForwardHint() ?? "";
                PersistSettings();
                RaiseCommands();
            }
        }
    }

    public string StatusText { get => _statusText; set => SetField(ref _statusText, value); }
    public string ListenLabel { get => _listenLabel; set => SetField(ref _listenLabel, value); }
    public string LastTransfer { get => _lastTransfer; set => SetField(ref _lastTransfer, value); }
    public string Hint { get => _hint; set => SetField(ref _hint, value); }

    public bool IsListening
    {
        get => _isListening;
        private set
        {
            if (SetField(ref _isListening, value))
            {
                PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(CanMarkReady)));
                RaiseCommands();
            }
        }
    }

    public bool ReadyToReceive
    {
        get => _readyToReceive;
        set
        {
            if (SetField(ref _readyToReceive, value) && _server is not null)
            {
                _server.ReadyToReceive = value;
                ListenLabel = value
                    ? $"Ready to receive on {_server.EndpointDisplay} (passphrase required)."
                    : $"Listening HTTPS {_server.EndpointDisplay} — not ready to receive.";
            }
        }
    }

    public bool CanMarkReady => IsListening && !string.IsNullOrWhiteSpace(DestinationFolder);

    public void SetPassphrase(string value) => _passphrase = value ?? "";

    public async ValueTask DisposeAsync() => await StopAsync().ConfigureAwait(true);

    private void ReloadTemplates()
    {
        var selectedId = SelectedTemplate?.Id;
        Templates.Clear();
        foreach (var template in CatcherStore.LoadTemplates(_paths))
        {
            Templates.Add(template);
        }

        if (selectedId is not null)
        {
            SelectedTemplate = Templates.FirstOrDefault(t => t.Id == selectedId);
        }
    }

    private void Import()
    {
        var dlg = new OpenFileDialog
        {
            Title = "Import Catcher file",
            Filter = "Catcher pack (*.mercury-catch)|*.mercury-catch|JSON (*.json)|*.json|All files (*.*)|*.*",
            CheckFileExists = true
        };
        if (dlg.ShowDialog() != true)
        {
            return;
        }

        try
        {
            var envelope = CatcherPack.LoadFile(dlg.FileName);
            CatcherStore.Upsert(_paths, envelope);
            ReloadTemplates();
            SelectedTemplate = Templates.FirstOrDefault(t => t.Id == envelope.Id);
            StatusText = $"Imported “{envelope.Name}”. Enter the passphrase and Listen. Fingerprint {envelope.FingerprintSha256}";
        }
        catch (Exception ex)
        {
            StatusText = Sanitize(ex.Message);
        }
    }

    private void BrowseDest()
    {
        var dlg = new OpenFolderDialog { Title = "Catcher destination folder" };
        if (dlg.ShowDialog() == true)
        {
            DestinationFolder = dlg.FolderName;
        }
    }

    private void Listen()
    {
        if (SelectedTemplate is null)
        {
            StatusText = "Import or select a Catcher template first.";
            return;
        }

        try
        {
            CatcherCrypto.ValidatePassphrase(_passphrase);
            Directory.CreateDirectory(DestinationFolder);
            if (!Directory.Exists(DestinationFolder))
            {
                throw new IOException($"Receive folder is missing: {DestinationFolder}");
            }

            var probe = Path.Combine(DestinationFolder, $".mercury-write-{Guid.NewGuid():N}");
            File.WriteAllText(probe, "ok");
            File.Delete(probe);

            _listening = CatcherPack.Unwrap(SelectedTemplate, _passphrase);
            var bind = CatcherCrypto.ParseBindAddress(SelectedTemplate.BindAddress);
            _server = new CatcherHttpsServer(
                bind,
                SelectedTemplate.InternalListenPort,
                _listening.Certificate,
                _listening.AuthToken,
                DestinationFolder,
                message => _dispatcher.BeginInvoke(() => StatusText = message),
                info => _dispatcher.BeginInvoke(() =>
                {
                    LastTransfer = info.Ok
                        ? $"{info.Utc.ToLocalTime():HH:mm:ss}  {info.FileName}  {ByteFormatter.ToString(info.Bytes)}  {info.Message}"
                        : $"{info.Utc.ToLocalTime():HH:mm:ss}  FAILED  {info.FileName}  {info.Message}";
                }));
            _server.Start();
            IsListening = true;
            ReadyToReceive = false;
            var warn = CatcherCrypto.IsLoopbackOnly(SelectedTemplate.BindAddress)
                ? " Bind is loopback — a router port-forward cannot reach this Catcher."
                : "";
            ListenLabel = $"Listening HTTPS {_server.EndpointDisplay}  (TLS). Passphrase required on every connect.{warn}";
            Hint = SelectedTemplate.PortForwardHint();
            StatusText = $"Listening. Tick Ready to receive when the dest folder is correct. {Hint}";
            PersistSettings();
        }
        catch (Exception ex)
        {
            MercuryErrorLog.Write(_paths, "catcher", Sanitize(ex.Message), ex, catcher: true);
            _ = StopAsync();
            StatusText = Sanitize(ex.Message);
        }
    }

    private async Task StopAsync()
    {
        IsListening = false;
        ReadyToReceive = false;
        ListenLabel = "Not listening";
        if (_server is not null)
        {
            await _server.DisposeAsync().ConfigureAwait(true);
            _server = null;
        }

        _listening?.Dispose();
        _listening = null;
        if (string.IsNullOrWhiteSpace(StatusText) || StatusText.StartsWith("Listening", StringComparison.Ordinal))
        {
            StatusText = "Stopped listening.";
        }
    }

    private void PersistSettings()
    {
        CatcherStore.SaveSettings(_paths, new CatcherLocalSettings
        {
            DestinationFolder = DestinationFolder,
            LastTemplateId = SelectedTemplate?.Id
        });
    }

    private void RaiseCommands()
    {
        (ImportCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (BrowseDestCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (ListenCommand as RelayCommand)?.RaiseCanExecuteChanged();
        (StopCommand as RelayCommand)?.RaiseCanExecuteChanged();
    }

    private static string Sanitize(string message)
    {
        if (message.Contains("passphrase", StringComparison.OrdinalIgnoreCase) &&
            !message.Contains("Wrong passphrase", StringComparison.OrdinalIgnoreCase) &&
            !message.Contains("required", StringComparison.OrdinalIgnoreCase) &&
            !message.Contains("at least", StringComparison.OrdinalIgnoreCase))
        {
            return "Authentication failed.";
        }

        return message;
    }

    private bool SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value))
        {
            return false;
        }

        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
        return true;
    }
}
