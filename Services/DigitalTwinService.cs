using System.Text;
using System.Text.Json;

namespace FlightReadinessEngine.Api.Services
{
    /// <summary>
    /// Service for updating Azure Digital Twin models
    /// </summary>
    public interface IDigitalTwinService
    {
        Task UpdatePartsStatusAsync(string status);
    }

    public class DigitalTwinService : IDigitalTwinService
    {
        private readonly ILogger<DigitalTwinService> _logger;
        private readonly HttpClient _httpClient;
        private const string DigitalTwinBaseUrl = "https://airlinedigitaltwin-h2h9dshpg6dtbqam.canadacentral-01.azurewebsites.net/api/twins";
        private const string PartsTwinId = "Parts";

        public DigitalTwinService(ILogger<DigitalTwinService> logger, IHttpClientFactory httpClientFactory)
        {
            _logger = logger;
            _httpClient = httpClientFactory.CreateClient("DigitalTwin");
        }

        /// <summary>
        /// Updates the Parts digital twin status and part availability
        /// </summary>
        /// <param name="status">Status value (e.g., "GREEN", "RED")</param>
        /// <param name="partAvailable">Part availability value (e.g., "Required", "Available")</param>
        public async Task UpdatePartsStatusAsync(string status)
        {
            try
            {
                var operations = new
                {
                    operations = new[]
                    {
                        new
                        {
                            op = "replace",
                            path = "/status",
                            value = status
                        }
                    }
                };

                var jsonContent = JsonSerializer.Serialize(operations);
                var content = new StringContent(jsonContent, Encoding.UTF8, "application/json");

                var url = $"{DigitalTwinBaseUrl}/{PartsTwinId}";
                
                _logger.LogInformation("[DIGITAL TWIN] Updating Parts twin - Status: {Status}, PartAvailable: {PartAvailable}", 
                    status);

                var response = await _httpClient.PatchAsync(url, content);

                if (response.IsSuccessStatusCode)
                {
                    _logger.LogInformation("[DIGITAL TWIN] Parts twin updated successfully");
                }
                else
                {
                    var errorContent = await response.Content.ReadAsStringAsync();
                    _logger.LogWarning("[DIGITAL TWIN] Failed to update Parts twin. Status: {StatusCode}, Error: {Error}", 
                        response.StatusCode, errorContent);
                }
            }
            catch (Exception ex)
            {
                _logger.LogError(ex, "[DIGITAL TWIN] Error updating Parts twin");
            }
        }
    }
}
