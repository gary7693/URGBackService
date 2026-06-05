using URGBackService.Models;
using URGBackService.Processing;
using URGBackService.Services;

namespace URGBackService;

public class Worker : BackgroundService
{
    private readonly ILogger<Worker> _logger;
    private readonly ILoggerFactory _loggerFactory;
    private readonly IConfiguration _config;

    public Worker(ILogger<Worker> logger, ILoggerFactory loggerFactory, IConfiguration config)
    {
        _logger = logger;
        _loggerFactory = loggerFactory;
        _config = config;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var mqttBroker = _config["Mqtt:Broker"]!;
        var mqttPort = int.Parse(_config["Mqtt:Port"]!);
        var clusterGap = double.Parse(_config["Detection:ClusterGap"]!);
        var minPoints = int.Parse(_config["Detection:MinPoints"]!);
        var maxWidth = double.Parse(_config["Detection:MaxObjectWidth"]!);
        var fgThreshold = double.Parse(_config["Detection:ForegroundThreshold"]!);
        var warmupFrames = int.Parse(_config["Detection:WarmupFrames"]!);
        var publishFps    = double.Parse(_config["Detection:PublishFps"]!);
        var publishRawFps = double.Parse(_config["Detection:PublishRawFps"]!);

        var devices = _config.GetSection("Devices").Get<List<DeviceConfig>>();
        if (devices is null || devices.Count == 0)
        {
            _logger.LogError("appsettings.json 中未設定任何 Devices，服務停止");
            return;
        }

        await using var mqttService = new MqttPublisherService(
            _loggerFactory.CreateLogger<MqttPublisherService>(),
            mqttBroker, mqttPort);

        await mqttService.ConnectAsync(stoppingToken);

        mqttService.OnReconnected = async ct =>
        {
            _logger.LogInformation("重新發布所有裝置設定...");
            foreach (var device in devices)
                await mqttService.PublishConfigAsync(device, $"Urg/Config/{device.Name}", ct);
        };

        _logger.LogInformation("啟動 {Count} 台 LiDAR", devices.Count);

        var tasks = devices.Select(device =>
            RunDeviceAsync(device, mqttService, clusterGap, minPoints, maxWidth, fgThreshold, warmupFrames, publishFps, publishRawFps, stoppingToken));

        await Task.WhenAll(tasks);
    }

    private async Task RunDeviceAsync(
        DeviceConfig device,
        MqttPublisherService mqtt,
        double clusterGap, int minPoints, double maxWidth, double fgThreshold, int warmupFrames,
        double publishFps, double publishRawFps,
        CancellationToken ct)
    {
        var minObjIntervalMs = publishFps    > 0 ? 1000.0 / publishFps    : 0.0;
        var minRawIntervalMs = publishRawFps > 0 ? 1000.0 / publishRawFps : 0.0;

        while (!ct.IsCancellationRequested)
        {
            // 每次重連都重建偵測器，讓背景重新暖機
            var detector = new ObjectDetector(1080, clusterGap, minPoints, maxWidth, fgThreshold, warmupFrames);
            var lastObjPublishTime = DateTime.MinValue;
            var lastRawPublishTime = DateTime.MinValue;

            using var urgService = new UrgTcpService(
                _loggerFactory.CreateLogger<UrgTcpService>(),
                device.Host, device.Port,
                device.IncludeAngleMin, device.IncludeAngleMax,
                device.RegionXMin, device.RegionXMax,
                device.RegionYMin, device.RegionYMax,
                device.RotationDeg, device.MirrorX,
                device.ExcludeRegions);

            try
            {
                await urgService.ConnectAsync(ct);
                await mqtt.PublishConfigAsync(device, $"Urg/Config/{device.Name}", ct);
                await foreach (var points in urgService.StreamScansAsync(ct))
                {
                    var objects = detector.Detect(points);
                    if (objects is null)
                    {
                        _logger.LogInformation("[{Name}] 背景建立中...", device.Name);
                        continue;
                    }

                    var now = DateTime.UtcNow;

                    if ((now - lastRawPublishTime).TotalMilliseconds >= minRawIntervalMs)
                    {
                        lastRawPublishTime = now;
                        await mqtt.PublishRawAsync(points, device.TopicRaw, ct);
                    }

                    if (objects.Count > 0 && (now - lastObjPublishTime).TotalMilliseconds >= minObjIntervalMs)
                    {
                        lastObjPublishTime = now;
                        _logger.LogInformation("[{Name}] 偵測到 {Count} 個物件", device.Name, objects.Count);
                        await mqtt.PublishObjectsAsync(objects, device.TopicObjects, ct);
                    }
                }
            }
            catch (OperationCanceledException)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[{Name}] 連線中斷，5 秒後重試...", device.Name);
                await Task.Delay(5000, ct);
            }
        }
    }
}
