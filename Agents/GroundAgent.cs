using System.Text.Json;
using FlightReadinessEngine.Api.Cache;
using FlightReadinessEngine.Api.Models;
using Google.Cloud.AIPlatform.V1;
using Google.Cloud.BigQuery.V2;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;

namespace FlightReadinessEngine.Api.Agents
{
    public class GroundAgent
    {
        private readonly ILogger<GroundAgent> _logger;
        private readonly IAgentCache _cache;
        private readonly string _projectId;
        private readonly string _location;
        private const string ModelId = "gemini-2.5-flash";

        public GroundAgent(ILogger<GroundAgent> logger, IAgentCache cache)
        {
            _logger = logger;
            _cache = cache;
            _projectId = Environment.GetEnvironmentVariable("GCP_PROJECT_ID") ?? "";
            _location = Environment.GetEnvironmentVariable("GCP_LOCATION") ?? "asia-south1";
        }

        public async Task<object> RunAsync(AgentSyncRequest? request)
        {
            _logger.LogInformation("--- [GROUND SUBSYSTEM ENGINE] Scanning BigQuery for Table Updates ---");

            List<string> targetFlightIds = ExtractTargetFlightIds(request);
            bool isTargetedRequest = targetFlightIds.Count > 0;
            List<string> updatedTails = targetFlightIds.Count == 0 ? await ScanTableForChanges(targetFlightIds) : targetFlightIds;

            if (!updatedTails.Any())
            {
                _logger.LogInformation("[GROUND SUBSYSTEM] No new row changes detected.");
                return new { message = "Ground ops data is already synchronized. No analysis needed." };
            }

            _logger.LogInformation($"[GROUND SUBSYSTEM] Found updates for {updatedTails.Count} aircraft tail(s): {string.Join(", ", updatedTails)}");
            var groundAssessments = new List<object>();

            foreach (var tail in updatedTails)
            {
                _logger.LogInformation($"[GROUND SUBSYSTEM] Deploying Ground Agent for Tail: {tail}...");
                string structuredAgentOutput = await ExecuteGroundAgentWithTools(tail);
                var parsedJson = JsonSerializer.Deserialize<JsonElement>(structuredAgentOutput);
                groundAssessments.Add(isTargetedRequest ? BuildTargetedResponse(tail, parsedJson) : parsedJson);
            }

            if (isTargetedRequest)
            {
                await UpdateCacheMarkersForTails(updatedTails);
            }

            return new
            {
                systemStatus = "SYNCHRONIZATION_COMPLETE",
                updatedGroundAssessments = groundAssessments
            };
        }

        private List<string> ExtractTargetFlightIds(AgentSyncRequest? payload)
        {
            if (payload == null) return new List<string>();
            string rawValue = !string.IsNullOrWhiteSpace(payload.FlightId) ? payload.FlightId : !string.IsNullOrWhiteSpace(payload.Flight) ? payload.Flight : payload.Tail ?? string.Empty;
            if (string.IsNullOrWhiteSpace(rawValue)) return new List<string>();
            return rawValue.Split(',').Select(id => id.Trim()).Where(id => !string.IsNullOrWhiteSpace(id)).Distinct().ToList();
        }

        private static object BuildTargetedResponse(string requestedTail, JsonElement assessment)
        {
            if (assessment.ValueKind != JsonValueKind.Object) return assessment;
            string clearanceStatus = GetJsonStringProperty(assessment, "clearanceStatus");
            string responseId = GetJsonStringProperty(assessment, "flightId");
            string effectiveId = string.IsNullOrWhiteSpace(responseId) ? requestedTail : responseId;
            if (string.Equals(clearanceStatus, "CLEARED", StringComparison.OrdinalIgnoreCase))
            {
                return new { flightId = effectiveId, clearanceStatus = "CLEARED", message = $"{effectiveId} is ready for dispatch. No active risks." };
            }
            return assessment;
        }

        private static string GetJsonStringProperty(JsonElement json, string propertyName)
        {
            if (json.ValueKind != JsonValueKind.Object || !json.TryGetProperty(propertyName, out var value)) return string.Empty;
            return value.ValueKind == JsonValueKind.String ? value.GetString() ?? string.Empty : value.ToString();
        }

        private async Task<List<string>> ScanTableForChanges(List<string> targetFlightIds)
        {
            BigQueryClient client = await BigQueryClient.CreateAsync(_projectId);
            var tailsWithUpdates = new List<string>();
            bool cacheUnavailable = false;

            string query;
            IEnumerable<BigQueryParameter> parameters;

            if (targetFlightIds == null || targetFlightIds.Count == 0)
            {
                query = @"SELECT tail, CAST(UNIX_MICROS(MAX(last_updated_at)) AS STRING) as latest_marker FROM `zinc-hour-460015-n7.aviation_ops_analytics.ground_analytics` GROUP BY tail";
                parameters = Enumerable.Empty<BigQueryParameter>();
            }
            else if (targetFlightIds.Count == 1)
            {
                query = @"SELECT tail, CAST(UNIX_MICROS(MAX(last_updated_at)) AS STRING) as latest_marker FROM `zinc-hour-460015-n7.aviation_ops_analytics.ground_analytics` WHERE tail = @flightId GROUP BY tail";
                parameters = new[] { new BigQueryParameter("flightId", BigQueryDbType.String, targetFlightIds[0]) };
            }
            else
            {
                query = @"SELECT tail, CAST(UNIX_MICROS(MAX(last_updated_at)) AS STRING) as latest_marker FROM `zinc-hour-460015-n7.aviation_ops_analytics.ground_analytics` WHERE tail IN UNNEST(@flightIds) GROUP BY tail";
                parameters = new[] { new BigQueryParameter("flightIds", BigQueryDbType.Array, targetFlightIds.ToArray()) };
            }

            BigQueryResults results = await client.ExecuteQueryAsync(query, parameters);

            foreach (var row in results)
            {
                string flightId = row["tail"]?.ToString() ?? "";
                string currentTimestampStr = row["latest_marker"]?.ToString() ?? "";
                if (string.IsNullOrEmpty(flightId) || string.IsNullOrEmpty(currentTimestampStr)) continue;

                string cacheKey = $"ground_agent:flight:{flightId}:last_seen";
                string cachedTimestampStr = null;
                if (!cacheUnavailable)
                {
                    try { cachedTimestampStr = await _cache.GetStringAsync(cacheKey); }
                    catch (RpcException ex) when (ex.StatusCode == Grpc.Core.StatusCode.PermissionDenied)
                    {
                        cacheUnavailable = true;
                        _logger.LogError("[GROUND SUBSYSTEM] Cache access denied.");
                    }
                }

                bool isModified = cacheUnavailable || string.IsNullOrEmpty(cachedTimestampStr) || IsMarkerNewer(currentTimestampStr, cachedTimestampStr);
                if (!isModified) continue;

                tailsWithUpdates.Add(flightId);
                if (!cacheUnavailable)
                {
                    try { await _cache.SetStringAsync(cacheKey, currentTimestampStr); }
                    catch (RpcException ex) when (ex.StatusCode == Grpc.Core.StatusCode.PermissionDenied)
                    {
                        cacheUnavailable = true;
                        _logger.LogError("[GROUND SUBSYSTEM] Cache write denied.");
                    }
                }
            }

            return tailsWithUpdates;
        }

        private static bool IsMarkerNewer(string currentMarker, string cachedMarker)
        {
            if (long.TryParse(currentMarker, out var currentMicros) && long.TryParse(cachedMarker, out var cachedMicros)) return currentMicros > cachedMicros;
            if (long.TryParse(currentMarker, out _)) return true;
            if (!DateTimeOffset.TryParse(currentMarker, out var ct)) return true;
            return DateTimeOffset.TryParse(cachedMarker, out var cc) ? ct > cc : true;
        }

        private async Task<string> ExecuteGroundAgentWithTools(string tail)
        {
            var client = await new PredictionServiceClientBuilder { Endpoint = $"{_location}-aiplatform.googleapis.com" }.BuildAsync();

            var toolParametersSchema = new OpenApiSchema { Type = Google.Cloud.AIPlatform.V1.Type.Object };
            toolParametersSchema.Properties.Add("flightId", new OpenApiSchema { Type = Google.Cloud.AIPlatform.V1.Type.String, Description = "The target aircraft tail identifier" });

            var queryTool = new Tool
            {
                FunctionDeclarations = { new FunctionDeclaration { Name = "QueryGroundTable", Description = "Queries ground_analytics for tail-level ground operations, cargo loading, and weight balance status.", Parameters = toolParametersSchema } }
            };

            var responseSchema = new OpenApiSchema { Type = Google.Cloud.AIPlatform.V1.Type.Object };
            responseSchema.Properties.Add("flightId", new OpenApiSchema { Type = Google.Cloud.AIPlatform.V1.Type.String });
            responseSchema.Properties.Add("agentDomain", new OpenApiSchema { Type = Google.Cloud.AIPlatform.V1.Type.String });
            responseSchema.Properties.Add("clearanceStatus", new OpenApiSchema { Type = Google.Cloud.AIPlatform.V1.Type.String, Description = "MUST be either 'CLEARED' or 'HOLD_DUE_TO_RISK'" });
            responseSchema.Properties.Add("domainRiskSummary", new OpenApiSchema { Type = Google.Cloud.AIPlatform.V1.Type.String });
            responseSchema.Properties.Add("mitigationSteps", new OpenApiSchema { Type = Google.Cloud.AIPlatform.V1.Type.Array, Items = new OpenApiSchema { Type = Google.Cloud.AIPlatform.V1.Type.String } });
            responseSchema.Required.Add("flightId");
            responseSchema.Required.Add("agentDomain");
            responseSchema.Required.Add("clearanceStatus");
            responseSchema.Required.Add("domainRiskSummary");
            responseSchema.Required.Add("mitigationSteps");

            var request = new GenerateContentRequest
            {
                Model = $"projects/{_projectId}/locations/{_location}/publishers/google/models/{ModelId}",
                Contents = { new Content { Role = "USER", Parts = { new Part { Text = $"You are the Ground Operations Agent. An update has occurred. Execute your table query tool for tail {tail}, review ground_ops_status, cargo_loading_complete, weight_balance_cleared, ground_ops_issue and last_updated_at, and provide your safety risk verification assessment." } } } },
                Tools = { queryTool },
                GenerationConfig = new GenerationConfig { ResponseMimeType = "application/json", ResponseSchema = responseSchema }
            };

            GenerateContentResponse response = await client.GenerateContentAsync(request);
            Part responsePart = response.Candidates[0].Content.Parts[0];

            if (responsePart.FunctionCall != null)
            {
                _logger.LogInformation($" -> [TOOL USE] Ground Agent requested execution of tool: {responsePart.FunctionCall.Name}");
                string requestedTail = responsePart.FunctionCall.Args.Fields["flightId"].StringValue;
                string tableDataJson = await TalkToGroundTable(requestedTail);

                var structuredResponseData = new Struct();
                structuredResponseData.Fields.Add("queryResult", Google.Protobuf.WellKnownTypes.Value.ForString(tableDataJson));

                var toolResponseRequest = new GenerateContentRequest
                {
                    Model = request.Model,
                    Contents =
                    {
                        request.Contents[0],
                        response.Candidates[0].Content,
                        new Content { Role = "USER", Parts = { new Part { FunctionResponse = new FunctionResponse { Name = "QueryGroundTable", Response = structuredResponseData } } } }
                    },
                    GenerationConfig = request.GenerationConfig
                };

                GenerateContentResponse finalAgentDecision = await client.GenerateContentAsync(toolResponseRequest);
                return finalAgentDecision.Candidates[0].Content.Parts[0].Text;
            }

            return responsePart.Text;
        }

        private async Task<string> TalkToGroundTable(string tail)
        {
            BigQueryClient client = await BigQueryClient.CreateAsync(_projectId);
            string query = @"SELECT tail, last_updated_at, ground_ops_status, cargo_loading_complete, weight_balance_cleared, ground_ops_issue FROM `zinc-hour-460015-n7.aviation_ops_analytics.ground_analytics` WHERE tail = @flightId ORDER BY last_updated_at DESC LIMIT 1";
            var parameters = new[] { new BigQueryParameter("flightId", BigQueryDbType.String, tail) };
            BigQueryResults rows = await client.ExecuteQueryAsync(query, parameters);
            var row = rows.FirstOrDefault();
            if (row == null) return "{}";
            return JsonSerializer.Serialize(new { tail = row["tail"]?.ToString(), last_updated = row["last_updated_at"]?.ToString(), ground_ops_status = row["ground_ops_status"]?.ToString(), cargo_loading_complete = row["cargo_loading_complete"]?.ToString(), weight_balance_cleared = row["weight_balance_cleared"]?.ToString(), ground_ops_issue = row["ground_ops_issue"]?.ToString() });
        }

        private async Task UpdateCacheMarkersForTails(List<string> tails)
        {
            try
            {
                BigQueryClient client = await BigQueryClient.CreateAsync(_projectId);
                foreach (var tail in tails)
                {
                    string query = @"SELECT CAST(UNIX_MICROS(MAX(last_updated_at)) AS STRING) as latest_marker FROM `zinc-hour-460015-n7.aviation_ops_analytics.ground_analytics` WHERE tail = @flightId";
                    var parameters = new[] { new BigQueryParameter("flightId", BigQueryDbType.String, tail) };
                    BigQueryResults rows = await client.ExecuteQueryAsync(query, parameters);
                    var row = rows.FirstOrDefault();
                    string marker = row?["latest_marker"]?.ToString() ?? "";
                    if (!string.IsNullOrEmpty(marker)) await _cache.SetStringAsync($"ground_agent:flight:{tail}:last_seen", marker);
                }
            }
            catch (Exception ex)
            {
                _logger.LogWarning($"[GROUND SUBSYSTEM] Could not update cache markers: {ex.Message}");
            }
        }
    }
}
