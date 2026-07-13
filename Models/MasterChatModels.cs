namespace FlightReadinessEngine.Api.Models;

/// <summary>
/// Request for the unified master chatbot layer.
/// A single natural-language question is reasoned over the entire
/// consolidated fleet_readiness_snapshot master table.
/// </summary>
public class MasterChatRequest
{
    public string UserQuery { get; set; } = string.Empty;

    /// <summary>Optional prior turn context for multi-turn conversations.</summary>
    public string? Context { get; set; }
}

/// <summary>
/// Rich, chatbot-friendly response grounded on the master fleet data.
/// </summary>
public class MasterChatResponse
{
    public bool Success { get; set; }
    public string Query { get; set; } = string.Empty;

    /// <summary>Domain the classifier routed the question to (e.g. crew_agent).</summary>
    public string Domain { get; set; } = string.Empty;

    public string Intent { get; set; } = string.Empty;
    public string Confidence { get; set; } = string.Empty;

    /// <summary>The natural-language chatbot answer shown to the user.</summary>
    public string Answer { get; set; } = string.Empty;

    /// <summary>Short, bullet-style facts extracted from the data.</summary>
    public List<string> KeyFindings { get; set; } = new();

    /// <summary>Structured list of the flights/tails involved in the answer.</summary>
    public List<AffectedFlight> AffectedFlights { get; set; } = new();

    public List<ActionRecommendation> Recommendations { get; set; } = new();

    /// <summary>How the routing / filtering decision was made.</summary>
    public string Explainability { get; set; } = string.Empty;

    /// <summary>Number of master rows scanned to produce the answer.</summary>
    public int RowsScanned { get; set; }

    public List<string> ExtractedEntities { get; set; } = new();

    public ErrorDetails? Error { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// A single flight/tail surfaced by the chatbot answer.
/// </summary>
public class AffectedFlight
{
    public string Tail { get; set; } = string.Empty;
    public string FlightNumber { get; set; } = string.Empty;
    public string Domain { get; set; } = string.Empty;
    public string Status { get; set; } = string.Empty;
    public string Issue { get; set; } = string.Empty;
}
