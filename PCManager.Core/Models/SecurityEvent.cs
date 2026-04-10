namespace PCManager.Core.Models;

public class SecurityEvent
{
    public int Id { get; set; }
    public DateTime Timestamp { get; set; }
    public string EventType { get; set; } = string.Empty;
    public string Description { get; set; } = string.Empty;
    public string SourceIp { get; set; } = string.Empty;
    public string SeverityContext { get; set; } = string.Empty;
    public string ActionTaken { get; set; } = string.Empty;
    public string DetailsJson { get; set; } = "{}";
}
