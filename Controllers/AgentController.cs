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

        [HttpPost("infographic")]
        public async Task<IActionResult> RunInfographicAgent([FromBody] AgentSyncRequest? request)
        {
            string html = await _infographicAgent.RunAsync(request);
            return Content(html, "text/html; charset=utf-8");
        }
    }
}
