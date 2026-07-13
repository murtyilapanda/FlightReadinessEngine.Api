using System.Text.RegularExpressions;
using FlightReadinessEngine.Api.Models;

namespace FlightReadinessEngine.Api.Services;

/// <summary>
/// Enhanced dynamic intent classifier for agent-based routing
/// Analyzes natural language queries to determine intent, extract entities, and route to appropriate agents
/// </summary>
public interface IAgentIntentClassifier
{
    AgentIntent ClassifyIntent(string userQuery);
}

public class AgentIntentClassifier : IAgentIntentClassifier
{
    private readonly ILogger<AgentIntentClassifier> _logger;

    // Entity extraction patterns
    private static readonly Regex FlightIdPattern = new(@"\b([A-Z]{2,3}[-\s]?\d{2,4})\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex AircraftIdPattern = new(@"\b(aircraft|plane|tail)\s+([A-Z0-9-]+)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex TimePattern = new(@"\b(tomorrow|today|tonight|next\s+\d+\s+hours?|in\s+\d+\s+hours?|\d{1,2}:\d{2}\s*(?:AM|PM)?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);
    private static readonly Regex StatusPattern = new(@"\b(ready|not\s+ready|grounded|delayed|critical|blocked?)\b", RegexOptions.IgnoreCase | RegexOptions.Compiled);

    // Dynamic agent keyword mappings with confidence scores
    private static readonly Dictionary<string, (string Agent, double Weight)> AgentKeywords = new()
    {
        // Maintenance Agent Keywords (highest specificity first)
        ["critical maintenance"] = (AgentTypes.MaintenanceAgent, 1.0),
        ["pending maintenance"] = (AgentTypes.MaintenanceAgent, 1.0),
        ["maintenance status"] = (AgentTypes.MaintenanceAgent, 0.95),
        ["out of service"] = (AgentTypes.MaintenanceAgent, 1.0),
        ["grounded"] = (AgentTypes.AircraftAgent, 0.95),
        ["maintenance"] = (AgentTypes.MaintenanceAgent, 0.85),
        ["engine"] = (AgentTypes.MaintenanceAgent, 0.8),
        ["tire"] = (AgentTypes.MaintenanceAgent, 0.8),
        ["repair"] = (AgentTypes.MaintenanceAgent, 0.85),
        ["inspection"] = (AgentTypes.MaintenanceAgent, 0.8),
        ["apu over"] = (AgentTypes.GroundAgent, 0.9),
        ["apu overrun"] = (AgentTypes.GroundAgent, 0.95),

        // Parts Agent Keywords
        ["parts"] = (AgentTypes.PartsAgent, 0.9),
        ["spare parts"] = (AgentTypes.PartsAgent, 1.0),
        ["inventory"] = (AgentTypes.PartsAgent, 0.85),
        ["part number"] = (AgentTypes.PartsAgent, 0.95),
        ["missing parts"] = (AgentTypes.PartsAgent, 1.0),
        ["parts availability"] = (AgentTypes.PartsAgent, 1.0),

        // Flight Planning Agent Keywords
        ["flight plan"] = (AgentTypes.FlightPlanningAgent, 1.0),
        ["flight planning"] = (AgentTypes.FlightPlanningAgent, 1.0),
        ["not filed"] = (AgentTypes.FlightPlanningAgent, 0.95),
        ["weather"] = (AgentTypes.FlightPlanningAgent, 0.85),
        ["route"] = (AgentTypes.FlightPlanningAgent, 0.8),
        ["atc"] = (AgentTypes.FlightPlanningAgent, 0.85),
        ["clearance"] = (AgentTypes.FlightPlanningAgent, 0.75),
        ["flight path"] = (AgentTypes.FlightPlanningAgent, 0.9),

        // Crew Agent Keywords
        ["crew"] = (AgentTypes.CrewAgent, 0.9),
        ["pilot"] = (AgentTypes.CrewAgent, 0.85),
        ["captain"] = (AgentTypes.CrewAgent, 0.85),
        ["purser"] = (AgentTypes.CrewAgent, 0.85),
        ["assigned crew"] = (AgentTypes.CrewAgent, 0.95),
        ["crew not assigned"] = (AgentTypes.CrewAgent, 1.0),
        ["crew unavailable"] = (AgentTypes.CrewAgent, 1.0),
        ["rest hours"] = (AgentTypes.CrewAgent, 0.95),
        ["fatigue"] = (AgentTypes.CrewAgent, 0.9),
        ["compliant"] = (AgentTypes.CrewAgent, 0.85),
        ["compliance"] = (AgentTypes.CrewAgent, 0.85),

        // Ground Operations Agent Keywords
        ["ground"] = (AgentTypes.GroundAgent, 0.85),
        ["ground operations"] = (AgentTypes.GroundAgent, 0.95),
        ["ground power"] = (AgentTypes.GroundAgent, 0.95),
        ["baggage"] = (AgentTypes.GroundAgent, 0.9),
        ["gate"] = (AgentTypes.GroundAgent, 0.8),
        ["boarding"] = (AgentTypes.GroundAgent, 0.85),
        ["pushback"] = (AgentTypes.GroundAgent, 0.9),
        ["turnaround"] = (AgentTypes.GroundAgent, 0.85),
        ["checkpoint"] = (AgentTypes.GroundAgent, 0.8),

        // Fuel Keywords (can map to Flight Planning Agent)
        ["fuel"] = (AgentTypes.FlightPlanningAgent, 0.85),
        ["fuel ticket"] = (AgentTypes.FlightPlanningAgent, 0.95),
        ["fuel planning"] = (AgentTypes.FlightPlanningAgent, 0.9),
        ["refuel"] = (AgentTypes.FlightPlanningAgent, 0.85),
        ["uplift"] = (AgentTypes.FlightPlanningAgent, 0.85),

        // Aircraft Agent Keywords (includes grounded aircraft)
        ["grounded aircraft"] = (AgentTypes.AircraftAgent, 1.0),
        ["aircraft grounded"] = (AgentTypes.AircraftAgent, 1.0),
        ["clearance required"] = (AgentTypes.AircraftAgent, 0.9),
        ["aircraft"] = (AgentTypes.AircraftAgent, 0.75),
        ["tail number"] = (AgentTypes.AircraftAgent, 0.85),

        // Readiness/General Keywords (map to Aircraft Agent for comprehensive view)
        ["readiness"] = (AgentTypes.AircraftAgent, 0.7),
        ["ready"] = (AgentTypes.AircraftAgent, 0.65),
        ["comprehensive"] = (AgentTypes.AircraftAgent, 1.0),
        ["overall"] = (AgentTypes.AircraftAgent, 0.8),
        ["all systems"] = (AgentTypes.AircraftAgent, 0.9),
    };

    // Query intent patterns for classification
    private static readonly Dictionary<string, string> IntentPatterns = new()
    {
        ["why|cause|reason|root"] = "root_cause_analysis",
        ["explain|explainability|details"] = "explainability",
        ["what should|recommend|suggest|action"] = "next_best_action",
        ["score|rating|assessment"] = "readiness_score",
        ["delay|late|behind"] = "delay_prediction",
        ["impact|affect|consequence"] = "cascading_impact",
        ["which|show|list|all"] = "broad_query",
        ["is|are|can|will"] = "specific_query",
        ["what-?if|change|substitute|swap"] = "what_if_scenario",
    };

    public AgentIntentClassifier(ILogger<AgentIntentClassifier> logger)
    {
        _logger = logger;
    }

    public AgentIntent ClassifyIntent(string userQuery)
    {
        if (string.IsNullOrWhiteSpace(userQuery))
        {
            return new AgentIntent
            {
                AgentType = AgentTypes.UnknownAgent,
                IntentCategory = "unknown",
                Confidence = "Low"
            };
        }

        var normalizedQuery = userQuery.ToLowerInvariant();
        
        // Extract entities
        var entities = ExtractEntities(userQuery);
        
        // Classify query type
        var queryType = ClassifyQueryType(normalizedQuery);
        
        // Determine intent category
        var intentCategory = DetermineIntentCategory(normalizedQuery);
        
        // Calculate agent scores using weighted keyword matching
        var agentScores = CalculateAgentScores(normalizedQuery);
        
        // Select best agent
        var bestAgent = SelectBestAgent(agentScores, entities, queryType);
        
        // Determine confidence level
        var confidence = DetermineConfidence(agentScores, entities, bestAgent);

        var explainability = BuildExplainability(userQuery, bestAgent, agentScores, entities, queryType, intentCategory);

        _logger.LogInformation(
            "Classified query '{Query}' -> Agent: {Agent}, Intent: {Intent}, Confidence: {Confidence}",
            userQuery, bestAgent, intentCategory, confidence);

        return new AgentIntent
        {
            AgentType = bestAgent,
            IntentCategory = intentCategory,
            QueryType = queryType,
            Confidence = confidence,
            Entities = entities,
            AgentScores = agentScores,
            Explainability = explainability
        };
    }

    private List<ExtractedEntity> ExtractEntities(string query)
    {
        var entities = new List<ExtractedEntity>();

        // Extract flight IDs
        var flightMatches = FlightIdPattern.Matches(query);
        foreach (Match match in flightMatches)
        {
            entities.Add(new ExtractedEntity { Type = "flight_id", Value = match.Value });
        }

        // Extract aircraft IDs
        var aircraftMatches = AircraftIdPattern.Matches(query);
        foreach (Match match in aircraftMatches)
        {
            if (match.Groups.Count > 2)
            {
                entities.Add(new ExtractedEntity { Type = "aircraft_id", Value = match.Groups[2].Value });
            }
        }

        // Extract time references
        var timeMatches = TimePattern.Matches(query);
        foreach (Match match in timeMatches)
        {
            entities.Add(new ExtractedEntity { Type = "time", Value = match.Value });
        }

        // Extract status keywords
        var statusMatches = StatusPattern.Matches(query);
        foreach (Match match in statusMatches)
        {
            entities.Add(new ExtractedEntity { Type = "status", Value = match.Value });
        }

        return entities;
    }

    private string ClassifyQueryType(string normalizedQuery)
    {
        // Broad query patterns
        if (Regex.IsMatch(normalizedQuery, @"\b(all|any|which|show|list)\b"))
        {
            return "broad_query";
        }

        // Specific query patterns
        if (Regex.IsMatch(normalizedQuery, @"\b(is|are|can|will|does|do)\b"))
        {
            return "specific_query";
        }

        // Root cause analysis patterns
        if (Regex.IsMatch(normalizedQuery, @"\b(why|cause|reason|root)\b"))
        {
            return "root_cause_analysis";
        }

        // What-if scenario patterns
        if (Regex.IsMatch(normalizedQuery, @"\b(what\s*if|change|substitute|swap)\b"))
        {
            return "what_if_scenario";
        }

        return "general_query";
    }

    private string DetermineIntentCategory(string normalizedQuery)
    {
        foreach (var (pattern, intent) in IntentPatterns)
        {
            if (Regex.IsMatch(normalizedQuery, pattern, RegexOptions.IgnoreCase))
            {
                return intent;
            }
        }

        return "general_query";
    }

    private Dictionary<string, double> CalculateAgentScores(string normalizedQuery)
    {
        var scores = new Dictionary<string, double>();

        // Sort keywords by length (longest first) for better matching
        var sortedKeywords = AgentKeywords
            .OrderByDescending(kv => kv.Key.Length)
            .ToList();

        foreach (var (keyword, (agent, weight)) in sortedKeywords)
        {
            if (normalizedQuery.Contains(keyword))
            {
                if (!scores.ContainsKey(agent))
                {
                    scores[agent] = 0;
                }
                scores[agent] += weight;

                _logger.LogDebug("Keyword '{Keyword}' matched for agent {Agent} with weight {Weight}",
                    keyword, agent, weight);
            }
        }

        return scores;
    }

    private string SelectBestAgent(Dictionary<string, double> agentScores, List<ExtractedEntity> entities, string queryType)
    {
        // If no agent scores, use fallback logic
        if (agentScores.Count == 0)
        {
            _logger.LogDebug("No keyword matches found. Using fallback logic.");

            // If flight/aircraft ID is present, default to Aircraft Agent
            if (entities.Any(e => e.Type == "flight_id" || e.Type == "aircraft_id"))
            {
                return AgentTypes.AircraftAgent;
            }

            // For broad queries without specific context, default to Maintenance Agent
            if (queryType == "broad_query")
            {
                return AgentTypes.MaintenanceAgent;
            }

            return AgentTypes.UnknownAgent;
        }

        // Get agent with highest score
        var topAgent = agentScores.OrderByDescending(kv => kv.Value).First();

        // Tie-breaking logic: if scores are close, prefer Aircraft Agent for comprehensive view
        var closeScores = agentScores
            .Where(kv => Math.Abs(kv.Value - topAgent.Value) < 0.1)
            .ToList();

        if (closeScores.Count > 1 && closeScores.Any(kv => kv.Key == AgentTypes.AircraftAgent))
        {
            _logger.LogDebug("Multiple close scores detected. Preferring Aircraft Agent for comprehensive view.");
            return AgentTypes.AircraftAgent;
        }

        return topAgent.Key;
    }

    private string DetermineConfidence(Dictionary<string, double> agentScores, List<ExtractedEntity> entities, string selectedAgent)
    {
        if (agentScores.Count == 0)
        {
            return "Low";
        }

        var topScore = agentScores.ContainsKey(selectedAgent) ? agentScores[selectedAgent] : 0;
        var hasEntities = entities.Any();

        // High confidence: Strong keyword match + entities
        if (topScore >= 1.0 && hasEntities)
        {
            return "High";
        }

        // Medium-High confidence: Good keyword match
        if (topScore >= 0.8)
        {
            return "Medium-High";
        }

        // Medium confidence: Some keyword match
        if (topScore >= 0.5)
        {
            return "Medium";
        }

        return "Low";
    }

    private string BuildExplainability(string originalQuery, string selectedAgent, Dictionary<string, double> agentScores, 
        List<ExtractedEntity> entities, string queryType, string intentCategory)
    {
        var explanation = new List<string>
        {
            $"Query classified as '{queryType}' with intent '{intentCategory}'.",
            $"Selected agent: {selectedAgent}."
        };

        if (agentScores.Any())
        {
            var scoreDetails = string.Join(", ", agentScores.Select(kv => $"{kv.Key}: {kv.Value:F2}"));
            explanation.Add($"Agent scores: {scoreDetails}.");
        }

        if (entities.Any())
        {
            var entityDetails = string.Join(", ", entities.Select(e => $"{e.Type}={e.Value}"));
            explanation.Add($"Extracted entities: {entityDetails}.");
        }
        else
        {
            explanation.Add("No entities extracted from query.");
        }

        return string.Join(" ", explanation);
    }
}
