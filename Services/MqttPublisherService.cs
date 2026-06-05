using MQTTnet;
using MQTTnet.Client;
using System.Text;
using System.Text.Json;
using URGBackService.Models;

namespace URGBackService.Services;

public class MqttPublisherService : IAsyncDisposable
{
    private readonly ILogger<MqttPublisherService> _logger;
    private readonly string _broker;
    private readonly int _port;
    private readonly IMqttClient _client;
    private readonly JsonSerializerOptions _jsonOpts = new() { PropertyNamingPolicy = JsonNamingPolicy.CamelCase };
    // 前端期望大寫 X/Y，不套用 CamelCase
    private static readonly JsonSerializerOptions _rawOpts = new();

    private MqttClientOptions? _connectOptions;
    private CancellationToken _ct;

    public MqttPublisherService(ILogger<MqttPublisherService> logger,
        string broker, int port)
    {
        _logger = logger;
        _broker = broker;
        _port = port;

        var factory = new MqttFactory();
        _client = factory.CreateMqttClient();
        _client.DisconnectedAsync += OnDisconnectedAsync;
    }

    private const string StatusTopic = "Urg/Status";

    public async Task ConnectAsync(CancellationToken ct)
    {
        _ct = ct;
        _connectOptions = new MqttClientOptionsBuilder()
            .WithTcpServer(_broker, _port)
            .WithClientId($"URGBackService_{Environment.MachineName}")
            .WithCleanSession()
            .WithWillTopic(StatusTopic)
            .WithWillPayload("offline")
            .WithWillRetain(true)
            .WithWillQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
            .Build();

        await _client.ConnectAsync(_connectOptions, ct);
        await PublishStatusAsync("online", ct);
        _logger.LogInformation("MQTT 已連線 {Broker}:{Port}", _broker, _port);
    }

    public Func<CancellationToken, Task>? OnReconnected { get; set; }

    private async Task OnDisconnectedAsync(MqttClientDisconnectedEventArgs args)
    {
        if (_connectOptions is null || _ct.IsCancellationRequested) return;
        _logger.LogWarning("MQTT 已斷線，5 秒後重試重連...");
        try
        {
            await Task.Delay(5000, _ct);
            await _client.ConnectAsync(_connectOptions, _ct);
            await PublishStatusAsync("online", _ct);
            _logger.LogInformation("MQTT 已重連 {Broker}:{Port}", _broker, _port);
            if (OnReconnected is not null)
                await OnReconnected(_ct);
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            _logger.LogError(ex, "MQTT 重連失敗，等待下次重試");
        }
    }

    private async Task PublishStatusAsync(string status, CancellationToken ct)
    {
        var message = new MqttApplicationMessageBuilder()
            .WithTopic(StatusTopic)
            .WithPayload(status)
            .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtLeastOnce)
            .WithRetainFlag(true)
            .Build();
        await _client.PublishAsync(message, ct);
    }

    public async Task PublishObjectsAsync(List<DetectedObject> objects, string topic, CancellationToken ct)
    {
        // 僅發送摘要（不含原始點雲）；X/Y 大寫與掃描點格式一致
        var payload = objects.Select(o => new
        {
            id         = o.Id,
            X          = o.CenterX,
            Y          = o.CenterY,
            width      = o.Width,
            distance   = o.Distance,
            pointCount = o.PointCount,
        });

        string json = JsonSerializer.Serialize(payload, _rawOpts);
        await PublishJsonAsync(topic, json, ct);
    }

    public async Task PublishRawAsync(List<ScanPoint> points, string topic, CancellationToken ct)
    {
        // 前端 LidarPage 期望 [{X, Y}] 大寫格式
        var payload = points.Select(p => new { X = p.X, Y = p.Y });
        string json = JsonSerializer.Serialize(payload, _rawOpts);
        await PublishJsonAsync(topic, json, ct);
    }

    public async Task PublishConfigAsync(DeviceConfig device, string topic, CancellationToken ct)
    {
        var payload = new
        {
            name             = device.Name,
            angleMin         = device.IncludeAngleMin,
            angleMax         = device.IncludeAngleMax,
            regionXMin       = device.RegionXMin,
            regionXMax       = device.RegionXMax,
            regionYMin       = device.RegionYMin,
            regionYMax       = device.RegionYMax,
            excludeRegions   = device.ExcludeRegions.Select(r => new { r.XMin, r.XMax, r.YMin, r.YMax }),
            mirrorX          = device.MirrorX,
            rotationDeg      = device.RotationDeg,
            topicRaw         = device.TopicRaw,
            topicObjects     = device.TopicObjects,
        };
        string json = JsonSerializer.Serialize(payload, _jsonOpts);
        await PublishJsonAsync(topic, json, ct, retain: true);
    }

    private async Task PublishAsync(string topic, object payload, CancellationToken ct)
    {
        await PublishJsonAsync(topic, JsonSerializer.Serialize(payload, _jsonOpts), ct);
    }

    private async Task PublishJsonAsync(string topic, string json, CancellationToken ct, bool retain = false)
    {
        if (!_client.IsConnected)
        {
            _logger.LogWarning("MQTT 未連線，跳過發送");
            return;
        }

        var message = new MqttApplicationMessageBuilder()
            .WithTopic(topic)
            .WithPayload(Encoding.UTF8.GetBytes(json))
            .WithQualityOfServiceLevel(MQTTnet.Protocol.MqttQualityOfServiceLevel.AtMostOnce)
            .WithRetainFlag(retain)
            .Build();

        await _client.PublishAsync(message, ct);
    }

    public async ValueTask DisposeAsync()
    {
        if (_client.IsConnected)
        {
            try { await PublishStatusAsync("offline", CancellationToken.None); } catch { }
            await _client.DisconnectAsync();
        }
        _client.Dispose();
    }
}
