using System.Text.Json;
using FlightReadinessEngine.Api.Agents;
using FlightReadinessEngine.Api.Cache;
using FlightReadinessEngine.Api.Models;
using Google.GenAI;

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
        private readonly IAgentCache _cache;
        private readonly string _projectId;
        private readonly string _location;
        private const string ModelId = "gemini-2.5-flash";

        public OperationManageAgent(
            ILogger<OperationManageAgent> logger,
            CrewAgent crewAgent,
            PartsAgent partsAgent,
            MaintenanceAgent maintenanceAgent,
            GroundAgent groundAgent,
            FlightPlanningAgent flightPlanningAgent,
            AircraftAgent aircraftAgent,
            IAgentCache cache)
        {
            _logger = logger;
            _crewAgent = crewAgent;
            _partsAgent = partsAgent;
            _maintenanceAgent = maintenanceAgent;
            _groundAgent = groundAgent;
            _flightPlanningAgent = flightPlanningAgent;
            _aircraftAgent = aircraftAgent;
            _cache = cache;
            _projectId = Environment.GetEnvironmentVariable("GCP_PROJECT_ID") ?? "";
            _location = Environment.GetEnvironmentVariable("GCP_VERTEX_LOCATION") ?? "us-central1";
        }

        public async Task<object> RunAsync(AgentSyncRequest? request)
        {
            _logger.LogInformation("--- [MASTER ORCHESTRATOR] Initializing Multi-Agent Dispatch Readiness Check ---");
            bool isTargetedRequest = request != null &&
                                     (!string.IsNullOrWhiteSpace(request.FlightId) ||
                                      !string.IsNullOrWhiteSpace(request.Flight) ||
                                      !string.IsNullOrWhiteSpace(request.Tail));

            _logger.LogInformation("[MASTER] Deploying domain agents in parallel (throttled to respect Vertex AI quota)...");

            // All six agents are dispatched in parallel. Full 6-wide concurrency bursts 12+
            // Vertex AI calls and can trip the per-minute Gemini quota (HTTP 429 /
            // ResourceExhausted) on constrained projects, so we cap concurrency (default 2)
            // and retry each agent with exponential backoff on 429. Set AGENT_MAX_CONCURRENCY=6
            // for fully parallel execution when quota allows.
            int maxConcurrency = int.TryParse(Environment.GetEnvironmentVariable("AGENT_MAX_CONCURRENCY"), out var mc) && mc > 0
                ? mc
                : 2;

            using var throttle = new SemaphoreSlim(maxConcurrency);

            async Task<object> DispatchAsync(string domain, Func<Task<object>> agentCall)
            {
                string cacheKey = BuildDomainCacheKey(domain, request);
                if (isTargetedRequest)
                {
                    string cached = await SafeGetAsync(cacheKey);
                    if (!string.IsNullOrWhiteSpace(cached))
                    {
                        _logger.LogInformation("[MASTER] Using cached {Domain} domain output for targeted request.", domain);
                        return JsonSerializer.Deserialize<JsonElement>(cached);
                    }
                }

                await throttle.WaitAsync();
                try
                {
                    var output = await RunWithRetryAsync(domain, agentCall);
                    if (isTargetedRequest)
                    {
                        await SafeSetAsync(cacheKey, JsonSerializer.Serialize(output));
                    }
                    return output;
                }
                finally { throttle.Release(); }
            }

            var crewTask = DispatchAsync("Crew", () => _crewAgent.RunAsync(request));
            var partsTask = DispatchAsync("Parts", () => _partsAgent.RunAsync(request));
            var maintenanceTask = DispatchAsync("Maintenance", () => _maintenanceAgent.RunAsync(request));
            var groundTask = DispatchAsync("Ground", () => _groundAgent.RunAsync(request));
            var flightPlanningTask = DispatchAsync("FlightPlanning", () => _flightPlanningAgent.RunAsync(request));
            var aircraftTask = DispatchAsync("Aircraft", () => _aircraftAgent.RunAsync(request));

            await Task.WhenAll(crewTask, partsTask, maintenanceTask, groundTask, flightPlanningTask, aircraftTask);

            _logger.LogInformation("[MASTER] All domain agents have reported. Aggregating results...");

            // Deterministic decision retained as the reliable source of truth / fallback.
            string deterministicDecision = DetermineFinalDispatchStatus(
                crewTask.Result,
                partsTask.Result,
                maintenanceTask.Result,
                groundTask.Result,
                flightPlanningTask.Result,
                aircraftTask.Result);

            // Google.GenAI orchestration layer: produce a reasoned, human-readable summary.
            string orchestrationSummary = await GenerateOrchestrationSummaryAsync(
                deterministicDecision,
                crewTask.Result,
                partsTask.Result,
                maintenanceTask.Result,
                groundTask.Result,
                flightPlanningTask.Result,
                aircraftTask.Result);

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
                overallDispatchDecision = deterministicDecision,
                orchestrationSummary
            };

            _logger.LogInformation($"[MASTER] Final dispatch decision: {aggregatedResults.overallDispatchDecision}");

            return aggregatedResults;
        }

        private string BuildDomainCacheKey(string domain, AgentSyncRequest? request)
        {
            string flightId = request?.FlightId?.Trim() ?? string.Empty;
            string flight = request?.Flight?.Trim() ?? string.Empty;
            string tail = request?.Tail?.Trim() ?? string.Empty;
            string requestKey = string.Join("|", new[] { flightId, flight, tail }).ToLowerInvariant();
            // Minute-level bucket keeps the cache fresh for the dashboard without stale persistence.
            string minuteBucket = DateTime.UtcNow.ToString("yyyyMMddHHmm");
            return $"master_agent:domain:{domain}:req:{requestKey}:bucket:{minuteBucket}";
        }

        private async Task<string> SafeGetAsync(string key)
        {
            try
            {
                return await _cache.GetStringAsync(key);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[MASTER] Cache read failed for key {CacheKey}", key);
                return string.Empty;
            }
        }

        private async Task SafeSetAsync(string key, string value)
        {
            if (string.IsNullOrWhiteSpace(value))
            {
                return;
            }

            try
            {
                await _cache.SetStringAsync(key, value);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[MASTER] Cache write failed for key {CacheKey}", key);
            }
        }

        // Retries an agent call when Vertex AI returns 429/ResourceExhausted,
        // using exponential backoff (1s, 2s, 4s, 8s...).
        private async Task<object> RunWithRetryAsync(string domain, Func<Task<object>> agentCall, int maxAttempts = 5)
        {
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    return await agentCall();
                }
                catch (Grpc.Core.RpcException ex) when (
                    ex.StatusCode == Grpc.Core.StatusCode.ResourceExhausted && attempt < maxAttempts)
                {
                    int delayMs = (int)(Math.Pow(2, attempt - 1) * 1000);
                    _logger.LogWarning($"[MASTER] {domain} agent hit Vertex AI quota (429). Retry {attempt}/{maxAttempts - 1} in {delayMs}ms...");
                    await Task.Delay(delayMs);
                }
            }
        }

        private async Task<string> GenerateOrchestrationSummaryAsync(string deterministicDecision, params object[] agentResults)
        {
            try
            {
                var client = new Client(
                    project: _projectId,
                    location: _location,
                    vertexAI: true,
                    credential: Services.GcpAuth.GetCredential());

                string domainPayload = JsonSerializer.Serialize(agentResults);

                string prompt =
                    "You are the Master Flight Operations Orchestrator. Six domain agents (Crew, Parts, " +
                    "Maintenance, Ground, Flight Planning, Aircraft) have each returned readiness assessments. " +
                    $"The deterministic rules engine computed this decision: '{deterministicDecision}'. " +
                    "Using the domain assessments below, write a concise operations briefing (3-5 sentences) that " +
                    "explains the overall dispatch readiness, names WHICH DOMAINS have issues (e.g. 'Maintenance and " +
                    "Crew report holds') rather than just counts, summarizes the specific risks in those domains, and " +
                    "states the recommended next action. Do not contradict the deterministic decision.\n\n" +
                    "DOMAIN ASSESSMENTS:\n" + domainPayload;

                var response = await client.Models.GenerateContentAsync(
                    model: ModelId,
                    contents: prompt);

                string? text = response?.Text;
                return string.IsNullOrWhiteSpace(text)
                    ? $"[Orchestrator] {deterministicDecision}"
                    : text.Trim();
            }
            catch (Exception ex)
            {
                // Preview SDK / auth issues must never break the dispatch response.
                _logger.LogWarning($"[MASTER] Google.GenAI orchestration summary unavailable, using deterministic decision. Reason: {ex.Message}");
                return $"[Orchestrator] {deterministicDecision}";
            }
        }

        private string DetermineFinalDispatchStatus(params object[] agentResults)
        {
            var criticalHolds = 0;
            var totalCleared = 0;
            var totalAssessed = 0;
            var affectedDomains = new List<string>();

            // Maps each agent's result payload property to a friendly domain name.
            var domainByProperty = new (string Property, string Domain)[]
            {
                ("updatedCrewAssessments", "Crew"),
                ("updatedPartsAssessments", "Parts"),
                ("updatedMaintenanceAssessments", "Maintenance"),
                ("updatedGroundAssessments", "Ground"),
                ("updatedFlightPlanningAssessments", "Flight Planning"),
                ("updatedAircraftAssessments", "Aircraft"),
            };

            foreach (var result in agentResults)
            {
                var jsonString = JsonSerializer.Serialize(result);
                var doc = JsonDocument.Parse(jsonString);

                foreach (var (property, domain) in domainByProperty)
                {
                    if (doc.RootElement.TryGetProperty(property, out var assessments) && assessments.GetArrayLength() > 0)
                    {
                        int holdsBefore = criticalHolds;
                        totalAssessed += AnalyzeAssessments(assessments, ref criticalHolds, ref totalCleared);
                        if (criticalHolds > holdsBefore)
                        {
                            affectedDomains.Add(domain);
                        }
                        break;
                    }
                }
            }

            if (criticalHolds > 0)
            {
                string domainList = affectedDomains.Count > 0 ? string.Join(", ", affectedDomains) : "one or more domains";
                return $"HOLD_DUE_TO_RISK ({criticalHolds} critical issue(s) detected across {affectedDomains.Count} domain(s): {domainList})";
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
