using FlightReadinessEngine.Api.Agents;
using FlightReadinessEngine.Api.Master;
using FlightReadinessEngine.Api.Models;
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

        public AgentController(
            ILogger<AgentController> logger,
            CrewAgent crewAgent,
            PartsAgent partsAgent,
            MaintenanceAgent maintenanceAgent,
            GroundAgent groundAgent,
            FlightPlanningAgent flightPlanningAgent,
            AircraftAgent aircraftAgent,
            OperationManageAgent masterAgent)
        {
            _logger = logger;
            _crewAgent = crewAgent;
            _partsAgent = partsAgent;
            _maintenanceAgent = maintenanceAgent;
            _groundAgent = groundAgent;
            _flightPlanningAgent = flightPlanningAgent;
            _aircraftAgent = aircraftAgent;
            _masterAgent = masterAgent;
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
    }
}
