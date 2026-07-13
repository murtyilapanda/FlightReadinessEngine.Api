using System.Text.Json;
using FlightReadinessEngine.Api.Models;
using Google.Cloud.AIPlatform.V1;
using Google.Cloud.BigQuery.V2;

namespace FlightReadinessEngine.Api.Services
{
    public class CascadeImpactService
    {
        private readonly ILogger<CascadeImpactService> _logger;
        private readonly string _projectId;
        private readonly string _location;
        private const string ModelId = "gemini-2.5-flash";

        public CascadeImpactService(ILogger<CascadeImpactService> logger)
        {
            _logger = logger;
            _projectId = Environment.GetEnvironmentVariable("GCP_PROJECT_ID") ?? "qwiklabs-gcp-04-509f741dc909";
            _location = Environment.GetEnvironmentVariable("GCP_VERTEX_LOCATION") ?? "us-central1";
        }

        // =====================================================================
        // Analyzes downstream (cascading) operational impact caused by one or
        // more trigger tails, using the consolidated fleet_readiness_snapshot
        // table plus Vertex AI reasoning across schedule/gate/crew dependencies.
        // =====================================================================
        public async Task<object> AnalyzeCascadeImpactAsync(CascadeImpactRequest? request)
        {
            List<string> triggerTails = ExtractTails(request);

            if (triggerTails.Count == 0)
            {
                _logger.LogWarning("[CASCADE ENGINE] No tail number supplied in payload.");
                return new { error = "At least one tail number is required in the payload." };
            }

            _logger.LogInformation($"[CASCADE ENGINE] Evaluating downstream impact for trigger tail(s): {string.Join(", ", triggerTails)}");

            // 1. Pull the trigger tail rows (the flights that initiated the disruption).
            var triggerRows = await GetSnapshotRowsAsync(triggerTails);
            if (triggerRows.Count == 0)
            {
                return new { message = $"No fleet_readiness_snapshot records found for tail(s): {string.Join(", ", triggerTails)}." };
            }

            // 2. Pull the wider fleet context so the model can reason about
            //    shared tails, gate/airport conflicts and connecting crew.
            var fleetContext = await GetFleetContextAsync(triggerRows);

            // 3. Ask Vertex AI to reason about the cascading downstream impact.
            string aiResult = await EvaluateCascadeWithAi(triggerRows, fleetContext);

            return JsonSerializer.Deserialize<object>(aiResult) ?? new { cascadingImpact = Array.Empty<object>() };
        }

        private static List<string> ExtractTails(CascadeImpactRequest? request)
        {
            if (request == null) return new List<string>();

            string raw = !string.IsNullOrWhiteSpace(request.Tails)
                ? request.Tails!
                : request.Tail ?? string.Empty;

            return raw.Split(',')
                .Select(t => t.Trim())
                .Where(t => !string.IsNullOrWhiteSpace(t))
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();
        }

        private async Task<List<Dictionary<string, object?>>> GetSnapshotRowsAsync(List<string> tails)
        {
            BigQueryClient client = await BigQueryClient.CreateAsync(_projectId, GcpAuth.GetCredential());

            string query = $@"
                SELECT
                    tail, fleet_type, current_airport, scheduled_departure,
                    fuel_ticket_status, apu_is_running, apu_within_guidelines,
                    maintenance_status, work_order_status, maintenance_issue,
                    parts_status, part_required, part_available, part_in_transit, parts_issue,
                    crew_status, crew_report_status, crew_issue,
                    ground_ops_status, cargo_loading_complete, weight_balance_cleared, ground_ops_issue,
                    flight_plan_status, flight_number, flight_plan_issue, last_updated_at
                FROM `{_projectId}.aviation_ops_analytics.fleet_readiness_snapshot`
                WHERE tail IN UNNEST(@tails)
                ORDER BY scheduled_departure ASC";

            var parameters = new[] { new BigQueryParameter("tails", BigQueryDbType.Array, tails.ToArray()) };
            BigQueryResults results = await client.ExecuteQueryAsync(query, parameters);

            return MapRows(results);
        }

        // Pulls the ENTIRE fleet from the master consolidated snapshot so the
        // model can evaluate schedule ripple, gate conflicts and connecting-crew
        // impact against every other tail — not just the trigger tail.
        private async Task<List<Dictionary<string, object?>>> GetFleetContextAsync(List<Dictionary<string, object?>> triggerRows)
        {
            BigQueryClient client = await BigQueryClient.CreateAsync(_projectId, GcpAuth.GetCredential());

            var triggerTails = triggerRows.Select(r => r.GetValueOrDefault("tail")?.ToString())
                .Where(v => !string.IsNullOrWhiteSpace(v))
                .Select(v => v!)
                .ToHashSet(StringComparer.OrdinalIgnoreCase);

            string query = $@"
                SELECT
                    tail, fleet_type, current_airport, scheduled_departure,
                    maintenance_status, work_order_status, maintenance_issue,
                    parts_status, part_required, part_available, part_in_transit, parts_issue,
                    crew_status, crew_report_status, crew_issue,
                    ground_ops_status, cargo_loading_complete, weight_balance_cleared, ground_ops_issue,
                    flight_plan_status, flight_number, flight_plan_issue, last_updated_at
                FROM `{_projectId}.aviation_ops_analytics.fleet_readiness_snapshot`
                ORDER BY scheduled_departure ASC";

            BigQueryResults results = await client.ExecuteQueryAsync(query, Enumerable.Empty<BigQueryParameter>());
            var allRows = MapRows(results);

            // Exclude the trigger rows themselves from the context list; they are
            // supplied separately so the model treats them as the disruption source.
            return allRows
                .Where(r => !triggerTails.Contains(r.GetValueOrDefault("tail")?.ToString() ?? string.Empty))
                .ToList();
        }

        private static List<Dictionary<string, object?>> MapRows(BigQueryResults results)
        {
            var list = new List<Dictionary<string, object?>>();
            foreach (var row in results)
            {
                var record = new Dictionary<string, object?>();
                foreach (var field in row.Schema.Fields)
                {
                    record[field.Name] = row[field.Name]?.ToString();
                }
                list.Add(record);
            }
            return list;
        }

        private async Task<string> EvaluateCascadeWithAi(
            List<Dictionary<string, object?>> triggerRows,
            List<Dictionary<string, object?>> fleetContext)
        {
            var client = await new PredictionServiceClientBuilder
            {
                Endpoint = $"{_location}-aiplatform.googleapis.com",
                TokenAccessMethod = GcpAuth.TokenAccessMethod
            }.BuildAsync();

            string triggerJson = JsonSerializer.Serialize(triggerRows);
            string contextJson = JsonSerializer.Serialize(fleetContext);

            // Structured JSON response schema matching the requested shape.
            var flightAtRiskSchema = new OpenApiSchema { Type = Google.Cloud.AIPlatform.V1.Type.Object };
            flightAtRiskSchema.Properties.Add("tail", new OpenApiSchema { Type = Google.Cloud.AIPlatform.V1.Type.String });
            flightAtRiskSchema.Properties.Add("riskReason", new OpenApiSchema { Type = Google.Cloud.AIPlatform.V1.Type.String });
            flightAtRiskSchema.Properties.Add("riskLevel", new OpenApiSchema { Type = Google.Cloud.AIPlatform.V1.Type.String, Description = "MUST be one of 'HIGH', 'MEDIUM', or 'LOW'" });
            flightAtRiskSchema.Required.Add("tail");
            flightAtRiskSchema.Required.Add("riskReason");
            flightAtRiskSchema.Required.Add("riskLevel");

            var impactItemSchema = new OpenApiSchema { Type = Google.Cloud.AIPlatform.V1.Type.Object };
            impactItemSchema.Properties.Add("flightNumber", new OpenApiSchema { Type = Google.Cloud.AIPlatform.V1.Type.String });
            impactItemSchema.Properties.Add("flightsAtRisk", new OpenApiSchema
            {
                Type = Google.Cloud.AIPlatform.V1.Type.Array,
                Items = flightAtRiskSchema
            });
            impactItemSchema.Required.Add("flightNumber");
            impactItemSchema.Required.Add("flightsAtRisk");

            var responseSchema = new OpenApiSchema { Type = Google.Cloud.AIPlatform.V1.Type.Object };
            responseSchema.Properties.Add("cascadingImpact", new OpenApiSchema
            {
                Type = Google.Cloud.AIPlatform.V1.Type.Array,
                Items = impactItemSchema
            });
            responseSchema.Required.Add("cascadingImpact");

            string prompt = $@"
You are the Fleet Cascade Impact Analyst for an aviation operations control center.
Your analysis is presented live to the operations leadership panel, so it must be
thorough, specific, and production-grade.

TRIGGER FLIGHT(S) — the disruption originates here (rows from the master
consolidated `fleet_readiness_snapshot` table):
{triggerJson}

FULL FLEET SNAPSHOT — every OTHER flight in the master consolidated table. You MUST
scan ALL of these rows and determine which ones are impacted by the trigger flight(s):
{contextJson}

ANALYSIS METHOD — think step by step across EVERY dependency chain below and inspect
EVERY row in the full fleet snapshot before deciding:
  1. SAME TAIL REUSE — Find every other flight_number that uses the SAME tail as a
     trigger flight. A late/held inbound tail will miss its next scheduled departure
     window. These are almost always HIGH risk.
  2. GATE / AIRPORT CONFLICT — Find other tails at the SAME current_airport as a
     trigger flight whose scheduled_departure is close to or after the trigger's.
     Extended ground time (maintenance/parts hold) forces gate reassignment ? MEDIUM.
  3. CONNECTING CREW — Consider crew rotations: a trigger flight arriving late at a
     downstream airport breaks the crew-rest / duty window for the next flight that
     crew is assigned to. Flag those tails ? MEDIUM (HIGH if legality is breached).
  4. MAINTENANCE / PARTS HOLD — If the trigger has an active maintenance_status,
     work_order_status, or parts hold, everything scheduled behind it on the same
     tail or gate is delayed.
  5. SCHEDULE RIPPLE — Compare scheduled_departure timestamps to determine the order
     of impact and which downstream flights are threatened by accumulating delay.

OUTPUT RULES (STRICT):
  - For EACH trigger flight, produce ONE cascadingImpact entry keyed by its flightNumber.
  - The flightsAtRisk array MUST begin with the trigger flight itself
    (riskReason: ""Trigger flight — <describe the originating disruption>"", riskLevel HIGH).
  - Then you MUST append EVERY downstream tail that is genuinely at risk, one entry
    per impacted tail, each with a concise, specific riskReason that NAMES the
    mechanism, the airport, and the trigger flight (e.g.
    ""Gate conflict at SDF — gate reassignment needed due to 5X449 extended ground time"").
  - Do NOT return only the trigger flight. If the fleet data shows shared tails, shared
    airports, or connecting-crew relationships, they MUST appear as additional entries.
  - riskLevel MUST be exactly one of HIGH, MEDIUM, or LOW.
  - Only include flights that are realistically impacted — do not fabricate tails that
    are not present in the fleet snapshot.

Return ONLY the JSON object matching the required schema.";

            var request = new GenerateContentRequest
            {
                Model = $"projects/{_projectId}/locations/{_location}/publishers/google/models/{ModelId}",
                Contents =
                {
                    new Content
                    {
                        Role = "USER",
                        Parts = { new Part { Text = prompt } }
                    }
                },
                GenerationConfig = new GenerationConfig
                {
                    ResponseMimeType = "application/json",
                    ResponseSchema = responseSchema,
                    Temperature = 0.4f
                }
            };

            GenerateContentResponse response = await VertexRetry.InvokeAsync(
                () => client.GenerateContentAsync(request), _logger, "Cascade");
            return response.Candidates[0].Content.Parts[0].Text;
        }
    }
}
