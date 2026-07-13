using Google.Apis.Auth.OAuth2;

namespace FlightReadinessEngine.Api.Services
{
    /// <summary>
    /// Central, process-wide GCP authentication holder.
    /// A single access token (or ADC fallback) is consumed by every
    /// Vertex AI, BigQuery, Datastore and Google.GenAI client in the project.
    /// </summary>
    public static class GcpAuth
    {
        private static string? _accessToken;

        /// <summary>Initialized once at startup from Program.cs.</summary>
        public static void Initialize(string? accessToken)
        {
            _accessToken = string.IsNullOrWhiteSpace(accessToken) ? null : accessToken.Trim();
        }

        /// <summary>True when an explicit access token was supplied.</summary>
        public static bool HasToken => !string.IsNullOrWhiteSpace(_accessToken);

        /// <summary>
        /// For PredictionServiceClientBuilder.TokenAccessMethod.
        /// Returns null when no token is set so the builder uses ADC automatically.
        /// </summary>
        public static Func<string, System.Threading.CancellationToken, Task<string>>? TokenAccessMethod =>
            HasToken ? (uri, cancellationToken) => Task.FromResult(_accessToken!) : null;

        /// <summary>
        /// A GoogleCredential for BigQuery / Datastore / Google.GenAI.
        /// Returns the ADC credential when no explicit token is present.
        /// </summary>
        public static GoogleCredential GetCredential()
        {
            return HasToken
                ? GoogleCredential.FromAccessToken(_accessToken)
                : GoogleCredential.GetApplicationDefault();
        }
    }
}
