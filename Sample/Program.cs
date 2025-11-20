using System;
using OpenTelemetry;
using System.Diagnostics;
using OpenTelemetry.Trace;
using OpenTelemetry.Metrics;
using OpenTelemetry.Resources;
using System.Threading;
using StackExchange.Redis;
using Amazon.S3;
using Amazon.S3.Model;
using System.Threading.Tasks;
using System.IO;
using NLog;

namespace sample
{
    class Program : IDisposable
    {
        private static readonly ActivitySource ActivitySource = new ActivitySource("Main");
        private readonly TracerProvider tracerProvider;
        private readonly MeterProvider meterProvider;
        private readonly IAmazonS3 s3Client;

        private readonly ConnectionMultiplexer redis;
        private readonly InstrumentedDatabase redisDb;
        private readonly NLog.Logger logger;
        public Program()
        {
            /*tracerProvider = Sdk.CreateTracerProviderBuilder()
                .ConfigureResource(r => r.AddService("sample"))
                .AddSource("Main")
                .AddSource("Redis")
                .AddAWSInstrumentation(opt => opt.SuppressDownstreamInstrumentation = true)
                .AddHttpClientInstrumentation()
                .AddOtlpExporter()
                .Build();

            meterProvider = Sdk.CreateMeterProviderBuilder()
                .ConfigureResource(r => r.AddService("sample"))
                .AddMeter("Redis")
                .AddAWSInstrumentation()
                .AddHttpClientInstrumentation()
                .AddRuntimeInstrumentation()
                .AddOtlpExporter((exporterOpt, metricReaderOpt) =>
                {
                    // configure exporter if needed, setting to 5 sec here for demo purposes
                    metricReaderOpt.PeriodicExportingMetricReaderOptions.ExportIntervalMilliseconds = 5000;
                })
                .Build();*/

            // initialize S3 client to local emulator
            s3Client = new AmazonS3Client("minioadmin", "minioadmin", new AmazonS3Config
            {
                ServiceURL = "http://localhost:9000",
                ForcePathStyle = true,
                UseHttp = true
            });

            redis = ConnectionMultiplexer.Connect("localhost");

            // wrap Redis database with instrumentation
            redisDb = new InstrumentedDatabase(redis.GetDatabase());

            logger = LogManager.GetLogger("Program");
            logger.Info("Program initialized");
        }

        public static int Main(string[] args)
        {
            using (var program = new Program())
            {
                var bucketName = $"my-bucket-{DateTimeOffset.Now.ToUnixTimeMilliseconds()}";
                for (int i = 0; i < 10; i++)
                {
                    var key = $"my-key-{DateTimeOffset.Now.ToUnixTimeMilliseconds()}";
                    var content = $"test data {i}";

                    using (var activity = ActivitySource.StartActivity("main"))
                    {
                        program.StoreDataAsync(bucketName, key, content).GetAwaiter().GetResult();
                        content = program.RetrieveDataAsync(bucketName, key).GetAwaiter().GetResult();
                        program.logger.Info($"Retrieved key: '{key}' content: '{content}'");
                    }
                    Thread.Sleep(1000);
                }
                // wait for metric reader to collect metrics and export
                Thread.Sleep(7000);
            }

            return 0;
        }

        private async Task StoreDataAsync(string bucketName, string key, string content)
        {
            try
            {
                await s3Client.PutBucketAsync(new PutBucketRequest
                {
                    BucketName = bucketName,
                    UseClientRegion = true,
                }).ConfigureAwait(false);
            }
            catch (BucketAlreadyOwnedByYouException)
            {
                // bucket already exists, ignore
            }

            await s3Client.PutObjectAsync(new PutObjectRequest
            {
                BucketName = bucketName,
                Key = key,
                ContentBody = content,
            }).ConfigureAwait(false);
        }

        private async Task<string> RetrieveDataAsync(string bucketName, string key)
        {
            string content = await redisDb.StringGetAsync(key).ConfigureAwait(false);
            if (content != null)
            {
                return content;
            }

            var request = new GetObjectRequest
            {
                BucketName = bucketName,
                Key = key,

            };

            using (var response = await s3Client.GetObjectAsync(request).ConfigureAwait(false))
            using (var reader = new StreamReader(response.ResponseStream))
            {
                content = await reader.ReadToEndAsync().ConfigureAwait(false);
            }

            if (content != null)
            {
                await redisDb.StringSetAsync(key, content).ConfigureAwait(false);
            }

            return content;
        }

        public void Dispose()
        {
            tracerProvider?.Dispose();
            meterProvider?.Dispose();
            s3Client?.Dispose();
            redis?.Dispose();
        }
    }
}