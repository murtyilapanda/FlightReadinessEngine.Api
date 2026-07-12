namespace FlightReadinessEngine.Api.Models
{
    public sealed class AgentSyncRequest
    {
        public string? FlightId { get; set; }
        public string? Flight { get; set; }
        public string? Tail { get; set; }
    }

    public sealed class CascadeImpactRequest
    {
        // Accepts a single tail or a comma-separated list of tails (e.g. "N449UP" or "N449UP,N108UP").
        public string? Tail { get; set; }
        public string? Tails { get; set; }
    }
}
