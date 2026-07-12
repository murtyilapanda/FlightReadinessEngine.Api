using Google.Cloud.BigQuery.V2;

namespace FlightReadinessEngine.Api.Services
{
    public class FlightService
    {
        private readonly string _projectId;

        public FlightService()
        {
            _projectId = Environment.GetEnvironmentVariable("GCP_PROJECT_ID") ?? "zinc-hour-460015-n7";
        }

        public async Task<List<Dictionary<string, object?>>> GetFleetReadinessSnapshotAsync()
        {
            BigQueryClient client = await BigQueryClient.CreateAsync(_projectId);

            string query = $@"
                SELECT 
                    tail, fleet_type, current_airport, scheduled_departure,
                    fuel_ticket_status, fuel_ticket_number, apu_is_running, apu_runtime_mins,
                    apu_within_guidelines, maintenance_status, work_order_status, maintenance_issue,
                    parts_status, part_required, part_available, part_in_transit, parts_issue,
                    crew_status, crew_report_status, crew_issue, ground_ops_status,
                    cargo_loading_complete, weight_balance_cleared, ground_ops_issue,
                    flight_plan_status, flight_number, flight_plan_issue, last_updated_at
                FROM `{_projectId}.aviation_ops_analytics.fleet_readiness_snapshot`
                ORDER BY scheduled_departure ASC";

            BigQueryResults results = await client.ExecuteQueryAsync(query, Enumerable.Empty<BigQueryParameter>());
            var snapshotList = new List<Dictionary<string, object?>>();

            foreach (var row in results)
            {
                var record = new Dictionary<string, object?>();
                foreach (var field in row.Schema.Fields)
                {
                    record[field.Name] = row[field.Name];
                }

                snapshotList.Add(record);
            }

            return snapshotList;
        }
    }
}
