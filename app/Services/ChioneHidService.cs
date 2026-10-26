using HidSharp;

namespace ZeusCastEnhanced.Services;

public sealed class ChioneHidService : IDisposable
{
    public const int VendorId = 0x1B80;
    public const int ProductId = 0xB53C;
    private readonly SemaphoreSlim _writeGate = new(1, 1);
    private HidStream? _stream;

    public bool IsConnected => _stream is not null;
    public string? DeviceName { get; private set; }

    public async Task<bool> ConnectAsync()
    {
        Disconnect();
        var device = DeviceList.Local.GetHidDevices(VendorId, ProductId)
            .OrderByDescending(item => item.GetMaxOutputReportLength())
            .FirstOrDefault(item => item.GetMaxOutputReportLength() >= 65);

        if (device is null || !device.TryOpen(out var stream))
        {
            return false;
        }

        stream.ReadTimeout = 150;
        stream.WriteTimeout = 500;
        _stream = stream;
        DeviceName = GetDeviceName(device);

        try
        {
            await WriteReportAsync(BuildHandshakeReport());
            await Task.Delay(100);
        }
        catch
        {
            Disconnect();
            return false;
        }

        return true;
    }

    public Task SendTemperatureAsync(double celsius, bool fahrenheit, bool flashing)
    {
        var value = fahrenheit ? (celsius * 1.8) + 32 : celsius;
        var tenths = Math.Clamp((int)Math.Round(value * 10), 0, 9999);
        return WriteReportAsync(BuildDisplayReport(ToDigits(tenths), true, fahrenheit, true, false, flashing));
    }

    public Task SendFanAsync(double rpm)
    {
        var digits = ToDigits(Math.Clamp((int)Math.Round(rpm), 0, 9999));
        return WriteReportAsync(BuildDisplayReport(digits, false, false, false, true, false));
    }

    public Task SendCustomAsync(int value)
    {
        var digits = ToDigits(Math.Clamp(value, 0, 9999));
        return WriteReportAsync(BuildDisplayReport(digits, false, false, false, false, false));
    }

    private async Task WriteReportAsync(byte[] report)
    {
        var stream = _stream ?? throw new InvalidOperationException("The CHIONE Digital cooler is not connected.");
        await _writeGate.WaitAsync();
        try
        {
            await stream.WriteAsync(report);
            await stream.FlushAsync();
        }
        finally
        {
            _writeGate.Release();
        }
    }

    private static byte[] BuildHandshakeReport()
    {
        var report = new byte[65];
        report[1] = 0x3A;
        report[2] = 0xB5;
        report[3] = 0xFF;
        report[4] = 0x20;
        report[5] = 0x20;
        report[6] = 0x20;
        report[7] = 0x20;
        report[9] = 1;
        report[10] = 1;
        report[13] = CalculateChecksum(report);
        return report;
    }

    private static byte[] BuildDisplayReport(byte[] digits, bool decimalPoint, bool fahrenheit, bool cpuIndicator, bool rpmIndicator, bool flashing)
    {
        var report = new byte[65];
        report[1] = 0x3A;
        report[2] = 0xB5;
        report[3] = 1;
        report[4] = digits[0] == 0 ? (byte)0x20 : digits[0];
        report[5] = digits[1];
        report[6] = digits[2];
        report[7] = digits[3];
        report[8] = decimalPoint ? (byte)1 : (byte)0;
        report[9] = fahrenheit ? (byte)0 : (byte)1;
        report[10] = cpuIndicator ? (byte)1 : (byte)0;
        report[11] = rpmIndicator ? (byte)1 : (byte)0;
        report[12] = flashing ? (byte)1 : (byte)0;
        report[13] = CalculateChecksum(report);
        return report;
    }

    private static byte[] ToDigits(int value)
    {
        return
        [
            (byte)(value / 1000),
            (byte)((value / 100) % 10),
            (byte)((value / 10) % 10),
            (byte)(value % 10)
        ];
    }

    private static byte CalculateChecksum(byte[] report)
    {
        var sum = 0;
        for (var index = 0; index < 13; index++)
        {
            sum += report[index];
        }

        return (byte)sum;
    }

    private static string GetDeviceName(HidDevice device)
    {
        try
        {
            return device.GetProductName();
        }
        catch
        {
            return "CHIONE Digital";
        }
    }

    public void Disconnect()
    {
        _stream?.Dispose();
        _stream = null;
        DeviceName = null;
    }

    public void Dispose()
    {
        Disconnect();
        _writeGate.Dispose();
    }
}
