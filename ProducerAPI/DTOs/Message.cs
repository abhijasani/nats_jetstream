namespace consumer.Dtos;

public record MessageRequest(string Content);

public record Message
{
    public string Id { get; set; } = string.Empty;
    public string Content { get; set; } = string.Empty;
    public DateTime Timestamp { get; set; }
}