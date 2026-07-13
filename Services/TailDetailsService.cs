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
                }
            };
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
