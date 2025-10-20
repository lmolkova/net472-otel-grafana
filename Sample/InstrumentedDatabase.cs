using System;
using System.Diagnostics;
using System.Net;
using System.Diagnostics.Metrics;
using System.Threading.Tasks;
using System.Collections.Generic;
using StackExchange.Redis;
namespace sample
{

    // A simple wrapper around StackExchange.Redis IDatabase to instrument a few Redis commands (GET and SET) with OpenTelemetry.
    // Any other commands can be added similarly as needed.
    internal class InstrumentedDatabase
    {
        private static readonly ActivitySource RedisSource = new ActivitySource("Redis");
        private static readonly Meter RedisMeter = new Meter("Redis");

        // following https://github.com/open-telemetry/semantic-conventions/blob/main/docs/database/database-metrics.md#metric-dbclientoperationduration
        private static readonly Histogram<double> DbOperationDuration = RedisMeter.CreateHistogram<double>(
            "db.client.operation.duration",
            "s",
            "Duration of Redis operation",
            advice: new InstrumentAdvice<double> { HistogramBucketBoundaries = new[] { 0.001, 0.005, 0.01, 0.05, 0.1, 0.5, 1, 5, 10 } });
        private readonly IDatabase _inner;
        private readonly string _serverAddress = null;
        private readonly int _serverPort = -1;

        public InstrumentedDatabase(IDatabase inner)
        {
            _inner = inner;

            // It's possible to capture actual per-command server endpoint when using Profiling API
            // as in https://github.com/open-telemetry/opentelemetry-dotnet-contrib/blob/8b5340d4b477d0ace861c5c709746fc22909dab4/src/OpenTelemetry.Instrumentation.StackExchangeRedis/Implementation/RedisProfilerEntryToActivityConverter.cs#L166,
            // In this example, we'll only capture it when connecting to a single endpoint and it's an IPEndPoint
            if (inner.Multiplexer.GetEndPoints().Length == 1)
            {
                var endpoint = inner.Multiplexer.GetEndPoints()[0] as DnsEndPoint;
                if (endpoint != null)
                {
                    _serverAddress = endpoint.Host;
                    _serverPort = endpoint.Port;
                }
            }
        }

        public async Task<RedisValue> StringGetAsync(string key)
        {
            if (!RedisSource.HasListeners() && !DbOperationDuration.Enabled)
            {
                // no need to create activity or measure duration if there are no listeners/subscribers
                return await _inner.StringGetAsync(key).ConfigureAwait(false);
            }

            return await InstrumentOperation("GET", () => _inner.StringGetAsync(key)).ConfigureAwait(false);
        }

        public async Task<bool> StringSetAsync(string key, string value)
        {
            if (!RedisSource.HasListeners() && !DbOperationDuration.Enabled)
            {
                // no need to create activity or measure duration if there are no listeners/subscribers
                return await _inner.StringSetAsync(key, value).ConfigureAwait(false);
            }

            return await InstrumentOperation("SET", () => _inner.StringSetAsync(key, value)).ConfigureAwait(false);
        }

        private async Task<T> InstrumentOperation<T>(string operationName, Func<Task<T>> operation)
        {
            var duration = Stopwatch.StartNew();
            var commonTags = GetStartTags(operationName);
            var activity = RedisSource.StartActivity(operationName, ActivityKind.Client, default(ActivityContext), commonTags);
            string errorType = null;
            try
            {
                return await operation().ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                errorType = ex.GetType().FullName;
                activity?.SetTag("error.type", errorType);
                activity?.SetStatus(ActivityStatusCode.Error, ex.Message);
                throw;
            }
            finally
            {
                RecordCommandDuration(duration.Elapsed, commonTags, errorType);
                activity?.Stop();
            }
        }

        private List<KeyValuePair<string, object>> GetStartTags(string operationName)
        {
            // following sampling-relevant attributes in https://github.com/open-telemetry/semantic-conventions/blob/main/docs/database/redis.md
            // which are the same as metric attributes in this case https://github.com/open-telemetry/semantic-conventions/blob/main/docs/database/database-metrics.md#metric-dbclientoperationduration
            var tags = new List<KeyValuePair<string, object>>();
            tags.Add(new KeyValuePair<string, object>("db.system.name", "redis"));
            tags.Add(new KeyValuePair<string, object>("db.operation.name", operationName));
            tags.Add(new KeyValuePair<string, object>("db.namespace", _inner.Database));
            // we could also report db.query.text attribute on spans, but for trivial GET and SET commands it's not really helpful
            if (_serverAddress != null)
            {
                tags.Add(new KeyValuePair<string, object>("server.address", _serverAddress));
                tags.Add(new KeyValuePair<string, object>("server.port", _serverPort));
            }
            return tags;
        }

        private void RecordCommandDuration(TimeSpan operationDuration, IEnumerable<KeyValuePair<string, object>> commonTags, string errorType = null)
        {
            var tagList = new TagList();
            foreach (var tag in commonTags)
            {
                tagList.Add(tag.Key, tag.Value);
            }
            if (errorType != null)
            {
                tagList.Add("error.type", errorType);
            }
            DbOperationDuration.Record(operationDuration.TotalSeconds, tagList);
        }
    }
}