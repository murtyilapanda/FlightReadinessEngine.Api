using Google.Cloud.BigQuery.V2;

namespace FlightReadinessEngine.Api.Services
{
    // L2 drill-down service: given a tail, fetches the matched row from each of the
    // 6 upstream domain tables and returns them as separate nodes.
    public class TailDetailsService
    {
        private readonly ILogger<TailDetailsService> _logger;
        private readonly string _projectId;
        private const string Dataset = "aviation_ops_analytics";

        public TailDetailsService(ILogger<TailDetailsService> logger)
        {
            _logger = logger;
            _projectId = Environment.GetEnvironmentVariable("GCP_PROJECT_ID") ?? "qwiklabs-gcp-04-509f741dc909";
        }

        public async Task<object> GetTailDetailsAsync(string tail)
        {
            if (string.IsNullOrWhiteSpace(tail))
            {
                return new { error = "A tail number is required." };
            }

            tail = tail.Trim();
            _logger.LogInformation($"--- [L2 DRILL-DOWN] Fetching all domain table details for tail: {tail} ---");

            BigQueryClient client = await BigQueryClient.CreateAsync(_projectId, GcpAuth.GetCredential());

            // These 6 lookups are independent, so run them concurrently instead of
            // sequentially and await them all together.
            var aircraftTask = FetchRowAsync(client, "aircraft_analytics", tail);
            var partsTask = FetchRowAsync(client, "parts_analytics", tail);
            var crewTask = FetchRowAsync(client, "crew_analytics", tail);
            var maintenanceTask = FetchRowAsync(client, "maintenance_analytics", tail);
            var groundTask = FetchRowAsync(client, "ground_analytics", tail);
            var flightPlanningTask = FetchRowAsync(client, "flight_planning_analytics", tail);

            await Task.WhenAll(
                aircraftTask, partsTask, crewTask, maintenanceTask, groundTask, flightPlanningTask);

            return new
            {
                tail,
                tailDetails = new
                {
                    aircraft = aircraftTask.Result,
                    parts = partsTask.Result,
                    crew = crewTask.Result,
                    maintenance = maintenanceTask.Result,
                    groundOps = groundTask.Result,
                    flightPlanning = flightPlanningTask.Result
                },
                // Chronological, card-ready timeline built from the fetched domain rows.
                timeline = BuildTimeline(
                    aircraftTask.Result,
                    partsTask.Result,
                    crewTask.Result,
                    maintenanceTask.Result,
                    groundTask.Result,
                    flightPlanningTask.Result)
            };
        }

        // Builds a chronological, card-friendly timeline of readiness events for the
        // tail. Each domain event is timestamped with that domain row's own
        // last_updated_at (the only per-domain time available), and the final
        // departure milestone uses aircraft.scheduled_departure.
        // Each event has the shape { time, timestamp, label, status, isNext } where
        // status is one of: done | atrisk | pending | waiting. The first event that is
        // not yet done is flagged with isNext = true so the card can highlight the
        // current step.
        private List<object> BuildTimeline(
            Dictionary<string, object?>? aircraft,
            Dictionary<string, object?>? parts,
            Dictionary<string, object?>? crew,
            Dictionary<string, object?>? maintenance,
            Dictionary<string, object?>? ground,
            Dictionary<string, object?>? flightPlanning)
        {
            var events = new List<TimelineEvent>();

            // --- APU + Fuel (aircraft) ---
            if (aircraft != null)
            {
                var acTs = GetTs(aircraft, "last_updated_at");
                if (IsTrue(Get(aircraft, "apu_is_running")))
                {
                    var runtime = Get(aircraft, "apu_runtime_mins");
                    events.Add(new TimelineEvent(acTs,
                        runtime != null ? $"APU Running ({runtime} min)" : "APU Running",
                        IsTrue(Get(aircraft, "apu_within_guidelines")) ? "done" : "atrisk"));
                }

                var fuelStatus = Get(aircraft, "fuel_ticket_status")?.ToString();
                events.Add(Eq(fuelStatus, "Approved")
                    ? new TimelineEvent(acTs, "Fuel Ticket Approved", "done")
                    : new TimelineEvent(acTs, $"Fuel {fuelStatus ?? "Pending"}", "pending"));
            }

            // --- Parts (parts) ---
            if (parts != null && IsTrue(Get(parts, "part_required")))
            {
                var partsTs = GetTs(parts, "last_updated_at");
                if (Eq(Get(parts, "part_available")?.ToString(), "Available"))
                    events.Add(new TimelineEvent(partsTs, "Part Available", "done"));
                else if (IsTrue(Get(parts, "part_in_transit")))
                    events.Add(new TimelineEvent(partsTs, "Part In Transit", "pending"));
                else
                    events.Add(new TimelineEvent(partsTs, "Part Required (Unavailable)", "atrisk"));
            }

            // --- Maintenance (maintenance) ---
            if (maintenance != null)
            {
                var maintTs = GetTs(maintenance, "last_updated_at");
                var woStatus = Get(maintenance, "work_order_status")?.ToString();
                events.Add(Eq(woStatus, "Closed") || Eq(woStatus, "Complete")
                    ? new TimelineEvent(maintTs, "Maintenance Complete", "done")
                    : new TimelineEvent(maintTs,
                        $"Maintenance: {Get(maintenance, "maintenance_status") ?? woStatus ?? "Open"}",
                        "pending"));
            }

            // --- Flight Plan + Dispatch (flight planning) ---
            if (flightPlanning != null)
            {
                var fpTs = GetTs(flightPlanning, "last_updated_at");
                var fpStatus = Get(flightPlanning, "flight_plan_status")?.ToString();
                events.Add(Eq(fpStatus, "Filed") || Eq(fpStatus, "Released")
                    ? new TimelineEvent(fpTs, "Flight Plan Filed", "done")
                    : new TimelineEvent(fpTs, $"Flight Plan {fpStatus ?? "Not Filed"}", "atrisk"));

                events.Add(Eq(fpStatus, "Released")
                    ? new TimelineEvent(fpTs, "Dispatch Released", "done")
                    : new TimelineEvent(fpTs, "Dispatch Release Pending", "waiting"));
            }

            // --- Crew (crew) ---
            if (crew != null)
            {
                var crewTs = GetTs(crew, "last_updated_at");
                var reported = Eq(Get(crew, "crew_report_status")?.ToString(), "Reported");
                events.Add(new TimelineEvent(crewTs,
                    reported ? "Crew Reported" : $"Crew: {Get(crew, "crew_report_status") ?? "Unknown"}",
                    reported ? "done" : "pending"));
            }

            // --- Ground Ops (ground) ---
            if (ground != null)
            {
                var groundTs = GetTs(ground, "last_updated_at");
                var complete = IsTrue(Get(ground, "cargo_loading_complete"))
                    && IsTrue(Get(ground, "weight_balance_cleared"));
                events.Add(complete
                    ? new TimelineEvent(groundTs, "Ground Ops Complete", "done")
                    : new TimelineEvent(groundTs,
                        $"Ground Ops: {Get(ground, "ground_ops_status") ?? "In Progress"}",
                        "pending"));
            }

            // --- Scheduled Departure (aircraft) ---
            events.Add(new TimelineEvent(GetTs(aircraft, "scheduled_departure"), "Scheduled Departure", "done"));

            // Sort chronologically (oldest first). Events without a timestamp fall to the end.
            var ordered = events
                .OrderBy(e => e.timestamp ?? DateTimeOffset.MaxValue)
                .ToList();

            // Flag the first not-yet-done event as the current step.
            var nextIndex = ordered.FindIndex(e => e.status != "done");

            return ordered
                .Select((e, i) => (object)new
                {
                    time = e.timestamp?.ToString("HH:mm") ?? "--:--",
                    timestamp = e.timestamp?.ToString("o"),
                    e.label,
                    e.status,
                    isNext = i == nextIndex
                })
                .ToList();
        }

        private readonly record struct TimelineEvent(DateTimeOffset? timestamp, string label, string status);

        // ----- small helpers for reading/formatting BigQuery values -----

        private static object? Get(Dictionary<string, object?>? row, string key)
            => row != null && row.TryGetValue(key, out var v) ? v : null;

        private static bool Eq(string? value, string expected)
            => value != null && string.Equals(value, expected, StringComparison.OrdinalIgnoreCase);

        private static bool IsTrue(object? value)
        {
            if (value == null) return false;
            if (value is bool b) return b;
            var s = value.ToString();
            return string.Equals(s, "true", StringComparison.OrdinalIgnoreCase)
                || string.Equals(s, "yes", StringComparison.OrdinalIgnoreCase)
                || string.Equals(s, "1", StringComparison.OrdinalIgnoreCase);
        }

        // Reads a timestamp column and returns it as a nullable DateTimeOffset.
        private static DateTimeOffset? GetTs(Dictionary<string, object?>? row, string key)
        {
            var value = Get(row, key);
            if (value == null) return null;
            if (value is DateTime dt) return new DateTimeOffset(DateTime.SpecifyKind(dt, DateTimeKind.Utc));
            if (value is DateTimeOffset dto) return dto;
            if (DateTimeOffset.TryParse(value.ToString(), out var parsed)) return parsed;
            return null;
        }

        // Fetches the most recent row for the tail from a given domain table.
        private async Task<Dictionary<string, object?>?> FetchRowAsync(
            BigQueryClient client, string tableName, string tail)
        {
            try
            {
                string query = $@"
                    SELECT *
                    FROM `{_projectId}.{Dataset}.{tableName}`
                    WHERE tail = @tail
                    ORDER BY last_updated_at DESC
                    LIMIT 1";

                var parameters = new[] { new BigQueryParameter("tail", BigQueryDbType.String, tail) };
                BigQueryResults results = await client.ExecuteQueryAsync(query, parameters);

                var row = results.FirstOrDefault();
                if (row == null) return null;

                var record = new Dictionary<string, object?>();
                foreach (var field in row.Schema.Fields)
                {
                    record[field.Name] = row[field.Name];
                }
                return record;
            }
            catch (Exception ex)
            {
                _logger.LogError($"[L2 DRILL-DOWN] Failed to fetch {tableName} for tail {tail}: {ex.Message}");
                return null;
            }
        }
    }
}
