namespace FlightReadinessEngine.Api.Cache
{
    public interface IAgentCache
    {
        Task<string> GetStringAsync(string key);
        Task SetStringAsync(string key, string value);
    }
}
