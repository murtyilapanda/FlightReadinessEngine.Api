namespace FlightReadinessEngine.Api.Models;

/// <summary>
/// Request model for natural language agent query processing
/// </summary>
public class AgentQueryRequest
{
    public string UserQuery { get; set; } = string.Empty;
    public string? Context { get; set; }
    public Dictionary<string, string>? Metadata { get; set; }
}

/// <summary>
/// Response model for agent query processing
/// </summary>
public class AgentQueryResponse
{
    public bool Success { get; set; }
    public string Query { get; set; } = string.Empty;
    public string Intent { get; set; } = string.Empty;
    public string Agent { get; set; } = string.Empty;
    public List<string> ExtractedEntities { get; set; } = new();
    public AgentAnalysis Analysis { get; set; } = new();
    public List<ActionRecommendation> Recommendations { get; set; } = new();
    public ErrorDetails? Error { get; set; }
    public DateTime Timestamp { get; set; } = DateTime.UtcNow;
}

/// <summary>
/// Agent analysis details
/// </summary>
public class AgentAnalysis
{
    public string Summary { get; set; } = string.Empty;
    public object? Data { get; set; }
    public Dictionary<string, object> Insights { get; set; } = new();
    public List<string> AffectedFlights { get; set; } = new();
    public string Confidence { get; set; } = string.Empty;
    public string Explainability { get; set; } = string.Empty;
}

/// <summary>
/// Action recommendation from agent analysis
/// </summary>
public class ActionRecommendation
{
    public string Priority { get; set; } = string.Empty;
    public string Action { get; set; } = string.Empty;
    public string Reason { get; set; } = string.Empty;
    public string Owner { get; set; } = string.Empty;
    public string Area { get; set; } = string.Empty;
}

/// <summary>
/// Error details for failed queries
/// </summary>
public class ErrorDetails
{
    public string Code { get; set; } = string.Empty;
    public string Message { get; set; } = string.Empty;
    public string Details { get; set; } = string.Empty;
}

/// <summary>
/// Intent classification result
/// </summary>
public class AgentIntent
{
    public string AgentType { get; set; } = string.Empty;
    public string IntentCategory { get; set; } = string.Empty;
    public string QueryType { get; set; } = string.Empty;
    public string Confidence { get; set; } = string.Empty;
    public List<ExtractedEntity> Entities { get; set; } = new();
    public Dictionary<string, double> AgentScores { get; set; } = new();
    public string Explainability { get; set; } = string.Empty;
}

/// <summary>
/// Extracted entity from user query
/// </summary>
public class ExtractedEntity
{
    public string Type { get; set; } = string.Empty;
    public string Value { get; set; } = string.Empty;
}

/// <summary>
/// Agent type constants
/// </summary>
public static class AgentTypes
{
    public const string MaintenanceAgent = "maintenance_agent";
    public const string CrewAgent = "crew_agent";
    public const string PartsAgent = "parts_agent";
    public const string GroundAgent = "ground_agent";
    public const string FlightPlanningAgent = "flight_planning_agent";
    public const string AircraftAgent = "aircraft_agent";
    public const string MasterAgent = "master_agent";
    public const string UnknownAgent = "unknown_agent";
}

/// <summary>
/// Agent endpoint path constants
/// </summary>
public static class AgentEndpoints
{
    public const string Crew = "/api/agents/crew";
    public const string Parts = "/api/agents/parts";
    public const string Maintenance = "/api/agents/maintenance";
    public const string Ground = "/api/agents/ground";
    public const string FlightPlanning = "/api/agents/flight-planning";
    public const string Aircraft = "/api/agents/aircraft";
    public const string Master = "/api/agents/master";
    public const string Query = "/api/agents/query";
}
