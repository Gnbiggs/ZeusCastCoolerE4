using LibreHardwareMonitor.Hardware;
using ZeusCastEnhanced.Models;

namespace ZeusCastEnhanced.Services;

public sealed class HardwareMonitorService : IDisposable
{
    private readonly Computer _computer = new()
    {
        IsCpuEnabled = true,
        IsMotherboardEnabled = true,
        IsControllerEnabled = true
    };

    private readonly CpuidSdkNative _cpuidSdk = new();
    private readonly object _gate = new();
    private bool _opened;
    private bool _libreOpened;
    private bool _cpuidOpened;
    private DateTime _lastCpuidAttempt = DateTime.MinValue;

    public IReadOnlyList<SensorReading> TemperatureSensors { get; private set; } = [];
    public IReadOnlyList<SensorReading> FanSensors { get; private set; } = [];
    public string BackendName { get; private set; } = "Starting sensor service…";
    public string? LastError { get; private set; }

    public void Open()
    {
        lock (_gate)
        {
            if (_opened)
            {
                return;
            }

            TryOpenCpuid();
            if (!_cpuidOpened)
            {
                OpenLibreHardwareMonitor();
            }

            _opened = true;
            RefreshCore();
        }
    }

    public (IReadOnlyList<SensorReading> Temperatures, IReadOnlyList<SensorReading> Fans) Refresh()
    {
        lock (_gate)
        {
            if (!_opened)
            {
                Open();
            }

            if (!_cpuidOpened && DateTime.UtcNow - _lastCpuidAttempt > TimeSpan.FromSeconds(10))
            {
                TryOpenCpuid();
            }

            RefreshCore();
            return (TemperatureSensors, FanSensors);
        }
    }

    private void RefreshCore()
    {
        if (_cpuidOpened)
        {
            var cpuidSnapshot = _cpuidSdk.ReadSensors();
            if (cpuidSnapshot.Temperatures.Any(sensor => sensor.Value.HasValue))
            {
                TemperatureSensors = cpuidSnapshot.Temperatures;
                FanSensors = cpuidSnapshot.Fans;
                BackendName = "GAMDIAS CPUID SDK • native readings";
                LastError = null;
                return;
            }

            LastError = "GAMDIAS CPUID SDK opened but returned no live values.";
        }

        OpenLibreHardwareMonitor();
        var temperatures = new List<SensorReading>();
        var fans = new List<SensorReading>();
        foreach (var hardware in _computer.Hardware)
        {
            VisitHardware(hardware, temperatures, fans);
        }

        TemperatureSensors = temperatures
            .OrderByDescending(sensor => IsPreferredTemperature(sensor.Name))
            .ThenBy(sensor => sensor.Name)
            .ToArray();
        FanSensors = fans
            .OrderByDescending(sensor => sensor.Name.Contains("CPU", StringComparison.OrdinalIgnoreCase))
            .ThenBy(sensor => sensor.Name)
            .ToArray();
        BackendName = _cpuidOpened
            ? "LibreHardwareMonitor fallback • GAMDIAS returned no values"
            : "LibreHardwareMonitor fallback";

        if (!TemperatureSensors.Any(sensor => sensor.Value.HasValue))
        {
            LastError = _cpuidSdk.LastError ?? "No sensor backend returned live temperature values.";
        }
    }

    private void TryOpenCpuid()
    {
        _lastCpuidAttempt = DateTime.UtcNow;
        _cpuidOpened = _cpuidSdk.TryInitialize();
        if (_cpuidOpened)
        {
            BackendName = "GAMDIAS CPUID SDK";
            LastError = null;
        }
        else
        {
            LastError = _cpuidSdk.LastError;
        }
    }

    private void OpenLibreHardwareMonitor()
    {
        if (_libreOpened)
        {
            return;
        }

        _computer.Open();
        _libreOpened = true;
    }

    private static void VisitHardware(IHardware hardware, ICollection<SensorReading> temperatures, ICollection<SensorReading> fans)
    {
        hardware.Update();

        foreach (var sensor in hardware.Sensors)
        {
            var name = $"{hardware.Name} • {sensor.Name}";
            var reading = new SensorReading(sensor.Identifier.ToString(), name, sensor.Value);
            if (sensor.SensorType == SensorType.Temperature)
            {
                temperatures.Add(reading);
            }
            else if (sensor.SensorType == SensorType.Fan)
            {
                fans.Add(reading);
            }
        }

        foreach (var subHardware in hardware.SubHardware)
        {
            VisitHardware(subHardware, temperatures, fans);
        }
    }

    private static bool IsPreferredTemperature(string name)
    {
        return name.Contains("CPU Package", StringComparison.OrdinalIgnoreCase)
            || name.Contains("CPU (Tctl", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("Package", StringComparison.OrdinalIgnoreCase);
    }

    public void Dispose()
    {
        lock (_gate)
        {
            _cpuidSdk.Dispose();
            if (_libreOpened)
            {
                _computer.Close();
            }

            _cpuidOpened = false;
            _libreOpened = false;
            _opened = false;
        }
    }
}
