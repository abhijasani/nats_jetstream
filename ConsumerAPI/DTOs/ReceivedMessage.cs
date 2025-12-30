namespace consumer.Dtos;

public record ReceivedMessage
{
    public string MessageId { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public DateTime OriginalTimestamp { get; set; }
    public DateTime ReceivedAt { get; set; }
    public double LatencyMs { get; set; }
}