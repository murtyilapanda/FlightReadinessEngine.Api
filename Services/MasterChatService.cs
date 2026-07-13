using System.Text.Json;
using FlightReadinessEngine.Api.Models;
using Google.Cloud.AIPlatform.V1;
using Google.Cloud.BigQuery.V2;

namespace FlightReadinessEngine.Api.Services
{
    /// <summary>
    /// Unified master chatbot layer.
    ///
    /// Instead of routing to a single domain agent that runs its own query, this
    /// service reads the ENTIRE consolidated `fleet_readiness_snapshot` master table
    /// once, uses the deterministic intent classifier to decide WHICH slice of the
    /// data matters, and then lets Vertex AI (Gemini) reason over the grounded rows
    /// to produce a rich, explainable, chatbot-ready answer.
    ///
    /// Design principle: the model NEVER generates or executes code. It only reasons
    /// over real rows we hand it and returns a schema-constrained JSON answer. This
    /// keeps the layer safe (no code injection), grounded (no hallucinated tails) and
    /// token-efficient (we pre-filter before we prompt).
    /// </summary>
    public class MasterChatService
    {
        private readonly ILogger<MasterChatService> _logger;
        private readonly FlightService _flightService;
        private readonly IAgentIntentClassifier _classifier;
        private readonly string _projectId;
        private readonly string _location;
        private const string ModelId = "gemini-2.5-flash";

        // Maps a classified agent to the master-table columns that matter for it, so
        // the model is told exactly which domain to spotlight while still seeing the
        // full readiness picture for context.
        private static readonly Dictionary<string, string[]> DomainSpotlightColumns = new()
        {
            [AgentTypes.MaintenanceAgent] = new[] { "maintenance_status", "work_order_status", "maintenance_issue" },
            [AgentTypes.PartsAgent] = new[] { "parts_status", "part_required", "part_available", "part_in_transit", "parts_issue" },
            [AgentTypes.CrewAgent] = new[] { "crew_status", "crew_report_status", "crew_issue" },
            [AgentTypes.GroundAgent] = new[] { "ground_ops_status", "cargo_loading_complete", "weight_balance_cleared", "apu_is_running", "apu_runtime_mins", "apu_within_guidelines", "ground_ops_issue" },
            [AgentTypes.FlightPlanningAgent] = new[] { "flight_plan_status", "flight_number", "flight_plan_issue", "fuel_ticket_status", "fuel_ticket_number" },
            [AgentTypes.AircraftAgent] = new[] { "maintenance_status", "parts_status", "crew_status", "ground_ops_status", "flight_plan_status", "fuel_ticket_status" },
        };

        private static readonly Dictionary<string, string> DomainLabels = new()
        {
            [AgentTypes.MaintenanceAgent] = "Maintenance",
            [AgentTypes.PartsAgent] = "Parts & Inventory",
            [AgentTypes.CrewAgent] = "Crew",
            [AgentTypes.GroundAgent] = "Ground Operations",
            [AgentTypes.FlightPlanningAgent] = "Flight Planning & Fuel",
            [AgentTypes.AircraftAgent] = "Aircraft Readiness (all domains)",
            [AgentTypes.MasterAgent] = "Fleet-wide Operations",
            [AgentTypes.UnknownAgent] = "Fleet-wide Operations",
        };

        public MasterChatService(
            ILogger<MasterChatService> logger,
            FlightService flightService,
            IAgentIntentClassifier classifier)
        {
            _logger = logger;
            _flightService = flightService;
            _classifier = classifier;
            _projectId = Environment.GetEnvironmentVariable("GCP_PROJECT_ID") ?? "qwiklabs-gcp-04-509f741dc909";
            _location = Environment.GetEnvironmentVariable("GCP_VERTEX_LOCATION") ?? "us-central1";
        }

        public async Task<MasterChatResponse> AskAsync(MasterChatRequest? request)
        {
            var userQuery = request?.UserQuery?.Trim() ?? string.Empty;

            if (string.IsNullOrWhiteSpace(userQuery))
            {
                return new MasterChatResponse
                {
                    Success = false,
                    Query = userQuery,
                    Error = new ErrorDetails
                    {
                        Code = "INVALID_REQUEST",
                        Message = "userQuery is required and cannot be empty.",
                        Details = "Ask something like 'Which aircraft have crew issues in the next 2 hours?'"
                    }
                };
            }

            try
            {
                // 1. Deterministic understanding of the question.
                var intent = _classifier.ClassifyIntent(userQuery);
                var domain = string.IsNullOrWhiteSpace(intent.AgentType) ? AgentTypes.AircraftAgent : intent.AgentType;

                _logger.LogInformation(
                    "[MASTER CHAT] Query '{Query}' -> domain {Domain}, intent {Intent}, confidence {Confidence}",
                    userQuery, domain, intent.IntentCategory, intent.Confidence);

                // 2. Pull the whole master table ONCE (single source of truth).
                var allRows = await _flightService.GetFleetReadinessSnapshotAsync();

                if (allRows.Count == 0)
                {
                    return new MasterChatResponse
                    {
                        Success = false,
                        Query = userQuery,
                        Domain = domain,
                        Intent = intent.IntentCategory,
                        Confidence = intent.Confidence,
                        Error = new ErrorDetails
                        {
                            Code = "NO_DATA",
                            Message = "The fleet_readiness_snapshot master table returned no rows.",
                            Details = "Verify the BigQuery table is populated for this project."
                        }
                    };
                }

                // 3. Deterministically pre-filter to the rows the model should focus on.
                var requestedTails = ExtractValues(intent, "aircraft_id");
                var requestedFlights = ExtractValues(intent, "flight_id");

                var focusRows = FilterFocusRows(allRows, requestedTails, requestedFlights);

                // 4. Ask Gemini to reason over grounded rows and return a chatbot answer.
                var ai = await ReasonWithAi(userQuery, request?.Context, domain, intent, focusRows, allRows);

                ai.Success = true;
                ai.Query = userQuery;
                ai.Domain = domain;
                ai.Intent = intent.IntentCategory;
                ai.Confidence = intent.Confidence;
                ai.RowsScanned = allRows.Count;
                ai.Explainability = intent.Explainability;
                ai.ExtractedEntities = intent.Entities.Select(e => $"{e.Type}:{e.Value}").ToList();
                return ai;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[MASTER CHAT] Failed to answer query: {Query}", userQuery);
                return new MasterChatResponse
                {
                    Success = false,
                    Query = userQuery,
                    Error = new ErrorDetails
                    {
                        Code = "INTERNAL_ERROR",
                        Message = "An unexpected error occurred while answering your query.",
                        Details = ex.Message
                    }
                };
            }
        }

        private static List<string> ExtractValues(AgentIntent intent, string type) =>
            intent.Entities
                .Where(e => e.Type == type && !string.IsNullOrWhiteSpace(e.Value))
                .Select(e => e.Value.Trim())
                .Distinct(StringComparer.OrdinalIgnoreCase)
                .ToList();

        // Narrows the master rows to those the user explicitly named. If they named
        // nothing (a broad/fleet question), every row is in focus.
        private static List<Dictionary<string, object?>> FilterFocusRows(
            List<Dictionary<string, object?>> allRows,
            List<string> tails,
            List<string> flights)
        {
            if (tails.Count == 0 && flights.Count == 0)
            {
                return allRows;
            }

            var tailSet = new HashSet<string>(tails, StringComparer.OrdinalIgnoreCase);
            var flightSet = new HashSet<string>(
                flights.Select(NormalizeId), StringComparer.OrdinalIgnoreCase);

            var matches = allRows.Where(r =>
            {
                var tail = r.GetValueOrDefault("tail")?.ToString() ?? string.Empty;
                var flight = NormalizeId(r.GetValueOrDefault("flight_number")?.ToString() ?? string.Empty);
                return tailSet.Contains(tail) || flightSet.Contains(flight);
            }).ToList();

            // If the named entities don't match any row, fall back to the whole fleet
            // so the model can still give a useful "not found / here's the fleet" answer.
            return matches.Count > 0 ? matches : allRows;
        }

        private static string NormalizeId(string id) =>
            id.Replace("-", string.Empty).Replace(" ", string.Empty).Trim();

        private async Task<MasterChatResponse> ReasonWithAi(
            string userQuery,
            string? conversationContext,
            string domain,
            AgentIntent intent,
            List<Dictionary<string, object?>> focusRows,
            List<Dictionary<string, object?>> allRows)
        {
            var client = await new PredictionServiceClientBuilder
            {
                Endpoint = $"{_location}-aiplatform.googleapis.com",
                TokenAccessMethod = GcpAuth.TokenAccessMethod
            }.BuildAsync();

            var domainLabel = DomainLabels.GetValueOrDefault(domain, "Fleet-wide Operations");
            var spotlight = DomainSpotlightColumns.GetValueOrDefault(domain, Array.Empty<string>());

            string focusJson = JsonSerializer.Serialize(Simplify(focusRows));
            // Only send the wider fleet context when the question isn't already scoped
            // to a small focus set — keeps token usage sensible.
            bool sendFleetContext = focusRows.Count == allRows.Count || focusRows.Count <= 3;
            string fleetJson = sendFleetContext
                ? JsonSerializer.Serialize(Simplify(allRows))
                : "[]";

            var responseSchema = BuildResponseSchema();

            string prompt = $@"
You are the Master Operations Copilot for an airline's fleet readiness control center.
You answer operational questions in a clear, confident, decision-ready way, and your
answers are shown live in a chatbot to operations controllers, so they must be specific,
grounded ONLY in the data provided, and never invented.

USER QUESTION:
""{userQuery}""

{(string.IsNullOrWhiteSpace(conversationContext) ? "" : $"CONVERSATION CONTEXT (previous turn):\n{conversationContext}\n")}
ROUTING (already computed deterministically for you):
  - Primary domain in focus: {domainLabel}
  - Intent category: {intent.IntentCategory}
  - Query type: {intent.QueryType}
  - Spotlight columns (pay special attention to these): {(spotlight.Length > 0 ? string.Join(", ", spotlight) : "all readiness columns")}

FOCUS ROWS — the flights the user is asking about (each row is a full readiness record
from the consolidated master `fleet_readiness_snapshot` table, covering all six domains:
maintenance, parts, crew, ground ops, flight planning/fuel, and aircraft):
{focusJson}

FULL FLEET SNAPSHOT — every flight in the master table (use for broad questions,
comparisons, counts and cross-flight impact; empty when the question is already scoped):
{fleetJson}

HOW TO ANSWER:
  1. Read the focus rows (and fleet snapshot when present) and reason across the spotlight
     columns for the primary domain, but consider other domains when they clearly affect
     readiness (e.g. a parts hold that causes a maintenance block).
  2. Produce a concise, natural-language 'answer' (2-5 sentences) that DIRECTLY answers the
     user's question, naming specific tails and flight numbers and the concrete reasons.
  3. Fill 'keyFindings' with short, factual bullet strings (each a single finding).
  4. Fill 'affectedFlights' with one entry per relevant tail. status = the domain status
     value from the data; issue = the specific issue text (empty string if none).
  5. Fill 'recommendations' with concrete next-best actions (priority = HIGH|MEDIUM|LOW).
     If everything is healthy, return an empty recommendations array.

STRICT RULES:
  - Use ONLY tails, flight numbers and statuses that appear in the provided data. Never
    fabricate a tail or flight that is not present.
  - If the answer is 'nothing found / all healthy', say so clearly and positively.
  - risk/priority values in recommendations MUST be exactly HIGH, MEDIUM, or LOW.
  - Return ONLY the JSON object matching the required schema.";

            var genRequest = new GenerateContentRequest
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
                    Temperature = 0.3f
                }
            };

            GenerateContentResponse response = await VertexRetry.InvokeAsync(
                () => client.GenerateContentAsync(genRequest), _logger, "MasterChat");

            string raw = response.Candidates[0].Content.Parts[0].Text;

            var parsed = JsonSerializer.Deserialize<MasterChatResponse>(raw, new JsonSerializerOptions
            {
                PropertyNameCaseInsensitive = true
            });

            return parsed ?? new MasterChatResponse
            {
                Answer = "I could not produce a structured answer for that question. Please try rephrasing.",
            };
        }

        // Converts BigQuery values to plain strings and drops null/empty fields so the
        // prompt stays compact and the model isn't distracted by empty columns.
        private static List<Dictionary<string, string>> Simplify(List<Dictionary<string, object?>> rows)
        {
            var result = new List<Dictionary<string, string>>(rows.Count);
            foreach (var row in rows)
            {
                var simple = new Dictionary<string, string>();
                foreach (var kv in row)
                {
                    var text = kv.Value?.ToString();
                    if (!string.IsNullOrWhiteSpace(text))
                    {
                        simple[kv.Key] = text;
                    }
                }
                result.Add(simple);
            }
            return result;
        }

        private static OpenApiSchema BuildResponseSchema()
        {
            var affectedSchema = new OpenApiSchema { Type = Google.Cloud.AIPlatform.V1.Type.Object };
            affectedSchema.Properties.Add("tail", new OpenApiSchema { Type = Google.Cloud.AIPlatform.V1.Type.String });
            affectedSchema.Properties.Add("flightNumber", new OpenApiSchema { Type = Google.Cloud.AIPlatform.V1.Type.String });
            affectedSchema.Properties.Add("domain", new OpenApiSchema { Type = Google.Cloud.AIPlatform.V1.Type.String });
            affectedSchema.Properties.Add("status", new OpenApiSchema { Type = Google.Cloud.AIPlatform.V1.Type.String });
            affectedSchema.Properties.Add("issue", new OpenApiSchema { Type = Google.Cloud.AIPlatform.V1.Type.String });

            var recommendationSchema = new OpenApiSchema { Type = Google.Cloud.AIPlatform.V1.Type.Object };
            recommendationSchema.Properties.Add("priority", new OpenApiSchema { Type = Google.Cloud.AIPlatform.V1.Type.String, Description = "MUST be one of 'HIGH', 'MEDIUM', or 'LOW'" });
            recommendationSchema.Properties.Add("action", new OpenApiSchema { Type = Google.Cloud.AIPlatform.V1.Type.String });
            recommendationSchema.Properties.Add("reason", new OpenApiSchema { Type = Google.Cloud.AIPlatform.V1.Type.String });
            recommendationSchema.Properties.Add("owner", new OpenApiSchema { Type = Google.Cloud.AIPlatform.V1.Type.String });
            recommendationSchema.Properties.Add("area", new OpenApiSchema { Type = Google.Cloud.AIPlatform.V1.Type.String });

            var schema = new OpenApiSchema { Type = Google.Cloud.AIPlatform.V1.Type.Object };
            schema.Properties.Add("answer", new OpenApiSchema { Type = Google.Cloud.AIPlatform.V1.Type.String });
            schema.Properties.Add("keyFindings", new OpenApiSchema
            {
                Type = Google.Cloud.AIPlatform.V1.Type.Array,
                Items = new OpenApiSchema { Type = Google.Cloud.AIPlatform.V1.Type.String }
            });
            schema.Properties.Add("affectedFlights", new OpenApiSchema
            {
                Type = Google.Cloud.AIPlatform.V1.Type.Array,
                Items = affectedSchema
            });
            schema.Properties.Add("recommendations", new OpenApiSchema
            {
                Type = Google.Cloud.AIPlatform.V1.Type.Array,
                Items = recommendationSchema
            });
            schema.Required.Add("answer");
            return schema;
        }
    }
}
