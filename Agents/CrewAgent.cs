using System.Text.Json;
using FlightReadinessEngine.Api.Cache;
using FlightReadinessEngine.Api.Models;
using Google.Cloud.AIPlatform.V1;
using Google.Cloud.BigQuery.V2;
using Google.Protobuf.WellKnownTypes;
using Grpc.Core;

namespace FlightReadinessEngine.Api.Agents
{
    public class CrewAgent
    {
        private readonly ILogger<CrewAgent> _logger;
        private readonly IAgentCache _cache;
        private readonly string _projectId;
        private readonly string _location;
        private const string CachePrefix = "crew_agent";
        private const string ModelId = "gemini-2.5-flash";

        public CrewAgent(ILogger<CrewAgent> logger, IAgentCache cache)
        {
            _logger = logger;
            _cache = cache;
            _projectId = Environment.GetEnvironmentVariable("GCP_PROJECT_ID") ?? "";
            _location = Environment.GetEnvironmentVariable("GCP_VERTEX_LOCATION") ?? "us-central1";
        }

        public async Task<object> RunAsync(AgentSyncRequest? request)
        {
            _logger.LogInformation("--- [CREW SUBSYSTEM ENGINE] Scanning BigQuery for Table Updates ---");

            List<string> inputFlightIds = ExtractTargetFlightIds(request);
            bool isTargetedRequest = inputFlightIds.Count > 0;

            if (isTargetedRequest)
            {
                return await RunTargetedCacheFirstAsync(inputFlightIds);
            }

            // FIX: Always filter through the cache scanner. 
            // If targeted, it checks if those specific IDs actually have new data compared to the cache.
            List<string> updatedFlights = await ScanTableForChanges(inputFlightIds);

            if (!updatedFlights.Any())
            {
                _logger.LogInformation("[CREW SUBSYSTEM] No new row changes or cache mismatches detected.");
                return new { message = "Crew data is already completely synchronized. No analysis needed." };
            }

            _logger.LogInformation($"[CREW SUBSYSTEM] Processing updates for {updatedFlights.Count} flight(s): {string.Join(", ", updatedFlights)}");
            var crewAssessments = new List<object>();

            foreach (var flightId in updatedFlights)
            {
                _logger.LogInformation($"[CREW SUBSYSTEM] Deploying Crew Agent with native database-tool for Flight: {flightId}...");
                string structuredAgentOutput = await ExecuteCrewAgentWithTools(flightId);
                var parsedJson = JsonSerializer.Deserialize<JsonElement>(structuredAgentOutput);
                crewAssessments.Add(isTargetedRequest ? BuildTargetedResponse(flightId, parsedJson) : parsedJson);
            }

            return new
            {
                systemStatus = "SYNCHRONIZATION_COMPLETE",
                updatedCrewAssessments = crewAssessments
            };
        }

        private async Task<object> RunTargetedCacheFirstAsync(List<string> targetTails)
        {
            var latestMarkers = await GetLatestMarkersAsync(targetTails);
            var assessments = new List<object>();

            foreach (var tail in targetTails)
            {
                if (!latestMarkers.TryGetValue(tail, out var currentMarker) || string.IsNullOrWhiteSpace(currentMarker))
                {
                    assessments.Add(new
                    {
                        flightId = tail,
                        agentDomain = "CREW",
                        clearanceStatus = "NO_DATA",
                        domainRiskSummary = "No crew row found for requested tail.",
                        mitigationSteps = Array.Empty<string>()
                    });
                    continue;
                }

                string markerKey = BuildKey(tail, "last_seen_marker");
                string rowKey = BuildKey(tail, "latest_row_json");
                string assessmentKey = BuildKey(tail, "assessment_json");
                string assessmentMarkerKey = BuildKey(tail, "assessment_marker");

                string cachedAssessmentJson = await SafeGetAsync(assessmentKey);
                string cachedAssessmentMarker = await SafeGetAsync(assessmentMarkerKey);

                if (!string.IsNullOrWhiteSpace(cachedAssessmentJson) && string.Equals(currentMarker, cachedAssessmentMarker, StringComparison.Ordinal))
                {
                    assessments.Add(ParseJsonOrFallback(cachedAssessmentJson, tail));
                    continue;
                }

                string rowJson = await TalkToCrewTable(tail);
                if (!string.IsNullOrWhiteSpace(rowJson) && rowJson != "{}")
                {
                    await SafeSetAsync(rowKey, rowJson);
                }

                string assessmentJson = await EvaluateCrewFromRowAsync(tail, rowJson);
                assessments.Add(ParseJsonOrFallback(assessmentJson, tail));

                await SafeSetAsync(markerKey, currentMarker);
                await SafeSetAsync(assessmentMarkerKey, currentMarker);
                await SafeSetAsync(assessmentKey, assessmentJson);
            }

            return new
            {
                systemStatus = "SYNCHRONIZATION_COMPLETE",
                updatedCrewAssessments = assessments
            };
        }

        private List<string> ExtractTargetFlightIds(AgentSyncRequest? payload)
        {
            if (payload == null) return new List<string>();

            string rawValue = !string.IsNullOrWhiteSpace(payload.FlightId)
                ? payload.FlightId
                : !string.IsNullOrWhiteSpace(payload.Flight)
                    ? payload.Flight
                    : payload.Tail ?? string.Empty;

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

        private static object ParseJsonOrFallback(string rawJson, string tail)
        {
            if (string.IsNullOrWhiteSpace(rawJson))
            {
                return new
                {
                    flightId = tail,
                    agentDomain = "CREW",
                    clearanceStatus = "NO_DATA",
                    domainRiskSummary = "Empty assessment payload.",
                    mitigationSteps = Array.Empty<string>()
                };
            }

            try
            {
                return JsonSerializer.Deserialize<JsonElement>(rawJson);
            }
            catch
            {
                return new
                {
                    flightId = tail,
                    agentDomain = "CREW",
                    clearanceStatus = "NO_DATA",
                    domainRiskSummary = "Invalid assessment payload.",
                    mitigationSteps = Array.Empty<string>()
                };
            }
        }

        private string BuildKey(string tail, string suffix)
        {
            return $"{CachePrefix}:flight:{tail}:{suffix}";
        }

        private async Task<string> SafeGetAsync(string key)
        {
            try
            {
                return await _cache.GetStringAsync(key);
            }
            catch (Exception ex)
            {
                _logger.LogWarning(ex, "[CREW SUBSYSTEM] Cache read failed for key {CacheKey}", key);
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
                _logger.LogWarning(ex, "[CREW SUBSYSTEM] Cache write failed for key {CacheKey}", key);
            }
        }

        private async Task<Dictionary<string, string>> GetLatestMarkersAsync(List<string> targetFlightIds)
        {
            BigQueryClient client = await BigQueryClient.CreateAsync(_projectId, Services.GcpAuth.GetCredential());
            var markers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

            string query;
            IEnumerable<BigQueryParameter> parameters;

            if (targetFlightIds.Count == 1)
            {
                query = @"SELECT tail, CAST(UNIX_MICROS(MAX(last_updated_at)) AS STRING) as latest_marker FROM `qwiklabs-gcp-04-509f741dc909.aviation_ops_analytics.crew_analytics` WHERE tail = @flightId GROUP BY tail";
                parameters = new[] { new BigQueryParameter("flightId", BigQueryDbType.String, targetFlightIds[0]) };
            }
            else
            {
                query = @"SELECT tail, CAST(UNIX_MICROS(MAX(last_updated_at)) AS STRING) as latest_marker FROM `qwiklabs-gcp-04-509f741dc909.aviation_ops_analytics.crew_analytics` WHERE tail IN UNNEST(@flightIds) GROUP BY tail";
                parameters = new[] { new BigQueryParameter("flightIds", BigQueryDbType.Array, targetFlightIds.ToArray()) };
            }

            BigQueryResults results = await client.ExecuteQueryAsync(query, parameters);
            foreach (var row in results)
            {
                string tail = row["tail"]?.ToString() ?? string.Empty;
                string marker = row["latest_marker"]?.ToString() ?? string.Empty;
                if (!string.IsNullOrWhiteSpace(tail) && !string.IsNullOrWhiteSpace(marker))
                {
                    markers[tail] = marker;
                }
            }

            return markers;
        }

        private async Task<List<string>> ScanTableForChanges(List<string> targetFlightIds)
        {
            BigQueryClient client = await BigQueryClient.CreateAsync(_projectId, Services.GcpAuth.GetCredential());
            var flightsWithUpdates = new List<string>();
            bool cacheUnavailable = false;

            // FIX: A targeted request (specific tail(s)) must always return the latest agent status,
            // even if the BigQuery marker matches the cache. Only untargeted scans filter by change.
            bool isTargetedScan = targetFlightIds != null && targetFlightIds.Count > 0;

            string query;
            IEnumerable<BigQueryParameter> parameters;

            if (targetFlightIds == null || targetFlightIds.Count == 0)
            {
                query = @"SELECT tail, CAST(UNIX_MICROS(MAX(last_updated_at)) AS STRING) as latest_marker FROM `qwiklabs-gcp-04-509f741dc909.aviation_ops_analytics.crew_analytics` GROUP BY tail";
                parameters = Enumerable.Empty<BigQueryParameter>();
            }
            else if (targetFlightIds.Count == 1)
            {
                query = @"SELECT tail, CAST(UNIX_MICROS(MAX(last_updated_at)) AS STRING) as latest_marker FROM `qwiklabs-gcp-04-509f741dc909.aviation_ops_analytics.crew_analytics` WHERE tail = @flightId GROUP BY tail";
                parameters = new[] { new BigQueryParameter("flightId", BigQueryDbType.String, targetFlightIds[0]) };
            }
            else
            {
                query = @"SELECT tail, CAST(UNIX_MICROS(MAX(last_updated_at)) AS STRING) as latest_marker FROM `qwiklabs-gcp-04-509f741dc909.aviation_ops_analytics.crew_analytics` WHERE tail IN UNNEST(@flightIds) GROUP BY tail";
                parameters = new[] { new BigQueryParameter("flightIds", BigQueryDbType.Array, targetFlightIds.ToArray()) };
            }

            BigQueryResults results = await client.ExecuteQueryAsync(query, parameters);

            foreach (var row in results)
            {
                string flightId = row["tail"]?.ToString() ?? "";
                string currentTimestampStr = row["latest_marker"]?.ToString() ?? "";
                if (string.IsNullOrEmpty(flightId) || string.IsNullOrEmpty(currentTimestampStr)) continue;

                string cacheKey = $"crew_agent:flight:{flightId}:last_seen";
                string legacyCacheKey = $"crew_agent:flightId:{flightId}:last_seen";

                string cachedTimestampStr = null;
                if (!cacheUnavailable)
                {
                    try
                    {
                        cachedTimestampStr = await _cache.GetStringAsync(cacheKey);
                        if (string.IsNullOrEmpty(cachedTimestampStr))
                        {
                            cachedTimestampStr = await _cache.GetStringAsync(legacyCacheKey);
                        }
                    }
                    catch (RpcException ex) when (ex.StatusCode == Grpc.Core.StatusCode.PermissionDenied)
                    {
                        cacheUnavailable = true;
                        _logger.LogError("[CREW SUBSYSTEM] Cache access denied. Continuing without cache. Verify GCP IAM for Datastore service account.");
                    }
                }

                bool isModified = isTargetedScan || cacheUnavailable || string.IsNullOrEmpty(cachedTimestampStr) || IsMarkerNewer(currentTimestampStr, cachedTimestampStr);
                if (!isModified) continue;

                flightsWithUpdates.Add(flightId);
                if (!cacheUnavailable)
                {
                    try
                    {
                        await _cache.SetStringAsync(cacheKey, currentTimestampStr);
                    }
                    catch (RpcException ex) when (ex.StatusCode == Grpc.Core.StatusCode.PermissionDenied)
                    {
                        cacheUnavailable = true;
                        _logger.LogError("[CREW SUBSYSTEM] Cache write denied. Continuing without cache. Verify GCP IAM for Datastore service account.");
                    }
                }
            }

            return flightsWithUpdates;
        }

        private static bool IsMarkerNewer(string currentMarker, string cachedMarker)
        {
            if (long.TryParse(currentMarker, out var currentMicros))
            {
                if (long.TryParse(cachedMarker, out var cachedMicros)) return currentMicros > cachedMicros;
                return true;
            }

            if (!DateTimeOffset.TryParse(currentMarker, out var currentTimestamp)) return true;
            if (DateTimeOffset.TryParse(cachedMarker, out var cachedTimestamp)) return currentTimestamp > cachedTimestamp;
            return true;
        }

        private async Task<string> EvaluateCrewFromRowAsync(string tail, string rowJson)
        {
            var client = await new PredictionServiceClientBuilder
            {
                Endpoint = $"{_location}-aiplatform.googleapis.com",
                TokenAccessMethod = Services.GcpAuth.TokenAccessMethod
            }.BuildAsync();

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
                Contents =
                {
                    new Content
                    {
                        Role = "USER",
                        Parts =
                        {
                            new Part
                            {
                                Text =
                                    $"You are the Crew Domain Roster Agent. Analyze the row for tail {tail}. " +
                                    "Use only the supplied row data and return risk verification assessment JSON.\n\n" +
                                    "ROW DATA:\n" + rowJson
                            }
                        }
                    }
                },
                GenerationConfig = new GenerationConfig { ResponseMimeType = "application/json", ResponseSchema = responseSchema }
            };

            GenerateContentResponse response = await Services.VertexRetry.InvokeAsync(
                () => client.GenerateContentAsync(request), _logger, "Crew");

            return response.Candidates[0].Content.Parts[0].Text;
        }

        private async Task<string> ExecuteCrewAgentWithTools(string flightId)
        {
            var client = await new PredictionServiceClientBuilder
            {
                Endpoint = $"{_location}-aiplatform.googleapis.com",
                TokenAccessMethod = Services.GcpAuth.TokenAccessMethod
            }.BuildAsync();

            var toolParametersSchema = new OpenApiSchema { Type = Google.Cloud.AIPlatform.V1.Type.Object };
            toolParametersSchema.Properties.Add("flightId", new OpenApiSchema { Type = Google.Cloud.AIPlatform.V1.Type.String, Description = "The target flight identifier string (e.g. AA-104)" });

            var queryTool = new Tool
            {
                FunctionDeclarations =
                {
                    new FunctionDeclaration
                    {
                        Name = "QueryCrewTable",
                        Description = "Queries the crew_analytics table to get roster and analytics fields (tail, crew_status, crew_report_status, crew_issue) for a targeted tail.",
                        Parameters = toolParametersSchema
                    }
                }
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
                Contents =
                {
                    new Content
                    {
                        Role = "USER",
                        Parts = { new Part { Text = $"You are the Crew Domain Roster Agent. An update has occurred. Execute your table query tool for tail {flightId}, review the crew_status, crew_report_status, crew_issue and last_updated_at fields from the crew_analytics table, and provide your safety risk verification assessment." } }
                    }
                },
                Tools = { queryTool },
                GenerationConfig = new GenerationConfig { ResponseMimeType = "application/json", ResponseSchema = responseSchema }
            };

            GenerateContentResponse response = await Services.VertexRetry.InvokeAsync(
                () => client.GenerateContentAsync(request), _logger, "Crew");
            Part responsePart = response.Candidates[0].Content.Parts[0];

            if (responsePart.FunctionCall != null)
            {
                _logger.LogInformation($" -> [TOOL USE] Crew Agent requested execution of tool: {responsePart.FunctionCall.Name}");
                string requestedFlight = responsePart.FunctionCall.Args.Fields["flightId"].StringValue;

                string tableDataJson = await TalkToCrewTable(requestedFlight);

                var structuredResponseData = new Struct();
                structuredResponseData.Fields.Add("queryResult", Google.Protobuf.WellKnownTypes.Value.ForString(tableDataJson));

                var toolResponseRequest = new GenerateContentRequest
                {
                    Model = request.Model,
                    Contents =
                    {
                        request.Contents[0],
                        response.Candidates[0].Content,
                        new Content
                        {
                            Role = "USER",
                            Parts =
                            {
                                new Part
                                {
                                    FunctionResponse = new FunctionResponse
                                    {
                                        Name = "QueryCrewTable",
                                        Response = structuredResponseData
                                    }
                                }
                            }
                        }
                    },
                    GenerationConfig = request.GenerationConfig
                };

                GenerateContentResponse finalAgentDecision = await Services.VertexRetry.InvokeAsync(
                    () => client.GenerateContentAsync(toolResponseRequest), _logger, "Crew");
                return finalAgentDecision.Candidates[0].Content.Parts[0].Text;
            }

            return responsePart.Text;
        }

        private async Task<string> TalkToCrewTable(string flightId)
        {
            BigQueryClient client = await BigQueryClient.CreateAsync(_projectId, Services.GcpAuth.GetCredential());
            string query = @"SELECT tail, last_updated_at, crew_status, crew_report_status, crew_issue FROM `qwiklabs-gcp-04-509f741dc909.aviation_ops_analytics.crew_analytics` WHERE tail = @flightId ORDER BY last_updated_at DESC LIMIT 1";

            var parameters = new[] { new BigQueryParameter("flightId", BigQueryDbType.String, flightId) };
            BigQueryResults rows = await client.ExecuteQueryAsync(query, parameters);

            var row = rows.FirstOrDefault();
            if (row == null) return "{}";

            return JsonSerializer.Serialize(new
            {
                tail = row["tail"]?.ToString(),
                last_updated = row["last_updated_at"]?.ToString(),
                crew_status = row["crew_status"]?.ToString(),
                crew_report_status = row["crew_report_status"]?.ToString(),
                crew_issue = row["crew_issue"]?.ToString()
            });
        }
    }
}