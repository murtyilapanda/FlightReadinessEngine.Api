using FlightReadinessEngine.Api.Models;
using FlightReadinessEngine.Api.Services;
using Google.Cloud.BigQuery.V2;
using Microsoft.AspNetCore.Mvc;

namespace FlightReadinessEngine.Api.Agents
{
    [ApiController]
    [Route("api/flight")]
    public class FlightController : ControllerBase
    {
        private readonly ILogger<FlightController> _logger;
        private readonly FlightService _flightService;
        private readonly CascadeImpactService _cascadeImpactService;
        private readonly TailDetailsService _tailDetailsService;

        public FlightController(
            ILogger<FlightController> logger,
            FlightService flightService,
            CascadeImpactService cascadeImpactService,
            TailDetailsService tailDetailsService)
        {
            _logger = logger;
            _flightService = flightService;
            _cascadeImpactService = cascadeImpactService;
            _tailDetailsService = tailDetailsService;
        }

        // =========================================================================
        // DIRECT DATA EXPOSURE API: Fetches raw snapshot data for UI processing
        // GET api/flight/fleet/snapshot
        // =========================================================================
        [HttpGet("fleet/snapshot")]
        public async Task<IActionResult> GetFleetReadinessSnapshot()
        {
            _logger.LogInformation("--- [DATA PIPELINE] Fetching Raw Fleet Readiness Snapshot Data ---");

            try
            {
                var snapshotList = await _flightService.GetFleetReadinessSnapshotAsync();
                return Ok(snapshotList);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error extracting fleet snapshot metrics: {ex.Message}");
                return StatusCode(500, new
                {
                    error = "Failed to extract operational data lake metrics.",
                    details = ex.Message
                });
            }
        }

        // =========================================================================
        // L2 DRILL-DOWN API: When a user clicks a tail from the snapshot list, this
        // returns that tail's matched row from all 6 upstream domain tables as
        // separate nodes under tailDetails.
        // GET api/flight/tail/{tail}/details
        // =========================================================================
        [HttpGet("tail/{tail}/details")]
        public async Task<IActionResult> GetTailDetails(string tail)
        {
            _logger.LogInformation($"--- [L2 DRILL-DOWN] Fetching all domain table details for tail: {tail} ---");

            try
            {
                var details = await _tailDetailsService.GetTailDetailsAsync(tail);
                return Ok(details);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error extracting tail details: {ex.Message}");
                return StatusCode(500, new
                {
                    error = "Failed to extract tail domain details.",
                    details = ex.Message
                });
            }
        }

        // =========================================================================
        // CASCADE IMPACT API: Given at least one trigger tail, evaluates downstream
        // operational impact across other tails/flights using the consolidated
        // fleet_readiness_snapshot table and Vertex AI reasoning.
        // POST api/flight/cascade/impact
        // =========================================================================
        [HttpPost("cascade/impact")]
        public async Task<IActionResult> GetCascadeImpact([FromBody] CascadeImpactRequest? request)
        {
            _logger.LogInformation("--- [CASCADE PIPELINE] Evaluating downstream fleet cascade impact ---");

            try
            {
                var result = await _cascadeImpactService.AnalyzeCascadeImpactAsync(request);
                return Ok(result);
            }
            catch (Exception ex)
            {
                _logger.LogError($"Error evaluating cascade impact: {ex.Message}");
                return StatusCode(500, new
                {
                    error = "Failed to evaluate cascading impact analysis.",
                    details = ex.Message
                });
            }
        }
    }
}
