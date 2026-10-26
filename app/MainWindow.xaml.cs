using System.ComponentModel;
using System.Diagnostics;
using System.IO;
using System.Runtime.InteropServices;
using System.Text.RegularExpressions;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Interop;
using System.Windows.Media;
using System.Windows.Threading;
using ZeusCastEnhanced.Models;
using ZeusCastEnhanced.Services;
using Application = System.Windows.Application;
using Button = System.Windows.Controls.Button;
using Color = System.Windows.Media.Color;

namespace ZeusCastEnhanced;

public partial class MainWindow : Window
{
    private static readonly string OriginalAppPath = Path.Combine(
        Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
        "GAMDIAS",
        "ZEUS CAST",
        "ZEUS CAST.exe");
    private const int DwmUseImmersiveDarkMode = 20;
    private const int DwmWindowCornerPreference = 33;
    private readonly SettingsService _settingsService = new();
    private readonly HardwareMonitorService _hardwareMonitor = new();
    private readonly ChioneHidService _cooler = new();
    private readonly StartupOverrideService _startupOverride = new();
    private readonly DispatcherTimer _refreshTimer = new();
    private readonly DateTime _cycleStarted = DateTime.UtcNow;
    private readonly bool _startHidden = Environment.GetCommandLineArgs().Any(argument => argument.Equals("--background", StringComparison.OrdinalIgnoreCase));
    private AppSettings _settings;
    private System.Windows.Forms.NotifyIcon? _trayIcon;
    private System.Windows.Forms.ContextMenuStrip? _trayMenu;
    private System.Drawing.Icon? _trayIconImage;
    private bool _initializing = true;
    private bool _refreshing;
    private bool _allowExit;
    private bool _trayHintShown;
    private double? _temperatureCelsius;
    private double? _fanRpm;
    private double? _minimumTemperature;
    private double? _maximumTemperature;
    private double? _minimumFanRpm;
    private double? _maximumFanRpm;
    private DateTime _lastReconnectAttempt = DateTime.MinValue;

    public MainWindow()
    {
        _settings = _settingsService.Load();
        InitializeComponent();
        Application.Current.SessionEnding += (_, _) => _allowExit = true;
    }

    private async void Window_Loaded(object sender, RoutedEventArgs e)
    {
        InitializeTrayIcon();
        ApplySettingsToUi();
        _startupOverride.StopOriginalProcesses();

        if (_settings.ReplaceOriginalZeusCast || _settings.StartWithWindows)
        {
            _settings.ReplaceOriginalZeusCast = true;
            _settings.StartWithWindows = true;
            StartWithWindowsCheck.IsChecked = true;
            SetStatus("Replacing the original Zeus Cast startup…");
            SetStatus(await _startupOverride.ApplyOverrideAsync());
        }

        await Task.Delay(350);
        await LoadSensorsAsync();
        _initializing = false;

        _refreshTimer.Interval = TimeSpan.FromMilliseconds(_settings.RefreshMilliseconds);
        _refreshTimer.Tick += RefreshTimer_Tick;
        _refreshTimer.Start();

        await TryConnectAsync();
        await RefreshAsync();
        SaveSettings();

        if (_startHidden)
        {
            HideToTray(showHint: false);
        }
    }

    private void ApplySettingsToUi()
    {
        SelectComboByTag(DisplayModeCombo, _settings.DisplayMode);
        CelsiusRadio.IsChecked = !_settings.UseFahrenheit;
        FahrenheitRadio.IsChecked = _settings.UseFahrenheit;
        CustomValueText.Text = _settings.CustomValue.ToString();
        AlarmCheck.IsChecked = _settings.AlarmEnabled;
        ThresholdText.Text = _settings.AlarmThresholdCelsius.ToString();
        SelectComboByTag(RefreshRateCombo, _settings.RefreshMilliseconds.ToString());
        StartWithWindowsCheck.IsChecked = _settings.ReplaceOriginalZeusCast || _settings.StartWithWindows;
        UpdateModeState();
    }

    private async Task LoadSensorsAsync(bool restart = false)
    {
        SetStatus("Reading CPU and motherboard sensors…");
        try
        {
            var snapshot = await Task.Run(() =>
            {
                if (restart)
                {
                    _hardwareMonitor.Dispose();
                }

                _hardwareMonitor.Open();
                return _hardwareMonitor.Refresh();
            });

            BindSensorLists(snapshot.Temperatures, snapshot.Fans);
            UpdateSensorBackendText(snapshot.Temperatures, snapshot.Fans);

            var liveTemperatures = snapshot.Temperatures.Count(sensor => sensor.Value.HasValue);
            var liveFans = snapshot.Fans.Count(sensor => sensor.Value.HasValue);
            if (liveTemperatures == 0)
            {
                SetStatus(_hardwareMonitor.LastError ?? "No live temperature reading was found.");
            }
            else
            {
                SetStatus($"Loaded {liveTemperatures} live temperature and {liveFans} live fan readings.");
            }
        }
        catch (Exception exception)
        {
            SensorBackendText.Text = "Sensor service unavailable";
            SetStatus($"Sensor service unavailable: {exception.Message}");
        }
    }

    private void BindSensorLists(IReadOnlyList<SensorReading> temperatures, IReadOnlyList<SensorReading> fans)
    {
        var temperatureId = (TemperatureSensorCombo.SelectedItem as SensorReading)?.Id ?? _settings.TemperatureSensorId;
        var fanId = (FanSensorCombo.SelectedItem as SensorReading)?.Id ?? _settings.FanSensorId;
        var wasInitializing = _initializing;
        _initializing = true;

        TemperatureSensorCombo.ItemsSource = temperatures;
        FanSensorCombo.ItemsSource = fans;
        TemperatureSensorCombo.SelectedItem = FindSensor(temperatures, temperatureId);
        FanSensorCombo.SelectedItem = FindSensor(fans, fanId);

        _settings.TemperatureSensorId = (TemperatureSensorCombo.SelectedItem as SensorReading)?.Id;
        _settings.FanSensorId = (FanSensorCombo.SelectedItem as SensorReading)?.Id;
        _initializing = wasInitializing;
    }

    private void EnsureSensorLists(IReadOnlyList<SensorReading> temperatures, IReadOnlyList<SensorReading> fans)
    {
        var currentTemperatureIds = (TemperatureSensorCombo.ItemsSource as IEnumerable<SensorReading>)?.Select(sensor => sensor.Id) ?? [];
        var currentFanIds = (FanSensorCombo.ItemsSource as IEnumerable<SensorReading>)?.Select(sensor => sensor.Id) ?? [];
        if (!currentTemperatureIds.SequenceEqual(temperatures.Select(sensor => sensor.Id))
            || !currentFanIds.SequenceEqual(fans.Select(sensor => sensor.Id)))
        {
            BindSensorLists(temperatures, fans);
            SaveSettings();
        }
    }

    private static SensorReading? FindSensor(IReadOnlyList<SensorReading> sensors, string? id)
    {
        return sensors.FirstOrDefault(sensor => sensor.Id == id && sensor.Value.HasValue)
            ?? sensors.FirstOrDefault(sensor => sensor.Value.HasValue)
            ?? sensors.FirstOrDefault(sensor => sensor.Id == id)
            ?? sensors.FirstOrDefault();
    }

    private async void RefreshTimer_Tick(object? sender, EventArgs e)
    {
        await RefreshAsync();
    }

    private async Task RefreshAsync()
    {
        if (_refreshing)
        {
            return;
        }

        _refreshing = true;
        try
        {
            var snapshot = await Task.Run(_hardwareMonitor.Refresh);
            EnsureSensorLists(snapshot.Temperatures, snapshot.Fans);
            UpdateSensorBackendText(snapshot.Temperatures, snapshot.Fans);

            var selectedTemperatureId = (TemperatureSensorCombo.SelectedItem as SensorReading)?.Id;
            var selectedFanId = (FanSensorCombo.SelectedItem as SensorReading)?.Id;
            _temperatureCelsius = snapshot.Temperatures.FirstOrDefault(sensor => sensor.Id == selectedTemperatureId)?.Value
                ?? snapshot.Temperatures.FirstOrDefault(sensor => sensor.Value.HasValue)?.Value;
            _fanRpm = snapshot.Fans.FirstOrDefault(sensor => sensor.Id == selectedFanId)?.Value
                ?? snapshot.Fans.FirstOrDefault(sensor => sensor.Value.HasValue)?.Value;

            UpdateSessionStatistics();
            UpdatePreview();

            if (!_cooler.IsConnected
                && DateTime.UtcNow - _lastReconnectAttempt > TimeSpan.FromSeconds(5)
                && Process.GetProcessesByName("ZEUS CAST").Length == 0)
            {
                _lastReconnectAttempt = DateTime.UtcNow;
                await TryConnectAsync();
            }

            if (_cooler.IsConnected)
            {
                await SendCurrentValueAsync();
            }
        }
        catch (Exception exception)
        {
            if (_cooler.IsConnected)
            {
                _cooler.Disconnect();
                SetDeviceState(false, "Cooler connection lost — retrying automatically");
            }

            SetStatus(exception.Message);
        }
        finally
        {
            _refreshing = false;
        }
    }

    private void UpdateSensorBackendText(IReadOnlyList<SensorReading> temperatures, IReadOnlyList<SensorReading> fans)
    {
        var liveTemperatures = temperatures.Count(sensor => sensor.Value.HasValue);
        var liveFans = fans.Count(sensor => sensor.Value.HasValue);
        SensorBackendText.Text = $"{_hardwareMonitor.BackendName} • {liveTemperatures} temperatures • {liveFans} fans";
    }

    private void UpdateSessionStatistics()
    {
        if (_temperatureCelsius.HasValue)
        {
            _minimumTemperature = Math.Min(_minimumTemperature ?? _temperatureCelsius.Value, _temperatureCelsius.Value);
            _maximumTemperature = Math.Max(_maximumTemperature ?? _temperatureCelsius.Value, _temperatureCelsius.Value);
        }

        if (_fanRpm.HasValue)
        {
            _minimumFanRpm = Math.Min(_minimumFanRpm ?? _fanRpm.Value, _fanRpm.Value);
            _maximumFanRpm = Math.Max(_maximumFanRpm ?? _fanRpm.Value, _fanRpm.Value);
        }

        var details = new List<string>();
        if (_minimumTemperature.HasValue && _maximumTemperature.HasValue)
        {
            details.Add($"Temperature {_minimumTemperature:0.0}–{_maximumTemperature:0.0} °C");
        }

        if (_minimumFanRpm.HasValue && _maximumFanRpm.HasValue)
        {
            details.Add($"Fan {_minimumFanRpm:0}–{_maximumFanRpm:0} RPM");
        }

        SessionStatsText.Text = details.Count > 0
            ? "SESSION  •  " + string.Join("  •  ", details)
            : "Waiting for live sensor readings…";
    }

    private void UpdatePreview()
    {
        var selectedMode = GetSelectedMode();
        var mode = GetEffectiveMode();
        var autoLabel = selectedMode == "cycle" ? "  •  AUTO CYCLE" : string.Empty;
        switch (mode)
        {
            case "fan":
                PreviewValue.Text = _fanRpm.HasValue ? Math.Round(_fanRpm.Value).ToString("0") : "----";
                PreviewLabel.Text = "FAN SPEED  •  RPM" + autoLabel;
                ThermalProgress.Value = 0;
                break;
            case "custom":
                var customValue = ReadCustomValue();
                PreviewValue.Text = customValue.ToString("0000");
                PreviewLabel.Text = "CUSTOM DISPLAY VALUE";
                ThermalProgress.Value = 0;
                break;
            default:
                if (_temperatureCelsius.HasValue)
                {
                    var displayed = _settings.UseFahrenheit
                        ? (_temperatureCelsius.Value * 1.8) + 32
                        : _temperatureCelsius.Value;
                    PreviewValue.Text = displayed.ToString("0.0");
                    ThermalProgress.Value = Math.Clamp(_temperatureCelsius.Value, 0, 100);
                }
                else
                {
                    PreviewValue.Text = "--.-";
                    ThermalProgress.Value = 0;
                }

                PreviewLabel.Text = (_settings.UseFahrenheit
                    ? "CPU TEMPERATURE  •  °F"
                    : "CPU TEMPERATURE  •  °C") + autoLabel;
                break;
        }

        UpdateTrayTooltip();
    }

    private string GetEffectiveMode()
    {
        var selectedMode = GetSelectedMode();
        if (selectedMode != "cycle")
        {
            return selectedMode;
        }

        if (!_temperatureCelsius.HasValue)
        {
            return _fanRpm.HasValue ? "fan" : "temperature";
        }

        if (!_fanRpm.HasValue)
        {
            return "temperature";
        }

        return (int)((DateTime.UtcNow - _cycleStarted).TotalSeconds / 5) % 2 == 0
            ? "temperature"
            : "fan";
    }

    private async Task SendCurrentValueAsync()
    {
        switch (GetEffectiveMode())
        {
            case "fan" when _fanRpm.HasValue:
                await _cooler.SendFanAsync(_fanRpm.Value);
                break;
            case "custom":
                await _cooler.SendCustomAsync(ReadCustomValue());
                break;
            case "temperature" when _temperatureCelsius.HasValue:
                var flashing = _settings.AlarmEnabled
                    && _temperatureCelsius.Value >= _settings.AlarmThresholdCelsius;
                await _cooler.SendTemperatureAsync(_temperatureCelsius.Value, _settings.UseFahrenheit, flashing);
                break;
        }
    }

    private async Task TryConnectAsync()
    {
        if (Process.GetProcessesByName("ZEUS CAST").Length > 0)
        {
            _startupOverride.StopOriginalProcesses();
            await Task.Delay(350);
        }

        SetDeviceState(false, "Connecting to CHIONE Digital…");
        var connected = await _cooler.ConnectAsync();
        if (connected)
        {
            SetDeviceState(true, $"{_cooler.DeviceName ?? "CHIONE Digital"} connected");
            ConnectButton.Content = "Reconnect cooler";
            SetStatus("Enhanced has exclusive control of the cooler display.");
            return;
        }

        SetDeviceState(false, "CHIONE Digital unavailable — retrying automatically");
        ConnectButton.Content = "Retry connection";
        SetStatus("Check the USB connection; Enhanced will retry automatically.");
    }

    private async void ConnectButton_Click(object sender, RoutedEventArgs e)
    {
        ConnectButton.IsEnabled = false;
        try
        {
            _startupOverride.StopOriginalProcesses();
            await Task.Delay(350);
            await TryConnectAsync();
            await RefreshAsync();
        }
        finally
        {
            ConnectButton.IsEnabled = true;
        }
    }

    private async void SendNow_Click(object sender, RoutedEventArgs e)
    {
        if (!_cooler.IsConnected)
        {
            await TryConnectAsync();
        }

        if (!_cooler.IsConnected)
        {
            return;
        }

        try
        {
            await SendCurrentValueAsync();
            SetStatus("Display updated.");
        }
        catch (Exception exception)
        {
            SetStatus($"Could not update display: {exception.Message}");
        }
    }

    private async void RefreshSensors_Click(object sender, RoutedEventArgs e)
    {
        if (sender is Button button)
        {
            button.IsEnabled = false;
            try
            {
                _startupOverride.StopOriginalProcesses();
                await LoadSensorsAsync(restart: true);
                await RefreshAsync();
            }
            finally
            {
                button.IsEnabled = true;
            }
        }
    }

    private async void RestoreOriginal_Click(object sender, RoutedEventArgs e)
    {
        _refreshTimer.Stop();
        _cooler.Disconnect();
        _settings.StartWithWindows = false;
        _settings.ReplaceOriginalZeusCast = false;
        var wasInitializing = _initializing;
        _initializing = true;
        StartWithWindowsCheck.IsChecked = false;
        _initializing = wasInitializing;
        SaveSettings();
        SetStatus(await _startupOverride.RestoreOriginalAsync());
        SetDeviceState(false, "Control released to original Zeus Cast");

        if (!File.Exists(OriginalAppPath))
        {
            SetStatus("The original Zeus Cast installation was not found.");
            return;
        }

        Process.Start(new ProcessStartInfo(OriginalAppPath)
        {
            WorkingDirectory = Path.GetDirectoryName(OriginalAppPath),
            UseShellExecute = true
        });
        _allowExit = true;
        Close();
    }

    private void Setting_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }

        ReadSettingsFromUi();
        UpdateModeState();
        UpdatePreview();
        SaveSettings();
    }

    private void Setting_Changed(object sender, TextChangedEventArgs e)
    {
        Setting_Changed(sender, new RoutedEventArgs());
    }

    private void Setting_Changed(object sender, SelectionChangedEventArgs e)
    {
        Setting_Changed(sender, new RoutedEventArgs());
    }

    private void SensorSelection_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }

        ReadSettingsFromUi();
        SaveSettings();
    }

    private void RefreshRate_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }

        ReadSettingsFromUi();
        _refreshTimer.Interval = TimeSpan.FromMilliseconds(_settings.RefreshMilliseconds);
        SaveSettings();
    }

    private async void Startup_Changed(object sender, RoutedEventArgs e)
    {
        if (_initializing)
        {
            return;
        }

        var enabled = StartWithWindowsCheck.IsChecked == true;
        _settings.StartWithWindows = enabled;
        _settings.ReplaceOriginalZeusCast = enabled;
        SaveSettings();
        SetStatus(enabled
            ? await _startupOverride.ApplyOverrideAsync()
            : await _startupOverride.RestoreOriginalAsync());
    }

    private void ReadSettingsFromUi()
    {
        _settings.DisplayMode = GetSelectedMode();
        _settings.UseFahrenheit = FahrenheitRadio.IsChecked == true;
        _settings.CustomValue = ReadCustomValue();
        _settings.AlarmEnabled = AlarmCheck.IsChecked == true;
        if (int.TryParse(ThresholdText.Text, out var threshold))
        {
            _settings.AlarmThresholdCelsius = Math.Clamp(threshold, 40, 110);
        }

        if (RefreshRateCombo.SelectedItem is ComboBoxItem refreshItem
            && int.TryParse(refreshItem.Tag?.ToString(), out var refreshMilliseconds))
        {
            _settings.RefreshMilliseconds = refreshMilliseconds;
        }

        _settings.TemperatureSensorId = (TemperatureSensorCombo.SelectedItem as SensorReading)?.Id;
        _settings.FanSensorId = (FanSensorCombo.SelectedItem as SensorReading)?.Id;
    }

    private void SaveSettings()
    {
        try
        {
            _settingsService.Save(_settings);
        }
        catch (Exception exception)
        {
            SetStatus($"Could not save settings: {exception.Message}");
        }
    }

    private void UpdateModeState()
    {
        var mode = GetSelectedMode();
        var usesTemperature = mode is "temperature" or "cycle";
        UnitPanel.Opacity = usesTemperature ? 1 : 0.35;
        UnitPanel.IsEnabled = usesTemperature;
        CustomPanel.Opacity = mode == "custom" ? 1 : 0.35;
        CustomPanel.IsEnabled = mode == "custom";
        AlarmCheck.IsEnabled = usesTemperature;
    }

    private string GetSelectedMode()
    {
        return (DisplayModeCombo.SelectedItem as ComboBoxItem)?.Tag?.ToString() ?? "temperature";
    }

    private int ReadCustomValue()
    {
        return int.TryParse(CustomValueText.Text, out var value) ? Math.Clamp(value, 0, 9999) : _settings.CustomValue;
    }

    private static void SelectComboByTag(System.Windows.Controls.ComboBox comboBox, string tag)
    {
        comboBox.SelectedItem = comboBox.Items
            .OfType<ComboBoxItem>()
            .FirstOrDefault(item => string.Equals(item.Tag?.ToString(), tag, StringComparison.OrdinalIgnoreCase))
            ?? comboBox.Items.OfType<ComboBoxItem>().FirstOrDefault();
    }

    private void SetDeviceState(bool connected, string message)
    {
        DeviceStatusText.Text = message;
        DeviceStatusDot.Fill = new SolidColorBrush(connected
            ? Color.FromRgb(0x38, 0xD9, 0xA9)
            : Color.FromRgb(0x66, 0x70, 0x85));
    }

    private void SetStatus(string message)
    {
        StatusText.Text = message;
    }

    private void Numeric_PreviewTextInput(object sender, TextCompositionEventArgs e)
    {
        e.Handled = !Regex.IsMatch(e.Text, "^[0-9]+$");
    }

    private void TitleBar_MouseLeftButtonDown(object sender, MouseButtonEventArgs e)
    {
        if (e.ChangedButton != MouseButton.Left)
        {
            return;
        }

        if (e.ClickCount == 2)
        {
            ToggleMaximize();
            return;
        }

        DragMove();
    }

    private void Minimize_Click(object sender, RoutedEventArgs e)
    {
        WindowState = WindowState.Minimized;
    }

    private void Window_StateChanged(object? sender, EventArgs e)
    {
        if (WindowState == WindowState.Minimized)
        {
            Dispatcher.BeginInvoke(() => HideToTray(showHint: true));
        }
    }

    private void Maximize_Click(object sender, RoutedEventArgs e)
    {
        ToggleMaximize();
    }

    private void ToggleMaximize()
    {
        WindowState = WindowState == WindowState.Maximized ? WindowState.Normal : WindowState.Maximized;
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        Close();
    }

    private void InitializeTrayIcon()
    {
        if (_trayIcon is not null)
        {
            return;
        }

        _trayIconImage = System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath ?? string.Empty);
        _trayMenu = new System.Windows.Forms.ContextMenuStrip();
        var openItem = new System.Windows.Forms.ToolStripMenuItem("Open Zeus Cast Enhanced");
        openItem.Click += (_, _) => Dispatcher.Invoke(ShowFromTray);
        var exitItem = new System.Windows.Forms.ToolStripMenuItem("Exit");
        exitItem.Click += (_, _) => Dispatcher.Invoke(ExitFromTray);
        _trayMenu.Items.Add(openItem);
        _trayMenu.Items.Add(new System.Windows.Forms.ToolStripSeparator());
        _trayMenu.Items.Add(exitItem);

        _trayIcon = new System.Windows.Forms.NotifyIcon
        {
            Icon = _trayIconImage,
            Text = "Zeus Cast Enhanced",
            ContextMenuStrip = _trayMenu,
            Visible = true
        };
        _trayIcon.MouseClick += (_, eventArgs) =>
        {
            if (eventArgs.Button == System.Windows.Forms.MouseButtons.Left)
            {
                Dispatcher.Invoke(ShowFromTray);
            }
        };
        _trayIcon.DoubleClick += (_, _) => Dispatcher.Invoke(ShowFromTray);
    }

    private void HideToTray(bool showHint)
    {
        ShowInTaskbar = false;
        Hide();

        if (showHint && !_trayHintShown && _trayIcon is not null)
        {
            _trayHintShown = true;
            _trayIcon.BalloonTipTitle = "Zeus Cast Enhanced is still running";
            _trayIcon.BalloonTipText = "Temperature and fan monitoring continue in the system tray.";
            _trayIcon.ShowBalloonTip(2500);
        }
    }

    private void ShowFromTray()
    {
        ShowInTaskbar = true;
        Show();
        WindowState = WindowState.Normal;
        Activate();
    }

    private void ExitFromTray()
    {
        _allowExit = true;
        Close();
    }

    private void UpdateTrayTooltip()
    {
        if (_trayIcon is null)
        {
            return;
        }

        var details = new List<string> { "Zeus Cast" };
        if (_temperatureCelsius.HasValue)
        {
            var temperature = _settings.UseFahrenheit
                ? (_temperatureCelsius.Value * 1.8) + 32
                : _temperatureCelsius.Value;
            details.Add($"{temperature:0.0} °{(_settings.UseFahrenheit ? "F" : "C")}");
        }

        if (_fanRpm.HasValue)
        {
            details.Add($"{_fanRpm:0} RPM");
        }

        var tooltip = string.Join(" • ", details);
        _trayIcon.Text = tooltip.Length <= 63 ? tooltip : tooltip[..63];
    }

    private void Window_SourceInitialized(object? sender, EventArgs e)
    {
        var handle = new WindowInteropHelper(this).Handle;
        var enabled = 1;
        var rounded = 2;
        DwmSetWindowAttribute(handle, DwmUseImmersiveDarkMode, ref enabled, sizeof(int));
        DwmSetWindowAttribute(handle, DwmWindowCornerPreference, ref rounded, sizeof(int));
    }

    private void Window_Closing(object? sender, CancelEventArgs e)
    {
        if (!_allowExit)
        {
            e.Cancel = true;
            HideToTray(showHint: true);
            return;
        }

        _refreshTimer.Stop();
        _cooler.Dispose();
        _hardwareMonitor.Dispose();
        if (_trayIcon is not null)
        {
            _trayIcon.Visible = false;
            _trayIcon.Dispose();
        }

        _trayMenu?.Dispose();
        _trayIconImage?.Dispose();
    }

    [DllImport("dwmapi.dll")]
    private static extern int DwmSetWindowAttribute(IntPtr windowHandle, int attribute, ref int value, int valueSize);
}
