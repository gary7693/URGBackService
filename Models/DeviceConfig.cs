namespace URGBackService.Models;

public class DeviceConfig
{
    public string Name { get; set; } = string.Empty;
    public string Host { get; set; } = string.Empty;
    public int Port { get; set; } = 10940;
    public double IncludeAngleMin { get; set; } = -135.0;
    public double IncludeAngleMax { get; set; } = 135.0;
    public double RegionXMin { get; set; } = -999.0;
    public double RegionXMax { get; set; } = 999.0;
    public double RegionYMin { get; set; } = -999.0;
    public double RegionYMax { get; set; } = 999.0;
    public List<ExcludeRegion> ExcludeRegions { get; set; } = [];
    public bool MirrorX { get; set; } = false;
    public double RotationDeg { get; set; } = 0.0;
    public string TopicObjects { get; set; } = string.Empty;
    public string TopicRaw { get; set; } = string.Empty;
}
