using NATS.Client.Core;
using System.Collections.Concurrent;
using System.Text.Json;
using Consumer.Service;
using consumer.Dtos;

var builder = WebApplication.CreateBuilder(args);

// Add services to the container.
// Learn more about configuring OpenAPI at https://aka.ms/aspnet/openapi

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

var receivedMessages = new ConcurrentBag<ReceivedMessage>();

builder.Services.AddSingleton(sp =>
{
    var natsUrl = Environment.GetEnvironmentVariable("NATS_URL") ?? "nats://localhost:4222";
    var opts = NatsOpts.Default with { Url = natsUrl };
    return new NatsConnection(opts);
});

builder.Services.AddSingleton(receivedMessages);

// Background service to consume messages
builder.Services.AddHostedService<NatsConsumerService>();

var app = builder.Build();

if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

// Get all received messages
app.MapGet("/api/messages", (ConcurrentBag<ReceivedMessage> messages) =>
{
    return Results.Ok(new
    {
        Count = messages.Count,
        // Messages = messages.OrderByDescending(m => m.ReceivedAt).Take(100)
    });
});

// Get message statistics
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

// Clear messages
app.MapDelete("/api/messages", (ConcurrentBag<ReceivedMessage> messages) =>
{
    messages.Clear();
    return Results.Ok(new { Message = "All messages cleared" });
});

// Health check
app.MapGet("/health", () => Results.Ok(new { Status = "Healthy", Service = "Consumer API" }));

app.UseHttpsRedirection();

app.Run();

