using System.Net.Sockets;
using System.Text;
using URGBackService.Models;

namespace URGBackService.Services;

/// <summary>
/// 透過 TCP 連線 URG LiDAR (SCIP 2.2)，使用 GD 命令持續取得掃描資料。
/// UST-20LX / UST-10LX 不支援 MD 連續命令，改用 GD 迴圈。
/// </summary>
public class UrgTcpService : IDisposable
{
    private readonly ILogger<UrgTcpService> _logger;
    private readonly string _host;
    private readonly int _port;

    // UST-20LX / UST-10LX 掃描參數
    private const int StartStep = 0;
    private const int EndStep = 1079;
    private const double StepAngleDeg = 0.25;                         // 度/step
    private const double StepAngle = StepAngleDeg * Math.PI / 180.0; // rad/step
    private const double FrontStep = 540.0;
    private const double MinDistance = 0.06;
    private const int ReadTimeoutMs = 2000; // 裝置無回應超時，觸發重連

    // 包含角度範圍（step 索引）
    private readonly int _includeStepMin;
    private readonly int _includeStepMax;

    // 包含區域範圍（AABB，公尺）
    private readonly double _regionXMin;
    private readonly double _regionXMax;
    private readonly double _regionYMin;
    private readonly double _regionYMax;

    // 排除區域（旋轉後套用）
    private readonly (double xMin, double xMax, double yMin, double yMax)[] _excludeRegions;

    // 左右鏡射（套用於旋轉前）
    private readonly bool _mirrorX;

    // 旋轉矩陣（正值 = 逆時針，套用於鏡射後）
    private readonly double _rotCos;
    private readonly double _rotSin;

    private readonly string _gdCmd;

    private TcpClient? _client;
    private NetworkStream? _stream;

    public UrgTcpService(ILogger<UrgTcpService> logger, string host, int port,
        double angleMinDeg = -135.0, double angleMaxDeg = 135.0,
        double regionXMin = -999, double regionXMax = 999,
        double regionYMin = -999, double regionYMax = 999,
        double rotationDeg = 0.0,
        bool mirrorX = false,
        IEnumerable<Models.ExcludeRegion>? excludeRegions = null)
    {
        _logger = logger;
        _host = host;
        _port = port;
        _gdCmd = $"GD{StartStep:D4}{EndStep:D4}01\n";
        _mirrorX = mirrorX;
        _excludeRegions = (excludeRegions ?? [])
            .Select(r => (r.XMin, r.XMax, r.YMin, r.YMax))
            .ToArray();
        var rotRad = rotationDeg * Math.PI / 180.0;
        _rotCos = Math.Cos(rotRad);
        _rotSin = Math.Sin(rotRad);

        // 角度（相對正前方）→ step 索引
        _includeStepMin = Math.Clamp(
            (int)Math.Round(FrontStep + angleMinDeg / StepAngleDeg), StartStep, EndStep);
        _includeStepMax = Math.Clamp(
            (int)Math.Round(FrontStep + angleMaxDeg / StepAngleDeg), StartStep, EndStep);

        _regionXMin = regionXMin;
        _regionXMax = regionXMax;
        _regionYMin = regionYMin;
        _regionYMax = regionYMax;

        _logger.LogInformation("包含角度範圍: {AMin}°~{AMax}° (step {SMin}~{SMax})",
            angleMinDeg, angleMaxDeg, _includeStepMin, _includeStepMax);
        _logger.LogInformation("包含區域範圍: X[{XMin}~{XMax}] Y[{YMin}~{YMax}] m",
            regionXMin, regionXMax, regionYMin, regionYMax);
        if (_excludeRegions.Length > 0)
            _logger.LogInformation("排除區域: {Count} 個", _excludeRegions.Length);
        if (mirrorX)
            _logger.LogInformation("左右鏡射: 啟用");
        if (rotationDeg != 0)
            _logger.LogInformation("旋轉角度: {Deg}°", rotationDeg);
    }

    public async Task ConnectAsync(CancellationToken ct)
    {
        _client = new TcpClient();
        await _client.ConnectAsync(_host, _port, ct);
        _stream = _client.GetStream();
        _logger.LogInformation("已連線 URG {Host}:{Port}", _host, _port);

        // 停止先前掃描
        await SendCommandAsync("QT\n", ct);
        await ReadResponseAsync(ct);

        // 裝置已為 SCIP 2.2，此命令可能回傳錯誤，忽略即可
        await SendCommandAsync("SCIP2.0\n", ct);
        await ReadResponseAsync(ct);

        // 開啟雷射
        await SendCommandAsync("BM\n", ct);
        string bmResp = await ReadResponseAsync(ct);
        _logger.LogInformation("BM 回應: {R}", bmResp.Replace("\n", "\\n").Trim());
    }

    /// <summary>
    /// 以 GD 迴圈持續 yield 每一幀點雲（UST 不支援 MD 連續模式）。
    /// </summary>
    public async IAsyncEnumerable<List<ScanPoint>> StreamScansAsync(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct)
    {
        int frameCount = 0;
        while (!ct.IsCancellationRequested)
        {
            await SendCommandAsync(_gdCmd, ct);
            string response = await ReadResponseAsync(ct);
            frameCount++;

            var points = ParseScanResponse(response);
            if (points is not null)
            {
                if (frameCount == 1 || frameCount % 500 == 0)
                    _logger.LogInformation("Frame#{N} 有效點數: {Count}", frameCount, points.Count);
                yield return points;
            }
            else if (frameCount <= 5)
            {
                var lines = response.Split('\n');
                _logger.LogWarning("Frame#{N} 解析失敗 status={S}",
                    frameCount, lines.Length > 1 ? lines[1] : "?");
            }
        }
    }

    private async Task SendCommandAsync(string command, CancellationToken ct)
    {
        byte[] data = Encoding.ASCII.GetBytes(command);
        await _stream!.WriteAsync(data, ct);
    }

    private async Task<string> ReadResponseAsync(CancellationToken ct)
    {
        var sb = new StringBuilder();
        byte[] buffer = new byte[8192];

        while (!ct.IsCancellationRequested)
        {
            using var readCts = CancellationTokenSource.CreateLinkedTokenSource(ct);
            readCts.CancelAfter(ReadTimeoutMs);

            int n;
            try
            {
                n = await _stream!.ReadAsync(buffer, readCts.Token);
            }
            catch (OperationCanceledException) when (!ct.IsCancellationRequested)
            {
                throw new IOException($"TCP 讀取逾時（>{ReadTimeoutMs}ms），裝置可能已斷線");
            }

            if (n == 0) throw new IOException("TCP 連線已關閉");
            sb.Append(Encoding.ASCII.GetString(buffer, 0, n));
            if (sb.ToString().Contains("\n\n"))
                break;
        }
        return sb.ToString();
    }

    /// <summary>
    /// 解析 GD / MD 回應，轉換為 XY 座標（公尺，Y-up 正前方）。
    /// </summary>
    private List<ScanPoint>? ParseScanResponse(string response)
    {
        try
        {
            var lines = response.Split('\n', StringSplitOptions.RemoveEmptyEntries);

            // 至少需要：命令行、狀態行、時間戳行、資料行
            if (lines.Length < 4) return null;

            // 狀態 "00" = 正常（GD），"99" = 連續掃描資料（MD）
            string status = lines[1];
            if (!status.StartsWith("00") && !status.StartsWith("99"))
                return null;

            // lines[2] = 時間戳，lines[3..] = 資料（每行末尾是 checksum，需去除）
            var rawData = string.Concat(lines.Skip(3).Select(l =>
                l.Length > 0 ? l[..^1] : string.Empty));

            var distances = Decode3Char(rawData);
            var points = new List<ScanPoint>(distances.Count);

            for (int i = 0; i < distances.Count; i++)
            {
                int stepIndex = StartStep + i;

                // 只處理包含角度範圍內的步數
                if (stepIndex < _includeStepMin || stepIndex > _includeStepMax) continue;

                double dist = distances[i] / 1000.0;
                if (dist < MinDistance) continue;

                double angle = (stepIndex - FrontStep) * StepAngle;
                double rawX = dist * Math.Sin(angle);
                double rawY = dist * Math.Cos(angle);

                // 左右鏡射（先於旋轉）
                if (_mirrorX) rawX = -rawX;

                // 旋轉矩陣（正值 = 逆時針）
                double px = Math.Round(rawX * _rotCos - rawY * _rotSin, 4);
                double py = Math.Round(rawX * _rotSin + rawY * _rotCos, 4);

                // Include AABB 過濾
                if (px < _regionXMin || px > _regionXMax ||
                    py < _regionYMin || py > _regionYMax) continue;

                // Exclude regions 過濾
                bool excluded = false;
                foreach (var (xMin, xMax, yMin, yMax) in _excludeRegions)
                {
                    if (px >= xMin && px <= xMax && py >= yMin && py <= yMax)
                    {
                        excluded = true;
                        break;
                    }
                }
                if (excluded) continue;

                points.Add(new ScanPoint(stepIndex, px, py, Math.Round(dist, 4)));
            }

            return points;
        }
        catch (Exception ex)
        {
            _logger.LogWarning(ex, "掃描資料解析失敗");
            return null;
        }
    }

    private static List<int> Decode3Char(string data)
    {
        var result = new List<int>(data.Length / 3);
        for (int i = 0; i + 2 < data.Length; i += 3)
        {
            result.Add(((data[i] - 0x30) << 12)
                     | ((data[i + 1] - 0x30) << 6)
                     | (data[i + 2] - 0x30));
        }
        return result;
    }

    public void Dispose()
    {
        _stream?.Dispose();
        _client?.Dispose();
    }
}
