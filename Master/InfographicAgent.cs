using System.Net;
using System.Text.Json;
using FlightReadinessEngine.Api.Models;

namespace FlightReadinessEngine.Api.Master
{
    public class InfographicAgent
    {
        private readonly ILogger<InfographicAgent> _logger;
        private readonly OperationManageAgent _masterAgent;
        private readonly IWebHostEnvironment _environment;

        public InfographicAgent(ILogger<InfographicAgent> logger, OperationManageAgent masterAgent, IWebHostEnvironment environment)
        {
            _logger = logger;
            _masterAgent = masterAgent;
            _environment = environment;
        }

        public async Task<string> RunAsync(AgentSyncRequest? request)
        {
            _logger.LogInformation("--- [INFOGRAPHIC AGENT] Collecting orchestration data for handover rendering ---");

            object orchestrationResult = await _masterAgent.RunAsync(request);
            string html = await BuildShiftHandoverHtmlAsync(orchestrationResult);

            _logger.LogInformation("[INFOGRAPHIC AGENT] Shift handover HTML generated successfully.");

            return html;
        }

        private async Task<string> BuildShiftHandoverHtmlAsync(object orchestrationResult)
        {
            try
            {
                string templatePath = Path.Combine(_environment.ContentRootPath, "Assets", "shift-handover-template.html");
                if (!File.Exists(templatePath))
                {
                    _logger.LogWarning("[INFOGRAPHIC AGENT] Template file not found at {TemplatePath}. Returning fallback HTML.", templatePath);
                    return BuildFallbackHtml("Template file missing.");
                }

                string template = await File.ReadAllTextAsync(templatePath);
                JsonElement root = JsonSerializer.SerializeToElement(orchestrationResult);

                string overallDecision = GetRootString(root, "overallDispatchDecision", "NO_DATA");
                string summary = GetRootString(root, "orchestrationSummary", "No orchestration summary available.");
                string timestampRaw = GetRootString(root, "timestamp", DateTime.UtcNow.ToString("o"));
                string timestamp = TryFormatTimestamp(timestampRaw);

                var crew = BuildDomainSnapshot(root, "crewAssessment", "updatedCrewAssessments", "Crew", "A");
                var parts = BuildDomainSnapshot(root, "partsAssessment", "updatedPartsAssessments", "Parts", "B");
                var maintenance = BuildDomainSnapshot(root, "maintenanceAssessment", "updatedMaintenanceAssessments", "Maintenance", "C");
                var ground = BuildDomainSnapshot(root, "groundAssessment", "updatedGroundAssessments", "Ground", "D");
                var flightPlanning = BuildDomainSnapshot(root, "flightPlanningAssessment", "updatedFlightPlanningAssessments", "Flight Planning", "E");
                var aircraft = BuildDomainSnapshot(root, "aircraftAssessment", "updatedAircraftAssessments", "Aircraft", "F");

                string rendered = template
                    .Replace("{{OVERALL_DECISION}}", Html(overallDecision))
                    .Replace("{{ORCHESTRATION_SUMMARY}}", Html(summary))
                    .Replace("{{TIMESTAMP}}", Html(timestamp));

                rendered = ApplySnapshot(rendered, "CREW", crew);
                rendered = ApplySnapshot(rendered, "PARTS", parts);
                rendered = ApplySnapshot(rendered, "MAINTENANCE", maintenance);
                rendered = ApplySnapshot(rendered, "GROUND", ground);
                rendered = ApplySnapshot(rendered, "FLIGHT_PLANNING", flightPlanning);
                rendered = ApplySnapshot(rendered, "AIRCRAFT", aircraft);

                return rendered;
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[INFOGRAPHIC AGENT] HTML handover rendering failed.");
                return BuildFallbackHtml($"Rendering error: {ex.Message}");
            }
        }

        private static string ApplySnapshot(string html, string tokenPrefix, DomainSnapshot snapshot)
        {
            return html
                .Replace($"{{{{{tokenPrefix}_LETTER}}}}", Html(snapshot.Letter))
                .Replace($"{{{{{tokenPrefix}_STATUS}}}}", Html(snapshot.Status))
                .Replace($"{{{{{tokenPrefix}_METRIC}}}}", Html(snapshot.Metric))
                .Replace($"{{{{{tokenPrefix}_NOTE}}}}", Html(snapshot.Note))
                .Replace($"{{{{{tokenPrefix}_CLASS}}}}", snapshot.CssClass);
        }

        private static DomainSnapshot BuildDomainSnapshot(JsonElement root, string assessmentProperty, string updatedArrayProperty, string domainName, string letter)
        {
            if (!root.TryGetProperty(assessmentProperty, out var domainAssessment) || domainAssessment.ValueKind != JsonValueKind.Object)
            {
                return new DomainSnapshot(domainName, letter, "NO_DATA", "status-no-data", "No fresh data", "No domain payload.");
            }

            if (!domainAssessment.TryGetProperty(updatedArrayProperty, out var updates) || updates.ValueKind != JsonValueKind.Array || updates.GetArrayLength() == 0)
            {
                string msg = GetObjectString(domainAssessment, "message", "No fresh data.");
                return new DomainSnapshot(domainName, letter, "NO_DATA", "status-no-data", "No fresh data", msg);
            }

            int total = 0;
            int hold = 0;
            int cleared = 0;
            string note = "No risk notes.";

            foreach (var item in updates.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.Object)
                {
                    continue;
                }

                total++;
                string status = GetObjectString(item, "clearanceStatus", "NO_DATA");
                if (status.Equals("HOLD_DUE_TO_RISK", StringComparison.OrdinalIgnoreCase))
                {
                    hold++;
                }
                else if (status.Equals("CLEARED", StringComparison.OrdinalIgnoreCase))
                {
                    cleared++;
                }

                string risk = GetObjectString(item, "domainRiskSummary", string.Empty);
                if (!string.IsNullOrWhiteSpace(risk))
                {
                    note = risk;
                }
            }

            string finalStatus;
            string cssClass;
            if (hold > 0)
            {
                finalStatus = "HOLD_DUE_TO_RISK";
                cssClass = "status-hold";
            }
            else if (cleared > 0)
            {
                finalStatus = "CLEARED";
                cssClass = "status-cleared";
            }
            else
            {
                finalStatus = "NO_DATA";
                cssClass = "status-no-data";
            }

            string metric = total > 0
                ? $"{total} update(s): {cleared} cleared / {hold} hold"
                : "No fresh data";

            return new DomainSnapshot(domainName, letter, finalStatus, cssClass, metric, note);
        }

        private static string GetRootString(JsonElement root, string propertyName, string fallback)
        {
            if (!root.TryGetProperty(propertyName, out var value))
            {
                return fallback;
            }

            return value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? fallback
                : value.ToString();
        }

        private static string GetObjectString(JsonElement obj, string propertyName, string fallback)
        {
            if (obj.ValueKind != JsonValueKind.Object || !obj.TryGetProperty(propertyName, out var value))
            {
                return fallback;
            }

            return value.ValueKind == JsonValueKind.String
                ? value.GetString() ?? fallback
                : value.ToString();
        }

        private static string TryFormatTimestamp(string timestamp)
        {
            if (DateTimeOffset.TryParse(timestamp, out var parsed))
            {
                return parsed.ToLocalTime().ToString("yyyy-MM-dd HH:mm:ss zzz");
            }

            return timestamp;
        }

        private static string Html(string value)
        {
            return WebUtility.HtmlEncode(value ?? string.Empty);
        }

        private static string BuildFallbackHtml(string message)
        {
            string escaped = Html(message);
                        return "<!DOCTYPE html>" +
                                "<html lang=\"en\">" +
                                "<head>" +
                                "<meta charset=\"utf-8\" />" +
                                "<title>Shift Handover</title>" +
                                "<style>" +
                                "body { margin: 0; background: #0f172a; color: #e2e8f0; font-family: Segoe UI, Arial, sans-serif; }" +
                                ".wrap { width: 800px; height: 600px; margin: 0 auto; display: grid; place-items: center; }" +
                                ".card { padding: 24px; border-radius: 16px; border: 1px solid #334155; background: #111827; max-width: 680px; }" +
                                ".title { color: #f59e0b; font-size: 24px; margin: 0 0 8px; }" +
                                ".msg { color: #cbd5e1; font-size: 14px; }" +
                                "</style>" +
                                "</head>" +
                                "<body>" +
                                "<div class=\"wrap\"><div class=\"card\">" +
                                "<h1 class=\"title\">SHIFT HANDOVER TEMPLATE UNAVAILABLE</h1>" +
                                "<p class=\"msg\">" + escaped + "</p>" +
                                "</div></div>" +
                                "</body>" +
                                "</html>";
        }

        private sealed record DomainSnapshot(string Name, string Letter, string Status, string CssClass, string Metric, string Note);
    }
}
