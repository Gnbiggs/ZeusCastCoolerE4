namespace ZeusCastEnhanced.Models;

public sealed record SensorReading(string Id, string Name, float? Value)
{
    public override string ToString() => Name;
}
