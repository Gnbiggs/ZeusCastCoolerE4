using System.IO;
using System.Runtime.InteropServices;
using ZeusCastEnhanced.Models;

namespace ZeusCastEnhanced.Services;

public sealed class CpuidSdkNative : IDisposable
{
    private const uint CreateInstanceCode = 2197226990;
    private const uint DestroyInstanceCode = 2161443242;
    private const uint InitializeCode = 2893371626;
    private const uint CloseCode = 1288739233;
    private const uint RefreshCode = 1329766021;
    private const uint GetDeviceCountCode = 2006511409;
    private const uint GetDeviceNameCode = 2570400362;
    private const uint GetSensorCountCode = 3829778571;
    private const uint GetSensorInfoCode = 2900711882;
    private const uint GetSensorTypeValueCode = 1614200941;
    private const int TemperatureSensorClass = 8192;
    private const int FanSensorClass = 12288;
    private const int CpuTemperatureType = 4202496;
    private const int CpuPackageTemperatureType = 25174016;
    private const int CpuFanType = 4206592;
    private const int PumpFanType = 8400896;
    private IntPtr _libraryHandle;
    private IntPtr _instance;
    private QueryInterfaceDelegate? _queryInterface;
    private bool _initialized;

    public string? LastError { get; private set; }

    public bool TryInitialize()
    {
        Dispose();
        LastError = null;
        var libraryPath = ResolveLibraryPath();
        if (libraryPath is null)
        {
            LastError = "GAMDIAS CPUID SDK was not found. Install the official Zeus Cast package or place cpuidsdk64.dll in the app's Native folder.";
            return false;
        }

        try
        {
            _libraryHandle = NativeLibrary.Load(libraryPath);
            var queryPointer = NativeLibrary.GetExport(_libraryHandle, "QueryInterface");
            _queryInterface = Marshal.GetDelegateForFunctionPointer<QueryInterfaceDelegate>(queryPointer);
            var createInstance = GetFunction<CreateInstanceDelegate>(CreateInstanceCode);
            _instance = createInstance?.Invoke() ?? IntPtr.Zero;
            if (_instance == IntPtr.Zero)
            {
                LastError = "GAMDIAS CPUID SDK could not create an instance.";
                Dispose();
                return false;
            }

            var initialize = GetFunction<InitializeDelegate>(InitializeCode);
            if (initialize is null)
            {
                LastError = "GAMDIAS CPUID SDK initialization function is unavailable.";
                Dispose();
                return false;
            }

            var errorCode = 0;
            var extendedErrorCode = 0;
            var initialized = initialize(
                _instance,
                Path.GetDirectoryName(libraryPath) + Path.DirectorySeparatorChar,
                "cpuidsdk64.dll",
                int.MaxValue,
                ref errorCode,
                ref extendedErrorCode) == 1;
            if (!initialized)
            {
                LastError = DescribeInitializationError(errorCode, extendedErrorCode);
                Dispose();
                return false;
            }

            _initialized = true;
            Refresh();
            return true;
        }
        catch (Exception exception)
        {
            LastError = $"GAMDIAS CPUID SDK failed: {exception.Message}";
            Dispose();
            return false;
        }
    }

    public (IReadOnlyList<SensorReading> Temperatures, IReadOnlyList<SensorReading> Fans) ReadSensors()
    {
        if (!_initialized)
        {
            return ([], []);
        }

        Refresh();
        var temperatures = ReadSensorClass(TemperatureSensorClass, "temperature");
        var fans = ReadSensorClass(FanSensorClass, "fan");

        AddTypedSensorIfMissing(temperatures, CpuPackageTemperatureType, "cpuid/type/cpu-package", "CPU Package");
        AddTypedSensorIfMissing(temperatures, CpuTemperatureType, "cpuid/type/cpu", "CPU Temperature");
        AddTypedSensorIfMissing(fans, CpuFanType, "cpuid/type/cpu-fan", "CPU Fan");
        AddTypedSensorIfMissing(fans, PumpFanType, "cpuid/type/pump", "Pump");

        return (
            temperatures.OrderByDescending(sensor => sensor.Name.Contains("Package", StringComparison.OrdinalIgnoreCase)).ThenBy(sensor => sensor.Name).ToArray(),
            fans.OrderByDescending(sensor => sensor.Name.Contains("CPU", StringComparison.OrdinalIgnoreCase)).ThenBy(sensor => sensor.Name).ToArray());
    }

    private List<SensorReading> ReadSensorClass(int sensorClass, string category)
    {
        var readings = new List<SensorReading>();
        var getDeviceCount = GetFunction<GetDeviceCountDelegate>(GetDeviceCountCode);
        var getDeviceName = GetFunction<GetDeviceNameDelegate>(GetDeviceNameCode);
        var getSensorCount = GetFunction<GetSensorCountDelegate>(GetSensorCountCode);
        var getSensorInfo = GetFunction<GetSensorInfoDelegate>(GetSensorInfoCode);
        if (getDeviceCount is null || getSensorCount is null || getSensorInfo is null)
        {
            return readings;
        }

        var deviceCount = Math.Max(0, getDeviceCount(_instance));
        for (var deviceIndex = 0; deviceIndex < deviceCount; deviceIndex++)
        {
            var deviceNamePointer = getDeviceName?.Invoke(_instance, deviceIndex) ?? IntPtr.Zero;
            var deviceName = Marshal.PtrToStringAnsi(deviceNamePointer) ?? $"Device {deviceIndex + 1}";
            var sensorCount = Math.Max(0, getSensorCount(_instance, deviceIndex, sensorClass));

            for (var sensorIndex = 0; sensorIndex < sensorCount; sensorIndex++)
            {
                var sensorId = 0;
                var sensorNamePointer = IntPtr.Zero;
                var rawValue = 0;
                var value = float.NaN;
                var minimum = float.NaN;
                var maximum = float.NaN;
                var succeeded = getSensorInfo(
                    _instance,
                    deviceIndex,
                    sensorIndex,
                    sensorClass,
                    ref sensorId,
                    ref sensorNamePointer,
                    ref rawValue,
                    ref value,
                    ref minimum,
                    ref maximum) == 1;
                if (!succeeded)
                {
                    continue;
                }

                var sensorName = Marshal.PtrToStringAnsi(sensorNamePointer) ?? $"Sensor {sensorIndex + 1}";
                readings.Add(new SensorReading(
                    $"cpuid/{category}/{deviceIndex}/{sensorIndex}/{sensorId}",
                    $"{deviceName} • {sensorName}",
                    IsValidSensorValue(value) ? value : null));
            }
        }

        return readings;
    }

    private void AddTypedSensorIfMissing(List<SensorReading> readings, int sensorType, string id, string name)
    {
        var getSensorTypeValue = GetFunction<GetSensorTypeValueDelegate>(GetSensorTypeValueCode);
        if (getSensorTypeValue is null)
        {
            return;
        }

        var deviceIndex = -1;
        var sensorIndex = -1;
        var value = getSensorTypeValue(_instance, sensorType, ref deviceIndex, ref sensorIndex);
        if (!IsValidSensorValue(value))
        {
            return;
        }

        if (readings.All(sensor => sensor.Value != value || !sensor.Name.Contains(name, StringComparison.OrdinalIgnoreCase)))
        {
            readings.Insert(0, new SensorReading(id, $"GAMDIAS CPUID • {name}", value));
        }
    }

    private void Refresh()
    {
        GetFunction<RefreshDelegate>(RefreshCode)?.Invoke(_instance);
    }

    private T? GetFunction<T>(uint code) where T : Delegate
    {
        var pointer = _queryInterface?.Invoke(code) ?? IntPtr.Zero;
        return pointer == IntPtr.Zero ? null : Marshal.GetDelegateForFunctionPointer<T>(pointer);
    }

    private static bool IsValidSensorValue(float value)
    {
        return float.IsFinite(value) && value >= 0 && value < 100000;
    }

    private static string? ResolveLibraryPath()
    {
        var bundledPath = Path.Combine(AppContext.BaseDirectory, "Native", "cpuidsdk64.dll");
        var vendorPath = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.ProgramFilesX86),
            "GAMDIAS",
            "ZEUS CAST",
            "cpuidsdk64.dll");

        return new[] { bundledPath, vendorPath }.FirstOrDefault(File.Exists);
    }

    private static string DescribeInitializationError(int errorCode, int extendedErrorCode)
    {
        return errorCode switch
        {
            2 => $"The GAMDIAS sensor driver could not start ({errorCode}/{extendedErrorCode}). Close the original Zeus Cast and refresh sensors.",
            8 => $"The GAMDIAS sensor driver is already in use ({errorCode}/{extendedErrorCode}). Close the original Zeus Cast and refresh sensors.",
            16 => $"The GAMDIAS sensor library is invalid or blocked ({errorCode}/{extendedErrorCode}).",
            _ => $"GAMDIAS CPUID SDK error {errorCode}/{extendedErrorCode}."
        };
    }

    public void Dispose()
    {
        if (_instance != IntPtr.Zero)
        {
            if (_initialized)
            {
                GetFunction<CloseDelegate>(CloseCode)?.Invoke(_instance);
            }

            GetFunction<DestroyInstanceDelegate>(DestroyInstanceCode)?.Invoke(_instance);
            _instance = IntPtr.Zero;
        }

        _initialized = false;
        _queryInterface = null;
        if (_libraryHandle != IntPtr.Zero)
        {
            NativeLibrary.Free(_libraryHandle);
            _libraryHandle = IntPtr.Zero;
        }
    }

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr QueryInterfaceDelegate(uint code);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr CreateInstanceDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void DestroyInstanceDelegate(IntPtr instance);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl, CharSet = CharSet.Ansi)]
    private delegate int InitializeDelegate(
        IntPtr instance,
        [MarshalAs(UnmanagedType.LPStr)] string libraryPath,
        [MarshalAs(UnmanagedType.LPStr)] string libraryName,
        int configuration,
        ref int errorCode,
        ref int extendedErrorCode);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void CloseDelegate(IntPtr instance);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate void RefreshDelegate(IntPtr instance);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetDeviceCountDelegate(IntPtr instance);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate IntPtr GetDeviceNameDelegate(IntPtr instance, int deviceIndex);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetSensorCountDelegate(IntPtr instance, int deviceIndex, int sensorClass);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int GetSensorInfoDelegate(
        IntPtr instance,
        int deviceIndex,
        int sensorIndex,
        int sensorClass,
        ref int sensorId,
        ref IntPtr sensorName,
        ref int rawValue,
        ref float value,
        ref float minimum,
        ref float maximum);

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate float GetSensorTypeValueDelegate(IntPtr instance, int sensorType, ref int deviceIndex, ref int sensorIndex);
}
