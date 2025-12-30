using NATS.Client.Core;
using System.Collections.Concurrent;
using Microsoft.Extensions.Logging;
using consumer.Dtos;
using System.Text.Json;
using NATS.Client.JetStream;
using NATS.Client.JetStream.Models;

namespace Consumer.Service;

public class NatsConsumerService : BackgroundService
{
    private readonly NatsConnection _nats;
    private readonly ConcurrentBag<ReceivedMessage> _messages;
    private readonly ILogger<NatsConsumerService> _logger;

    public NatsConsumerService(
        NatsConnection nats,
        ConcurrentBag<ReceivedMessage> messages,
        ILogger<NatsConsumerService> logger)
    {
        _nats = nats;
        _messages = messages;
        _logger = logger;
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        _logger.LogInformation("JetStream Consumer starting");

        var js = new NatsJSContext(_nats);

        // Retry loop: wait for JetStream and stream/consumer to be available.
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                // Ensure stream exists (idempotent). If JetStream isn't enabled, this will throw NoResponders.
                try
                {
                    await js.CreateStreamAsync(new StreamConfig
                    {
                        Name = "ORDERS_STREAM",
                        Subjects = new[] { "test" },
                        Storage = StreamConfigStorage.File
                    }, stoppingToken);
                }
                catch (NATS.Client.Core.NatsNoRespondersException)
                {
                    _logger.LogWarning("JetStream API not available (No responders). Ensure NATS is started with JetStream: 'nats-server -js -p 4222'. Retrying...");
                    await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
                    continue;
                }
                catch (Exception ex)
                {
                    // Stream may already exist or server not fully ready; log and continue.
                    _logger.LogDebug(ex, "CreateStreamAsync warning (may already exist)");
                }

                // Create or load durable consumer
                var consumer = await js.CreateOrUpdateConsumerAsync("ORDERS_STREAM", new ConsumerConfig
                {
                    AckPolicy = ConsumerConfigAckPolicy.Explicit,
                    FilterSubject = "test"
                });

                _logger.LogInformation("JetStream Consumer connected; starting pull consumption");

                // Pull-style consumption (guaranteed delivery)
                await foreach (var msg in consumer.ConsumeAsync<byte[]>(null, null, stoppingToken))
                {
                    try
                    {
                        var message = JsonSerializer.Deserialize<Message>(msg.Data)!;

                        var receivedAt = DateTime.UtcNow;
                        var latency = (receivedAt - message.Timestamp).TotalMilliseconds;

                        _messages.Add(new ReceivedMessage
                        {
                            MessageId = message.Id,
                            Content = message.Content,
                            OriginalTimestamp = message.Timestamp,
                            ReceivedAt = receivedAt,
                            LatencyMs = latency
                        });

                        await msg.AckAsync();
                    }
                    catch (Exception ex)
                    {
                        _logger.LogError(ex, "Error processing message");
                        await msg.NakAsync();
                    }
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Normal shutdown
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Consumer setup or loop failed; retrying shortly");
                try { await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken); } catch { }
            }
        }
    }

}