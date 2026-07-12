using System.Text.Json;
using FlightReadinessEngine.Api.Agents;
using FlightReadinessEngine.Api.Models;

namespace FlightReadinessEngine.Api.Master
{
    public class OperationManageAgent
    {
        private readonly ILogger<OperationManageAgent> _logger;
        private readonly CrewAgent _crewAgent;
        private readonly PartsAgent _partsAgent;
        private readonly MaintenanceAgent _maintenanceAgent;
        private readonly GroundAgent _groundAgent;
        private readonly FlightPlanningAgent _flightPlanningAgent;
        private readonly AircraftAgent _aircraftAgent;

        public OperationManageAgent(
            ILogger<OperationManageAgent> logger,
            CrewAgent crewAgent,
            PartsAgent partsAgent,
            MaintenanceAgent maintenanceAgent,
            GroundAgent groundAgent,
            FlightPlanningAgent flightPlanningAgent,
            AircraftAgent aircraftAgent)
        {
            _logger = logger;
            _crewAgent = crewAgent;
            _partsAgent = partsAgent;
            _maintenanceAgent = maintenanceAgent;
            _groundAgent = groundAgent;
            _flightPlanningAgent = flightPlanningAgent;
            _aircraftAgent = aircraftAgent;
        }

        public async Task<object> RunAsync(AgentSyncRequest? request)
        {
            _logger.LogInformation("--- [MASTER ORCHESTRATOR] Initializing Multi-Agent Dispatch Readiness Check ---");

            _logger.LogInformation("[MASTER] Deploying all domain agents concurrently...");

            var crewTask = _crewAgent.RunAsync(request);
            var partsTask = _partsAgent.RunAsync(request);
            var maintenanceTask = _maintenanceAgent.RunAsync(request);
            var groundTask = _groundAgent.RunAsync(request);
            var flightPlanningTask = _flightPlanningAgent.RunAsync(request);
            var aircraftTask = _aircraftAgent.RunAsync(request);

            await Task.WhenAll(crewTask, partsTask, maintenanceTask, groundTask, flightPlanningTask, aircraftTask);

            _logger.LogInformation("[MASTER] All domain agents have reported. Aggregating results...");

            var aggregatedResults = new
            {
                systemStatus = "MASTER_ORCHESTRATION_COMPLETE",
                timestamp = DateTime.UtcNow.ToString("o"),
                crewAssessment = crewTask.Result,
                partsAssessment = partsTask.Result,
                maintenanceAssessment = maintenanceTask.Result,
                groundAssessment = groundTask.Result,
                flightPlanningAssessment = flightPlanningTask.Result,
                aircraftAssessment = aircraftTask.Result,
                overallDispatchDecision = DetermineFinalDispatchStatus(
                    crewTask.Result,
                    partsTask.Result,
                    maintenanceTask.Result,
                    groundTask.Result,
                    flightPlanningTask.Result,
                    aircraftTask.Result)
            };

            _logger.LogInformation($"[MASTER] Final dispatch decision: {aggregatedResults.overallDispatchDecision}");

            return aggregatedResults;
        }

        private string DetermineFinalDispatchStatus(params object[] agentResults)
        {
            var criticalHolds = 0;
            var totalCleared = 0;
            var totalAssessed = 0;

            foreach (var result in agentResults)
            {
                var jsonString = JsonSerializer.Serialize(result);
                var doc = JsonDocument.Parse(jsonString);

                if (doc.RootElement.TryGetProperty("updatedCrewAssessments", out var crewAssessments) && crewAssessments.GetArrayLength() > 0)
                {
                    totalAssessed += AnalyzeAssessments(crewAssessments, ref criticalHolds, ref totalCleared);
                }
                else if (doc.RootElement.TryGetProperty("updatedPartsAssessments", out var partsAssessments) && partsAssessments.GetArrayLength() > 0)
                {
                    totalAssessed += AnalyzeAssessments(partsAssessments, ref criticalHolds, ref totalCleared);
                }
                else if (doc.RootElement.TryGetProperty("updatedMaintenanceAssessments", out var maintenanceAssessments) && maintenanceAssessments.GetArrayLength() > 0)
                {
                    totalAssessed += AnalyzeAssessments(maintenanceAssessments, ref criticalHolds, ref totalCleared);
                }
                else if (doc.RootElement.TryGetProperty("updatedGroundAssessments", out var groundAssessments) && groundAssessments.GetArrayLength() > 0)
                {
                    totalAssessed += AnalyzeAssessments(groundAssessments, ref criticalHolds, ref totalCleared);
                }
                else if (doc.RootElement.TryGetProperty("updatedFlightPlanningAssessments", out var planningAssessments) && planningAssessments.GetArrayLength() > 0)
                {
                    totalAssessed += AnalyzeAssessments(planningAssessments, ref criticalHolds, ref totalCleared);
                }
                else if (doc.RootElement.TryGetProperty("updatedAircraftAssessments", out var aircraftAssessments) && aircraftAssessments.GetArrayLength() > 0)
                {
                    totalAssessed += AnalyzeAssessments(aircraftAssessments, ref criticalHolds, ref totalCleared);
                }
            }

            if (criticalHolds > 0)
            {
                return $"HOLD_DUE_TO_RISK ({criticalHolds} critical issue(s) detected across {totalAssessed} aircraft)";
            }

            if (totalCleared == totalAssessed && totalAssessed > 0)
            {
                return $"APPROVED_FOR_DEPARTURE (All {totalAssessed} aircraft cleared across all domains)";
            }

            return totalAssessed > 0
                ? $"CONDITIONAL_CLEARANCE ({totalCleared} of {totalAssessed} cleared)"
                : "NO_DATA (No assessments returned from agents)";
        }

        private int AnalyzeAssessments(JsonElement assessments, ref int criticalHolds, ref int totalCleared)
        {
            int count = 0;

            foreach (var assessment in assessments.EnumerateArray())
            {
                count++;

                if (assessment.TryGetProperty("clearanceStatus", out var status))
                {
                    var statusValue = status.GetString() ?? "";

                    if (statusValue.Equals("CLEARED", StringComparison.OrdinalIgnoreCase))
                    {
                        totalCleared++;
                    }
                    else if (statusValue.Equals("HOLD_DUE_TO_RISK", StringComparison.OrdinalIgnoreCase))
                    {
                        criticalHolds++;
                    }
                }
            }

            return count;
        }
    }
}
