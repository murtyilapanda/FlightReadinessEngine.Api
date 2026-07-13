using FlightReadinessEngine.Api.Agents;
using FlightReadinessEngine.Api.Master;
using FlightReadinessEngine.Api.Models;
using FlightReadinessEngine.Api.Services;
using Microsoft.AspNetCore.Mvc;

namespace FlightReadinessEngine.Api.Agents
{
    [ApiController]
    [Route("api/agents")]
    public class AgentController : ControllerBase
    {
        private readonly ILogger<AgentController> _logger;
        private readonly CrewAgent _crewAgent;
        private readonly PartsAgent _partsAgent;
        private readonly MaintenanceAgent _maintenanceAgent;
        private readonly GroundAgent _groundAgent;
        private readonly FlightPlanningAgent _flightPlanningAgent;
        private readonly AircraftAgent _aircraftAgent;
        private readonly OperationManageAgent _masterAgent;
        private readonly IAgentIntentClassifier _intentClassifier;
        private readonly InfographicAgent _infographicAgent;
        private readonly MasterChatService _masterChatService;

        public AgentController(
            ILogger<AgentController> logger,
            CrewAgent crewAgent,
            PartsAgent partsAgent,
            MaintenanceAgent maintenanceAgent,
            GroundAgent groundAgent,
            FlightPlanningAgent flightPlanningAgent,
            AircraftAgent aircraftAgent,
            OperationManageAgent masterAgent,
            IAgentIntentClassifier intentClassifier,
            InfographicAgent infographicAgent,
            MasterChatService masterChatService)
        {
            _logger = logger;
            _crewAgent = crewAgent;
            _partsAgent = partsAgent;
            _maintenanceAgent = maintenanceAgent;
            _groundAgent = groundAgent;
            _flightPlanningAgent = flightPlanningAgent;
            _aircraftAgent = aircraftAgent;
            _masterAgent = masterAgent;
            _intentClassifier = intentClassifier;
            _infographicAgent = infographicAgent;
            _masterChatService = masterChatService;
        }

        /// <summary>
        /// Unified master chatbot endpoint. Reasons over the ENTIRE consolidated
        /// fleet_readiness_snapshot master table in a single call, using the intent
        /// classifier to focus the answer, and returns a rich, grounded chatbot reply.
        /// </summary>
        [HttpPost("chat")]
        [ProducesResponseType(typeof(MasterChatResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(MasterChatResponse), StatusCodes.Status400BadRequest)]
        public async Task<ActionResult<MasterChatResponse>> Chat([FromBody] MasterChatRequest? request)
        {
            var response = await _masterChatService.AskAsync(request);
            return response.Success ? Ok(response) : BadRequest(response);
        }

        [HttpPost("crew")]
        public async Task<IActionResult> RunCrewAgent([FromBody] AgentSyncRequest? request)
        {
            var result = await _crewAgent.RunAsync(request);
            return Ok(result);
        }

        [HttpPost("parts")]
        public async Task<IActionResult> RunPartsAgent([FromBody] AgentSyncRequest? request)
        {
            var result = await _partsAgent.RunAsync(request);
            return Ok(result);
        }

        [HttpPost("maintenance")]
        public async Task<IActionResult> RunMaintenanceAgent([FromBody] AgentSyncRequest? request)
        {
            var result = await _maintenanceAgent.RunAsync(request);
            return Ok(result);
        }

        [HttpPost("ground")]
        public async Task<IActionResult> RunGroundAgent([FromBody] AgentSyncRequest? request)
        {
            var result = await _groundAgent.RunAsync(request);
            return Ok(result);
        }

        [HttpPost("flight-planning")]
        public async Task<IActionResult> RunFlightPlanningAgent([FromBody] AgentSyncRequest? request)
        {
            var result = await _flightPlanningAgent.RunAsync(request);
            return Ok(result);
        }

        [HttpPost("aircraft")]
        public async Task<IActionResult> RunAircraftAgent([FromBody] AgentSyncRequest? request)
        {
            var result = await _aircraftAgent.RunAsync(request);
            return Ok(result);
        }

        [HttpPost("master")]
        public async Task<IActionResult> RunMasterOrchestration([FromBody] AgentSyncRequest? request)
        {
            var result = await _masterAgent.RunAsync(request);
            return Ok(result);
        }

        /// <summary>
        /// Process natural language query with AI agents
        /// This endpoint enables direct UI integration without BFF layer
        /// </summary>
        [HttpPost("query")]
        [ProducesResponseType(typeof(AgentQueryResponse), StatusCodes.Status200OK)]
        [ProducesResponseType(typeof(AgentQueryResponse), StatusCodes.Status400BadRequest)]
        public async Task<ActionResult<AgentQueryResponse>> ProcessQuery([FromBody] AgentQueryRequest request)
        {
            try
            {
                if (string.IsNullOrWhiteSpace(request.UserQuery))
                {
                    return BadRequest(CreateErrorResponse(
                        request.UserQuery,
                        "INVALID_REQUEST",
                        "userQuery field is required and cannot be empty",
                        "Please provide a query about flight readiness, crew, maintenance, etc."));
                }

                _logger.LogInformation("Processing agent query: {Query}", request.UserQuery);

                // Step 1: Classify intent and determine agent
                var intent = _intentClassifier.ClassifyIntent(request.UserQuery);

                // Step 2: Route to appropriate agent directly (no HTTP call needed)
                var response = await RouteToAgent(intent, request.UserQuery);

                return Ok(response);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error processing agent query: {Query}", request.UserQuery);
                return StatusCode(500, CreateErrorResponse(
                    request.UserQuery,
                    "INTERNAL_ERROR",
                    "An unexpected error occurred while processing your query",
                    ex.Message));
            }
        }

        /// <summary>
        /// Get agent capabilities and supported query types
        /// </summary>
        [HttpGet("capabilities")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<object> GetCapabilities()
        {
            return Ok(new
            {
                agents = new[]
                {
                    new
                    {
                        name = AgentTypes.MaintenanceAgent,
                        description = "Analyzes aircraft maintenance status, critical issues, and grounding requirements",
                        capabilities = new[] { "maintenance_status", "critical_issues", "grounding_analysis", "repair_tracking" },
                        sampleQueries = new[]
                        {
                            "Which aircraft have pending critical maintenance?",
                            "Is the engine status OK for flight AI-102?",
                            "Show all maintenance blockers for today"
                        }
                    },
                    new
                    {
                        name = AgentTypes.CrewAgent,
                        description = "Analyzes crew assignment, compliance, and rest hours",
                        capabilities = new[] { "crew_assignment", "rest_hours", "compliance_check", "crew_availability" },
                        sampleQueries = new[]
                        {
                            "Is the assigned crew for flight AI-102 compliant?",
                            "Which flights have crew issues?",
                            "Check pilot rest hours for AA-123"
                        }
                    },
                    new
                    {
                        name = AgentTypes.FlightPlanningAgent,
                        description = "Analyzes flight plans, weather, fuel, and ATC clearance",
                        capabilities = new[] { "flight_plan_status", "weather_analysis", "fuel_planning", "atc_clearance" },
                        sampleQueries = new[]
                        {
                            "What is the fuel plan vs uplift for flight AI-102?",
                            "Is the flight plan filed for AA-123?",
                            "Check weather conditions for FL-456"
                        }
                    },
                    new
                    {
                        name = AgentTypes.GroundAgent,
                        description = "Analyzes ground operations, APU status, and gate connections",
                        capabilities = new[] { "ground_operations", "apu_monitoring", "baggage_status", "gate_readiness" },
                        sampleQueries = new[]
                        {
                            "Is APU overrun for any flights?",
                            "Check baggage loading status for AI-102",
                            "Which flights need ground power?"
                        }
                    },
                    new
                    {
                        name = AgentTypes.PartsAgent,
                        description = "Analyzes parts availability and inventory status",
                        capabilities = new[] { "parts_availability", "inventory_status", "missing_parts", "parts_tracking" },
                        sampleQueries = new[]
                        {
                            "Are all required parts available for flight AI-102?",
                            "Which aircraft have missing parts?",
                            "Show parts inventory status"
                        }
                    },
                    new
                    {
                        name = AgentTypes.AircraftAgent,
                        description = "Analyzes aircraft readiness, grounded status, and comprehensive flight readiness across all domains",
                        capabilities = new[] { "aircraft_readiness", "grounded_status", "comprehensive_analysis", "dispatch_decision", "clearance_tracking" },
                        sampleQueries = new[]
                        {
                            "Is flight AI-102 ready for departure?",
                            "Which aircraft are currently grounded?",
                            "Give me readiness summary for AA-123",
                            "Show overall status for next 6 hours"
                        }
                    }
                },
                queryTypes = new[]
                {
                    "specific_query - Query about specific flight/aircraft",
                    "broad_query - Query about all flights or multiple aircraft",
                    "root_cause_analysis - Why/cause/reason questions",
                    "explainability - Explain/details requests",
                    "next_best_action - Action recommendations",
                    "what_if_scenario - Hypothetical scenarios"
                },
                supportedEntities = new[]
                {
                    "flight_id - Flight identifiers (e.g., AI-102, AA-123)",
                    "aircraft_id - Aircraft identifiers",
                    "time - Time references (tomorrow, next 2 hours, etc.)",
                    "status - Status keywords (ready, grounded, delayed, etc.)"
                }
            });
        }

        /// <summary>
        /// Get sample queries for testing
        /// </summary>
        [HttpGet("samples")]
        [ProducesResponseType(StatusCodes.Status200OK)]
        public ActionResult<object> GetSampleQueries()
        {
            return Ok(new
            {
                categories = new[]
                {
                    new
                    {
                        category = "Maintenance Queries",
                        samples = new[]
                        {
                            "Which aircraft have pending critical maintenance?",
                            "Is the engine status OK for flight AI-102?",
                            "Show all maintenance blockers for today",
                            "Why is aircraft N12345 out of service?"
                        }
                    },
                    new
                    {
                        category = "Crew Queries",
                        samples = new[]
                        {
                            "Is the assigned crew for flight AI-102 compliant?",
                            "Which flights have crew not assigned?",
                            "Check pilot rest hours for AA-123",
                            "Are all crew members available for tomorrow's flights?"
                        }
                    },
                    new
                    {
                        category = "Flight Planning Queries",
                        samples = new[]
                        {
                            "What is the fuel plan vs uplift for flight AI-102?",
                            "Is the flight plan filed for AA-123?",
                            "Are there any fuel ticket issues?",
                            "Check weather conditions for next 2 hours"
                        }
                    },
                    new
                    {
                        category = "Ground Operations Queries",
                        samples = new[]
                        {
                            "Is APU overrun for any flights?",
                            "Which flights require ground power?",
                            "Check baggage loading status for AI-102",
                            "Are all ground checkpoints complete?"
                        }
                    },
                    new
                    {
                        category = "Comprehensive Queries",
                        samples = new[]
                        {
                            "Is flight AI-102 on tomorrow 10:00 AM departure ready?",
                            "Show all readiness blockers for today",
                            "Give me readiness summary for next 6 hours",
                            "Which flights are not ready in next 2 hours?"
                        }
                    }
                }
            });
        }

        // Private helper methods

        /// <summary>
        /// Routes query to appropriate agent based on classified intent
        /// Direct agent invocation - no HTTP calls needed
        /// </summary>
        private async Task<AgentQueryResponse> RouteToAgent(AgentIntent intent, string userQuery)
        {
            try
            {
                // Build agent request from extracted entities
                var agentRequest = BuildAgentRequest(intent);

                // Route directly to appropriate agent (no HTTP overhead)
                object? agentResult = intent.AgentType switch
                {
                    AgentTypes.CrewAgent => await _crewAgent.RunAsync(agentRequest),
                    AgentTypes.PartsAgent => await _partsAgent.RunAsync(agentRequest),
                    AgentTypes.MaintenanceAgent => await _maintenanceAgent.RunAsync(agentRequest),
                    AgentTypes.GroundAgent => await _groundAgent.RunAsync(agentRequest),
                    AgentTypes.FlightPlanningAgent => await _flightPlanningAgent.RunAsync(agentRequest),
                    AgentTypes.AircraftAgent => await _aircraftAgent.RunAsync(agentRequest),
                    AgentTypes.MasterAgent => await _masterAgent.RunAsync(agentRequest),
                    _ => null
                };

                if (agentResult == null && intent.AgentType == AgentTypes.UnknownAgent)
                {
                    return CreateErrorResponse(
                        userQuery,
                        "UNKNOWN_AGENT",
                        "Could not determine appropriate agent for this query",
                        $"Please try a more specific query. Detected confidence: {intent.Confidence}");
                }

                // Build response
                return BuildAgentResponse(userQuery, intent, agentResult);
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "Error routing to agent {Agent} for query: {Query}",
                    intent.AgentType, userQuery);

                return CreateErrorResponse(
                    userQuery,
                    "AGENT_EXECUTION_ERROR",
                    $"Error executing {intent.AgentType}",
                    ex.Message);
            }
        }

        /// <summary>
        /// Builds agent request from classified intent
        /// </summary>
        private AgentSyncRequest BuildAgentRequest(AgentIntent intent)
        {
            var flightIds = intent.Entities
                .Where(e => e.Type == "flight_id")
                .Select(e => e.Value)
                .ToList();

            var aircraftIds = intent.Entities
                .Where(e => e.Type == "aircraft_id")
                .Select(e => e.Value)
                .ToList();

            var flightIdString = flightIds.Any() ? string.Join(",", flightIds) : null;
            var tailString = aircraftIds.Any() ? string.Join(",", aircraftIds) : null;

            return new AgentSyncRequest
            {
                FlightId = flightIdString,
                Tail = tailString
            };
        }

        /// <summary>
        /// Builds unified response from agent execution
        /// </summary>
        private AgentQueryResponse BuildAgentResponse(string userQuery, AgentIntent intent, object? agentResult)
        {
            var extractedEntities = intent.Entities.Select(e => $"{e.Type}:{e.Value}").ToList();

            return new AgentQueryResponse
            {
                Success = true,
                Query = userQuery,
                Intent = intent.IntentCategory,
                Agent = intent.AgentType,
                ExtractedEntities = extractedEntities,
                Analysis = new AgentAnalysis
                {
                    Summary = $"Analysis completed by {intent.AgentType}",
                    Data = agentResult,
                    Confidence = intent.Confidence,
                    Explainability = intent.Explainability
                },
                Recommendations = new List<ActionRecommendation>(),
                Timestamp = DateTime.UtcNow
            };
        }

        /// <summary>
        /// Creates error response with consistent format
        /// </summary>
        private AgentQueryResponse CreateErrorResponse(string query, string errorCode, string message, string details)
        {
            return new AgentQueryResponse
            {
                Success = false,
                Query = query,
                Error = new ErrorDetails
                {
                    Code = errorCode,
                    Message = message,
                    Details = details
                },
                Timestamp = DateTime.UtcNow
            };
        }

        [HttpPost("infographic")]
        public async Task<IActionResult> RunInfographicAgent([FromBody] AgentSyncRequest? request)
        {
            string html = await _infographicAgent.RunAsync(request);
            return Content(html, "text/html; charset=utf-8");
        }
    }
}
