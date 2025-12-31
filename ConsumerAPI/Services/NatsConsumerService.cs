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


                // Create or load durable consumer - DeliverNew means only new messages
                var consumer = await js.CreateOrUpdateConsumerAsync("ORDERS_STREAM", new ConsumerConfig
                {
                    Name = "orders-consumer",  // Durable consumer name
                    DurableName = "orders-consumer",  // Makes consumer persistent
                    AckPolicy = ConsumerConfigAckPolicy.Explicit,
                    FilterSubject = "test",
                    DeliverPolicy = ConsumerConfigDeliverPolicy.New,  // Only receive NEW messages, not old ones
                });

                _logger.LogInformation("JetStream Consumer connected to {Server} (DeliverPolicy=New); starting pull consumption", _nats.ServerInfo?.Name);

                // Pull-style consumption (guaranteed delivery)
                await foreach (var msg in consumer.ConsumeAsync<byte[]>(null, null, stoppingToken))
                {
                    try
                    {
                        // Check if connection is still healthy
                        if (_nats.ConnectionState != NatsConnectionState.Open)
                        {
                            _logger.LogWarning("Connection lost during consumption. Breaking to reconnect...");
                            break;
                        }

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
                        try { await msg.NakAsync(); } catch { /* Ignore if NAK fails during disconnect */ }
                    }
                }

                _logger.LogInformation("Consume loop exited. Will retry connection...");
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                // Normal shutdown
                break;
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "Consumer setup or loop failed (State: {State}); retrying in 3 seconds...", _nats.ConnectionState);
                try { await Task.Delay(TimeSpan.FromSeconds(3), stoppingToken); } catch { }
            }
        }
    }

}