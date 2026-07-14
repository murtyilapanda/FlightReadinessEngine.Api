using System.Collections.Concurrent;
using Google.Cloud.Datastore.V1;
using Grpc.Core;

namespace FlightReadinessEngine.Api.Cache
{
    public class DatastoreCacheService : IAgentCache
    {
        private static readonly ConcurrentDictionary<string, string> LocalFallbackCache = new();
        private readonly DatastoreDb _db;
        private readonly string _kind;

        public DatastoreCacheService(string projectId, string kind = "MultiAgentCacheMatrix")
        {
            _db = new DatastoreDbBuilder
            {
                ProjectId = projectId,
                GoogleCredential = FlightReadinessEngine.Api.Services.GcpAuth.GetCredential()
            }.Build();
            _kind = kind;
        }

        public async Task<string> GetStringAsync(string key)
        {
            LocalFallbackCache.TryGetValue(key, out var localValue);

            try
            {
                Key datastoreKey = _db.CreateKeyFactory(_kind).CreateKey(key);
                Entity entity = await _db.LookupAsync(datastoreKey);

                if (entity != null && entity.Properties.ContainsKey("value"))
                {
                    string value = entity["value"].StringValue;
                    LocalFallbackCache[key] = value;
                    return value;
                }

                return localValue;
            }
            catch (RpcException ex) when (
                ex.StatusCode == StatusCode.PermissionDenied ||
                ex.StatusCode == StatusCode.Unauthenticated ||
                ex.StatusCode == StatusCode.Unavailable ||
                ex.StatusCode == StatusCode.DeadlineExceeded)
            {
                return localValue;
            }
        }

        public async Task SetStringAsync(string key, string value)
        {
            LocalFallbackCache[key] = value;

            try
            {
                Key datastoreKey = _db.CreateKeyFactory(_kind).CreateKey(key);
                Entity entity = new Entity
                {
                    Key = datastoreKey
                };

                // Datastore indexed strings are limited to 1500 bytes; cache payloads can be larger.
                entity.Properties["value"] = new Value
                {
                    StringValue = value,
                    ExcludeFromIndexes = true
                };
                entity.Properties["updatedAt"] = new Value
                {
                    StringValue = DateTime.UtcNow.ToString("o"),
                    ExcludeFromIndexes = true
                };

                await _db.UpsertAsync(entity);
            }
            catch (RpcException ex) when (
                ex.StatusCode == StatusCode.PermissionDenied ||
                ex.StatusCode == StatusCode.Unauthenticated ||
                ex.StatusCode == StatusCode.Unavailable ||
                ex.StatusCode == StatusCode.DeadlineExceeded)
            {
            }
        }
    }
}
