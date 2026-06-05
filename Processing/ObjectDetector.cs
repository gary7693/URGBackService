using URGBackService.Models;

namespace URGBackService.Processing;

/// <summary>
/// 背景消去（Background Subtraction）+ 群聚物件偵測器。
/// 啟動時以前 WarmupFrames 幀建立靜態背景模型（每步距的最遠距離）。
/// 後續只偵測比背景明顯更近（ForegroundThreshold）的點，忽略靜態牆壁/家具。
/// </summary>
public class ObjectDetector
{
    private readonly int _totalSteps;
    private readonly double _clusterGap;
    private readonly int _minPoints;
    private readonly double _maxObjectWidth;
    private readonly double _foregroundThreshold; // 比背景近超過此值才算前景 (m)
    private readonly int _warmupFrames;

    private readonly double[] _background;         // 每步距的背景距離
    private readonly double[] _backgroundAccum;    // 累積加總（用於平均）
    private readonly int[] _backgroundCount;
    private int _framesCollected;
    private bool _isReady;

    public bool IsReady => _isReady;

    public ObjectDetector(int totalSteps = 1080, double clusterGap = 0.15,
        int minPoints = 5, double maxObjectWidth = 1.0,
        double foregroundThreshold = 0.3, int warmupFrames = 30)
    {
        _totalSteps = totalSteps;
        _clusterGap = clusterGap;
        _minPoints = minPoints;
        _maxObjectWidth = maxObjectWidth;
        _foregroundThreshold = foregroundThreshold;
        _warmupFrames = warmupFrames;

        _background = new double[totalSteps];
        _backgroundAccum = new double[totalSteps];
        _backgroundCount = new int[totalSteps];
        Array.Fill(_background, double.MaxValue);
    }

    /// <summary>
    /// 以目前幀更新背景模型或執行物件偵測。
    /// 回傳 null 表示仍在暖機（建立背景）中。
    /// </summary>
    public List<DetectedObject>? Detect(List<ScanPoint> points)
    {
        if (!_isReady)
        {
            // 暖機：累積每步距的距離，取平均作為背景
            foreach (var p in points)
            {
                if (p.StepIndex < _totalSteps)
                {
                    _backgroundAccum[p.StepIndex] += p.Distance;
                    _backgroundCount[p.StepIndex]++;
                }
            }

            _framesCollected++;
            if (_framesCollected >= _warmupFrames)
            {
                for (int i = 0; i < _totalSteps; i++)
                {
                    _background[i] = _backgroundCount[i] > 0
                        ? _backgroundAccum[i] / _backgroundCount[i]
                        : double.MaxValue;
                }
                _isReady = true;
            }
            return null;
        }

        // 前景過濾：只保留比背景明顯更近的點
        var foreground = points
            .Where(p => p.StepIndex < _totalSteps &&
                        _background[p.StepIndex] != double.MaxValue &&
                        p.Distance < _background[p.StepIndex] - _foregroundThreshold)
            .ToList();

        if (foreground.Count == 0) return [];

        // 相鄰點歐氏距離分群
        var clusters = new List<List<ScanPoint>>();
        var current = new List<ScanPoint> { foreground[0] };

        for (int i = 1; i < foreground.Count; i++)
        {
            double dx = foreground[i].X - foreground[i - 1].X;
            double dy = foreground[i].Y - foreground[i - 1].Y;
            if (Math.Sqrt(dx * dx + dy * dy) <= _clusterGap)
                current.Add(foreground[i]);
            else
            {
                clusters.Add(current);
                current = [foreground[i]];
            }
        }
        clusters.Add(current);

        var objects = new List<DetectedObject>();
        int id = 1;

        foreach (var cluster in clusters)
        {
            if (cluster.Count < _minPoints) continue;

            double width = Distance(cluster[0], cluster[^1]);
            if (width > _maxObjectWidth) continue;

            double cx = cluster.Average(p => p.X);
            double cy = cluster.Average(p => p.Y);

            objects.Add(new DetectedObject
            {
                Id = id++,
                CenterX = Math.Round(cx, 3),
                CenterY = Math.Round(cy, 3),
                Width = Math.Round(width, 3),
                Distance = Math.Round(Math.Sqrt(cx * cx + cy * cy), 3),
                PointCount = cluster.Count,
                Points = cluster
            });
        }

        return objects;
    }

    private static double Distance(ScanPoint a, ScanPoint b)
        => Math.Sqrt((a.X - b.X) * (a.X - b.X) + (a.Y - b.Y) * (a.Y - b.Y));
}
