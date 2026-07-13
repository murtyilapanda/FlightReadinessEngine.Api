using Google.Cloud.AIPlatform.V1;

namespace FlightReadinessEngine.Api.Services
{
    // Shared retry helper for Vertex AI (Gemini) calls. Retries on
    // ResourceExhausted (HTTP 429 quota) with exponential backoff so that
    // every agent - not just the master orchestrator - survives transient
    // quota pressure instead of crashing the request.
    public static class VertexRetry
    {
        public static async Task<GenerateContentResponse> InvokeAsync(
            Func<Task<GenerateContentResponse>> call,
            ILogger logger,
            string context = "Vertex AI",
            int maxAttempts = 5)
        {
            for (int attempt = 1; ; attempt++)
            {
                try
                {
                    return await call();
                }
                catch (Grpc.Core.RpcException ex) when (
                    ex.StatusCode == Grpc.Core.StatusCode.ResourceExhausted && attempt < maxAttempts)
                {
                    int delayMs = (int)(Math.Pow(2, attempt - 1) * 1000);
                    logger.LogWarning(
                        "[{Context}] Vertex AI quota hit (429). Retry {Attempt}/{Max} in {Delay}ms...",
                        context, attempt, maxAttempts - 1, delayMs);
                    await Task.Delay(delayMs);
                }
            }
        }
    }
}
