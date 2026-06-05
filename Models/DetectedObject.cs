namespace URGBackService.Models;

public class DetectedObject
{
    public int Id { get; set; }
    public double CenterX { get; set; }
    public double CenterY { get; set; }
    public double Width { get; set; }
    public double Distance { get; set; }   // 距離原點 (m)
    public int PointCount { get; set; }
    public List<ScanPoint> Points { get; set; } = [];
}
