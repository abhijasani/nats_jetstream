using NATS.Client.Core;
using System.Text.Json;
using System.Linq;
using Producer.Dtos;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;
using System.Collections.Concurrent;
using System.Net.Sockets;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Track messages per node
var nodeStats = new ConcurrentDictionary<string, int>();
builder.Services.AddSingleton(nodeStats);

// Preferred node configuration
var preferredNode = Environment.GetEnvironmentVariable("PREFERRED_NATS_NODE") ?? "nats://192.168.27.203:4222";
var fallbackNodes = Environment.GetEnvironmentVariable("FALLBACK_NATS_NODES") ?? "nats://192.168.27.201:4222,nats://192.168.27.202:4222";
var natsUrl = $"{preferredNode},{fallbackNodes}";

Console.WriteLine($"NATS Preferred Node: {preferredNode}");
Console.WriteLine($"NATS Connection Order: {natsUrl}");

// Store preferred node info for background monitoring
// builder.Services.AddSingleton(new PreferredNodeConfig { PreferredNodeUrl = preferredNode });

builder.Services.AddSingleton(sp =>
{
    var opts = new NatsOpts
    {
        Url = natsUrl,
        Name = "ProducerAPI",
        ConnectTimeout = TimeSpan.FromSeconds(5),
        ReconnectWaitMax = TimeSpan.FromSeconds(1),
        MaxReconnectRetry = -1,  // Infinite retries
        PingInterval = TimeSpan.FromSeconds(5),  // Detect failure faster
        RequestTimeout = TimeSpan.FromSeconds(10),
        NoRandomize = true,  // Connect to servers in order - preferred node first!
    };
    return new NatsConnection(opts);
});

// Background service to monitor and reconnect to preferred node
// builder.Services.AddHostedService<PreferredNodeMonitor>();

var app = builder.Build();

var nats = app.Services.GetRequiredService<NatsConnection>();
var js = new NatsJSContext(nats);

try
{
    // Try to get existing stream and check replicas
    try
    {
        var existingStream = await js.GetStreamAsync("ORDERS_STREAM");
        // Delete if replicas don't match (need 3 for 3-node cluster)
        if (existingStream.Info.Config.NumReplicas != 3)
        {
            Console.WriteLine($"Stream exists with {existingStream.Info.Config.NumReplicas} replica(s). Deleting to recreate with 3 replicas...");
            await js.DeleteStreamAsync("ORDERS_STREAM");
        }
    }
    catch { /* Stream doesn't exist, will create */ }

    await js.CreateStreamAsync(new StreamConfig
    {
        Name = "ORDERS_STREAM",
        Subjects = new[] { "test" },
        Storage = StreamConfigStorage.File,
        NumReplicas = 3  // Replicate across all 3 cluster nodes for high availability
    });
    Console.WriteLine("Stream ORDERS_STREAM created/verified with 3 replicas");
}
catch (Exception ex)
{
    Console.WriteLine($"JetStream stream setup warning: {ex.Message}");
}


if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapPost("/api/publish", async (MessageRequest request, NatsConnection nats, ConcurrentDictionary<string, int> stats) =>
{
    var message = new Message
    {
        Id = Guid.NewGuid().ToString(),
        Content = request.Content ?? string.Empty,
        Timestamp = DateTime.UtcNow
    };
    var payload = JsonSerializer.SerializeToUtf8Bytes(message);

    var js = new NatsJSContext(nats);

    // Retry logic for failover - give more time for leader election
    var maxRetries = 10;
    var retryDelay = TimeSpan.FromSeconds(3);

    for (int attempt = 1; attempt <= maxRetries; attempt++)
    {
        try
        {
            // Check if connected, if not wait for reconnection
            if (nats.ConnectionState != NatsConnectionState.Open)
            {
                Console.WriteLine($"Attempt {attempt}: Waiting for NATS reconnection...");
                await Task.Delay(retryDelay);
                continue;
            }

            await js.PublishAsync("test", payload);

            // Track which node handled this publish
            var serverName = nats.ServerInfo?.Name ?? "unknown";
            stats.AddOrUpdate(serverName, 1, (key, count) => count + 1);

            return Results.Ok(new
            {
                Success = true,
                MessageId = message.Id,
                Message = "Message published successfully",
                SentViaNode = serverName,
                Attempt = attempt
            });
        }
        catch (Exception ex) when (attempt < maxRetries)
        {
            Console.WriteLine($"Publish attempt {attempt} failed: {ex.Message}. Waiting {retryDelay.TotalSeconds}s for failover...");
            await Task.Delay(retryDelay);
        }
        catch (Exception ex)
        {
            return Results.Problem($"Error publishing message after {maxRetries} attempts: {ex.Message}");
        }
    }

    return Results.Problem("Failed to publish message");
});

app.MapPost("/api/publish-batch", async (int count, NatsConnection nats, ConcurrentDictionary<string, int> stats) =>
{
    var messages = new List<(Message msg, byte[] payload)>();

    for (int i = 0; i < count; i++)
    {
        var baseText = "Lorem ipsum dolor sit amet, consectetur adipiscing elit. Sed do eiusmod tempor incididunt ut labore et dolore magna aliqua. Ut enim ad minim veniam, quis nostrud exercitation ullamco laboris nisi ut aliquip ex ea commodo consequat. Duis aute irure dolor in reprehenderit in voluptate velit esse cillum dolore eu fugiat nulla pariatur. Excepteur sint occaecat cupidatat non proident, sunt in culpa qui officia deserunt mollit anim id est laborum. ";
        var padding = string.Concat(Enumerable.Repeat(baseText, 9)); // ~4KB of text
        var message = new Message
        {
            Id = Guid.NewGuid().ToString(),
            Content = $"Batch message {i + 1} - {padding}",
            Timestamp = DateTime.UtcNow
        };
        var payload = JsonSerializer.SerializeToUtf8Bytes(message);
        messages.Add((message, payload));
    }

    // Retry logic for failover - give more time for leader election
    var maxRetries = 10;
    var retryDelay = TimeSpan.FromSeconds(3);

    for (int attempt = 1; attempt <= maxRetries; attempt++)
    {
        try
        {
            // Check if connected, if not wait for reconnection
            if (nats.ConnectionState != NatsConnectionState.Open)
            {
                Console.WriteLine($"Batch attempt {attempt}: Waiting for NATS reconnection...");
                await Task.Delay(retryDelay);
                continue;
            }

            var tasks = new List<Task>();
            foreach (var (msg, payload) in messages)
            {
                var publishTask = js.PublishAsync("test", payload);
                tasks.Add(publishTask.AsTask());
            }

            await Task.WhenAll(tasks);

            // Track which node handled this batch
            var serverName = nats.ServerInfo?.Name ?? "unknown";
            stats.AddOrUpdate(serverName, count, (key, existing) => existing + count);

            return Results.Ok(new
            {
                Success = true,
                Count = count,
                SentViaNode = serverName,
                Attempt = attempt
            });
        }
        catch (Exception ex) when (attempt < maxRetries)
        {
            Console.WriteLine($"Batch publish attempt {attempt} failed: {ex.Message}. Waiting {retryDelay.TotalSeconds}s for failover...");
            await Task.Delay(retryDelay);
        }
        catch (Exception ex)
        {
            return Results.Problem($"Error publishing batch after {maxRetries} attempts: {ex.Message}");
        }
    }

    return Results.Problem("Failed to publish batch");
});

app.MapGet("/health", () => Results.Ok(new { Status = "Healthy", Service = "Producer API" }));

// Node statistics - shows messages sent through each node
app.MapGet("/api/node-stats", (ConcurrentDictionary<string, int> stats, NatsConnection nats) =>
{
    return Results.Ok(new
    {
        CurrentlyConnectedTo = nats.ServerInfo?.Name ?? "unknown",
        MessagesSentPerNode = stats.ToDictionary(x => x.Key, x => x.Value),
        TotalMessages = stats.Values.Sum()
    });
});

// Reset node stats
app.MapDelete("/api/node-stats", (ConcurrentDictionary<string, int> stats) =>
{
    stats.Clear();
    return Results.Ok(new { Message = "Node stats cleared" });
});

// Cluster info endpoint to monitor which node is being used
app.MapGet("/api/cluster-info", async (NatsConnection nats) =>
{
    try
    {
        var js = new NatsJSContext(nats);
        var stream = await js.GetStreamAsync("ORDERS_STREAM");
        var info = stream.Info;

        return Results.Ok(new
        {
            ConnectedServer = nats.ServerInfo?.Host,
            ConnectedPort = nats.ServerInfo?.Port,
            ClusterId = nats.ServerInfo?.Cluster,
            StreamInfo = new
            {
                Name = info.Config.Name,
                Messages = info.State.Messages,
                Bytes = info.State.Bytes,
                Replicas = info.Config.NumReplicas,
                Leader = info.Cluster?.Leader,
                ClusterReplicas = info.Cluster?.Replicas?.Select(r => new { r.Name, r.Current, r.Active })
            }
        });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Error: {ex.Message}");
    }
});

app.UseHttpsRedirection();

app.Run();

// Configuration class for preferred node
public class PreferredNodeConfig
{
    public string PreferredNodeUrl { get; set; } = string.Empty;
}

// Background service to monitor and reconnect to preferred node
public class PreferredNodeMonitor : BackgroundService
{
    private readonly NatsConnection _nats;
    private readonly PreferredNodeConfig _config;
    private readonly ILogger<PreferredNodeMonitor> _logger;

    public PreferredNodeMonitor(NatsConnection nats, PreferredNodeConfig config, ILogger<PreferredNodeMonitor> logger)
    {
        _nats = nats;
        _config = config;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        // Extract host and port from preferred node URL
        var uri = new Uri(_config.PreferredNodeUrl);
        var preferredHost = uri.Host;
        var preferredPort = uri.Port;

        _logger.LogInformation("Preferred Node Monitor started. Preferred: {Host}:{Port}", preferredHost, preferredPort);

        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                await Task.Delay(TimeSpan.FromSeconds(30), stoppingToken);

                // Check if currently connected to preferred node
                var currentHost = _nats.ServerInfo?.Host;
                var currentName = _nats.ServerInfo?.Name;

                // If not connected to preferred node, check if preferred is available
                if (currentHost != preferredHost && currentHost != "0.0.0.0")
                {
                    // Try to connect to preferred node
                    if (await IsNodeAvailable(preferredHost, preferredPort))
                    {
                        _logger.LogInformation("Preferred node {Host}:{Port} is available. Reconnecting...", preferredHost, preferredPort);

                        // Force reconnection - the client will try preferred node first
                        await _nats.ReconnectAsync();

                        _logger.LogInformation("Reconnected. Now connected to: {Name}", _nats.ServerInfo?.Name);
                    }
                }
                else
                {
                    _logger.LogDebug("Already connected to preferred node: {Name}", currentName);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Error in preferred node monitor");
            }
        }
    }

    private async Task<bool> IsNodeAvailable(string host, int port)
    {
        try
        {
            using var client = new TcpClient();
            var connectTask = client.ConnectAsync(host, port);
            var timeoutTask = Task.Delay(TimeSpan.FromSeconds(2));

            var completedTask = await Task.WhenAny(connectTask, timeoutTask);
            return completedTask == connectTask && client.Connected;
        }
        catch
        {
            return false;
        }
    }
}