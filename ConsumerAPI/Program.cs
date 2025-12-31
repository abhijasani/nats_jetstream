using NATS.Client.Core;
using System.Collections.Concurrent;
using System.Text.Json;
using Consumer.Service;
using consumer.Dtos;
using NATS.Client.JetStream;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var receivedMessages = new ConcurrentBag<ReceivedMessage>();

builder.Services.AddSingleton(sp =>
{
    // PREFERRED node first, then fallback nodes
    var preferredNode = Environment.GetEnvironmentVariable("PREFERRED_NATS_NODE") ?? "nats://192.168.27.203:4222";
    var fallbackNodes = Environment.GetEnvironmentVariable("FALLBACK_NATS_NODES") ?? "nats://192.168.27.201:4222,nats://192.168.27.202:4222";

    var natsUrl = $"{preferredNode},{fallbackNodes}";
    Console.WriteLine($"NATS Connection Order: {natsUrl}");

    var opts = new NatsOpts
    {
        Url = natsUrl,
        Name = "ConsumerAPI",
        ConnectTimeout = TimeSpan.FromSeconds(5),
        ReconnectWaitMax = TimeSpan.FromSeconds(1),
        MaxReconnectRetry = -1,
        PingInterval = TimeSpan.FromSeconds(5),
        RequestTimeout = TimeSpan.FromSeconds(10),
    };
    return new NatsConnection(opts);
});

builder.Services.AddSingleton(receivedMessages);
builder.Services.AddHostedService<NatsConsumerService>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapGet("/api/messages", (ConcurrentBag<ReceivedMessage> messages) =>
{
    return Results.Ok(new
    {
        Count = messages.Count
    });
});

app.MapGet("/api/stats", (ConcurrentBag<ReceivedMessage> messages) =>
{
    var now = DateTime.UtcNow;
    var lastMinute = messages.Count(m => (now - m.ReceivedAt).TotalMinutes < 1);
    var last5Minutes = messages.Count(m => (now - m.ReceivedAt).TotalMinutes < 5);

    return Results.Ok(new
    {
        TotalMessages = messages.Count,
        LastMinute = lastMinute,
        Last5Minutes = last5Minutes,
        MessagesPerSecond = lastMinute / 60.0,
        AverageLatencyMs = messages.Any() ? messages.Average(m => m.LatencyMs) : 0
    });
});

app.MapDelete("/api/messages", (ConcurrentBag<ReceivedMessage> messages) =>
{
    messages.Clear();
    return Results.Ok(new { Message = "All messages cleared" });
});

app.MapGet("/health", () => Results.Ok(new { Status = "Healthy", Service = "Consumer API" }));

app.MapGet("/api/cluster-info", async (NatsConnection nats) =>
{
    try
    {
        var js = new NatsJSContext(nats);
        var stream = await js.GetStreamAsync("ORDERS_STREAM");
        var info = stream.Info;

        return Results.Ok(new
        {
            ConnectedToNode = nats.ServerInfo?.Name,
            ConnectedServer = nats.ServerInfo?.Host,
            ConnectedPort = nats.ServerInfo?.Port,
            ConnectionState = nats.ConnectionState.ToString(),
            ClusterName = nats.ServerInfo?.Cluster,
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