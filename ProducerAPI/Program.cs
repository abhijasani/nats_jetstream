using NATS.Client.Core;
using System.Text.Json;
using System.Linq;
using consumer.Dtos;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;

var builder = WebApplication.CreateBuilder(args);

builder.Services.AddEndpointsApiExplorer();
builder.Services.AddSwaggerGen();

// Register NATS connection as singleton
builder.Services.AddSingleton(sp =>
{
    var natsUrl = Environment.GetEnvironmentVariable("NATS_URL") ?? "nats://localhost:4222";
    var opts = NatsOpts.Default with { Url = natsUrl };
    return new NatsConnection(opts);
});

var app = builder.Build();

var nats = app.Services.GetRequiredService<NatsConnection>();
var js = new NatsJSContext(nats);

try
{
    await js.CreateStreamAsync(new StreamConfig
    {
        Name = "ORDERS_STREAM",
        Subjects = new[] { "test" },
        Storage = StreamConfigStorage.File
    });
}
catch (Exception ex)
{
    Console.WriteLine($"JetStream stream setup warning: {ex.Message}");
    // If the stream already exists or server isn't ready yet, continue starting the app
}


// Configure the HTTP request pipeline.
if (app.Environment.IsDevelopment())
{
    app.UseSwagger();
    app.UseSwaggerUI();
}

app.MapPost("/api/publish", async (MessageRequest request, NatsConnection nats) =>
{
    try
    {
        var message = new Message
        {
            Id = Guid.NewGuid().ToString(),
            Content = request.Content ?? string.Empty,
            Timestamp = DateTime.UtcNow
        };
        var payload = JsonSerializer.SerializeToUtf8Bytes(message);

        // await nats.PublishAsync(subject: "orders.created", data: message);
        // await nats.PublishAsync("orders.created", payload).AsTask();
        var js = new NatsJSContext(nats);
        await js.PublishAsync("test", payload);

        return Results.Ok(new
        {
            Success = true,
            MessageId = message.Id,
            Message = "Message published successfully"
        });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Error publishing message: {ex.Message}");
    }
});

// Publish batch messages for testing
app.MapPost("/api/publish-batch", async (int count, NatsConnection nats) =>
{
    try
    {
        var tasks = new List<Task>();
        var messageIds = new List<string>();

        for (int i = 0; i < count; i++)
        {
            // Generate ~4KB content with meaningful text
            var baseText = "Lorem ipsum dolor sit amet, consectetur adipiscing elit. Sed do eiusmod tempor incididunt ut labore et dolore magna aliqua. Ut enim ad minim veniam, quis nostrud exercitation ullamco laboris nisi ut aliquip ex ea commodo consequat. Duis aute irure dolor in reprehenderit in voluptate velit esse cillum dolore eu fugiat nulla pariatur. Excepteur sint occaecat cupidatat non proident, sunt in culpa qui officia deserunt mollit anim id est laborum. ";
            var padding = string.Concat(Enumerable.Repeat(baseText, 9)); // ~4KB of text
            var message = new Message
            {
                Id = Guid.NewGuid().ToString(),
                Content = $"Batch message {i + 1} - {padding}",
                Timestamp = DateTime.UtcNow
            };
            var payload = JsonSerializer.SerializeToUtf8Bytes(message);
            messageIds.Add(message.Id);

            // ✔ Publish raw bytes

            var publishTask = js.PublishAsync("test", payload);
            tasks.Add(publishTask.AsTask());

            // tasks.Add(nats.PublishAsync("orders.created", payload).AsTask());

            // tasks.Add(nats.PublishAsync(subject: "orders.created", data: message).AsTask());
        }

        await Task.WhenAll(tasks);

        return Results.Ok(new
        {
            Success = true,
            Count = count,
            // MessageIds = messageIds,
            // Message = $"{count} messages published successfully"
        });
    }
    catch (Exception ex)
    {
        return Results.Problem($"Error publishing batch: {ex.Message}");
    }
});

// Health check
app.MapGet("/health", () => Results.Ok(new { Status = "Healthy", Service = "Producer API" }));

app.UseHttpsRedirection();

app.Run();

