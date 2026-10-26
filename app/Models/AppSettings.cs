namespace ZeusCastEnhanced.Models;

public sealed class AppSettings
{
    public string DisplayMode { get; set; } = "temperature";
    public bool UseFahrenheit { get; set; }
    public int CustomValue { get; set; } = 2026;
    public bool AlarmEnabled { get; set; }
    public int AlarmThresholdCelsius { get; set; } = 85;
    public int RefreshMilliseconds { get; set; } = 1000;
    public string? TemperatureSensorId { get; set; }
    public string? FanSensorId { get; set; }
    public bool StartWithWindows { get; set; }

    public bool ReplaceOriginalZeusCast { get; set; } = true;
}
