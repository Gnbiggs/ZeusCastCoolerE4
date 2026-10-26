using System.Windows;
using System.Windows.Media;
using System.Windows.Media.Imaging;
using ZeusCastEnhanced.Services;

namespace ZeusCastDiagnostics;

internal static class Program
{
    [STAThread]
    private static int Main(string[] args)
    {
        if (args.Length == 2 && args[0] == "--render")
        {
            RenderUi(args[1]);
            return 0;
        }

        return RunDiagnosticsAsync().GetAwaiter().GetResult();
    }

    private static async Task<int> RunDiagnosticsAsync()
    {

        using var hardware = new HardwareMonitorService();
        using var cooler = new ChioneHidService();

        Console.WriteLine("Zeus Cast Enhanced diagnostics");
        Console.WriteLine($"Process elevated: {new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent()).IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator)}");

        try
        {
            hardware.Open();
            var snapshot = hardware.Refresh();
            Console.WriteLine($"Sensor backend: {hardware.BackendName}");
            Console.WriteLine($"Sensor backend detail: {hardware.LastError ?? "none"}");
            Console.WriteLine($"Temperature sensors: {snapshot.Temperatures.Count}");
            foreach (var sensor in snapshot.Temperatures.Take(8))
            {
                Console.WriteLine($"  {FormatValue(sensor.Value),6} °C  {sensor.Name}");
            }

            Console.WriteLine($"Fan sensors: {snapshot.Fans.Count}");
            foreach (var sensor in snapshot.Fans.Take(8))
            {
                Console.WriteLine($"  {FormatValue(sensor.Value),6} RPM {sensor.Name}");
            }

            var originalRunning = System.Diagnostics.Process.GetProcessesByName("ZEUS CAST").Length > 0;
            var connected = !originalRunning && await cooler.ConnectAsync();
            Console.WriteLine($"Cooler connected: {connected}");
            if (originalRunning)
            {
                Console.WriteLine("Cooler write test skipped because original Zeus Cast is running.");
            }
            if (connected)
            {
                var temperature = snapshot.Temperatures.FirstOrDefault(sensor => sensor.Value.HasValue);
                if (temperature?.Value is { } value)
                {
                    await cooler.SendTemperatureAsync(value, fahrenheit: false, flashing: false);
                    Console.WriteLine($"Sent actual temperature: {value:0.0} °C");
                }
            }

            return 0;
        }
        catch (Exception exception)
        {
            Console.Error.WriteLine(exception);
            return 1;
        }
    }

    private static string FormatValue(float? value)
    {
        return value.HasValue ? value.Value.ToString("0.0") : "n/a";
    }

    private static void RenderUi(string outputPath)
    {
        var application = new ZeusCastEnhanced.App();
        application.InitializeComponent();
        var window = new ZeusCastEnhanced.MainWindow();
        var visual = (FrameworkElement)window.Content;
        var size = new Size(1180, 760);
        visual.Measure(size);
        visual.Arrange(new Rect(size));
        visual.UpdateLayout();

        var bitmap = new RenderTargetBitmap(1180, 760, 96, 96, PixelFormats.Pbgra32);
        bitmap.Render(visual);
        var encoder = new PngBitmapEncoder();
        encoder.Frames.Add(BitmapFrame.Create(bitmap));
        Directory.CreateDirectory(Path.GetDirectoryName(outputPath)!);
        using var stream = File.Create(outputPath);
        encoder.Save(stream);
        Console.WriteLine($"Rendered {outputPath}");
    }
}
