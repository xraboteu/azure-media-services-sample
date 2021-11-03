using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Azure.Storage.Blobs;
using Microsoft.Azure.Management.Media;
using Microsoft.Azure.Management.Media.Models;
using Microsoft.Extensions.Configuration;
using Microsoft.IdentityModel.Clients.ActiveDirectory;
using Microsoft.Rest;
using Microsoft.Rest.Azure.Authentication;
using Microsoft.VisualBasic;

namespace media_services_sample
{
    public class Program
    {
        private static IAzureMediaServicesClient _client;
        public static async Task Main(string[] args)
        {
            var config = new ConfigWrapper(new ConfigurationBuilder()
                .SetBasePath(Directory.GetCurrentDirectory())
                .AddJsonFile("appsettings.json", optional: true, reloadOnChange: true)
                .AddEnvironmentVariables()
                .Build());

            _client = await CreateMediaServicesClientAsync(config);
            Console.WriteLine("connected");

            var uniqueness = Guid.NewGuid().ToString("N");
            var jobName = $"job-{uniqueness}";
            var outputAssetName = $"output-{uniqueness}";
            var inputAssetName = $"input-{uniqueness}";
            var locatorName = $"locator-{uniqueness}";

            var assetInput = await CreateInputAssetAsync(config.ResourceGroup, config.AccountName, inputAssetName,
                    config.FileToUpload);

            var assetOutput = await CreateOutputAssetAsync(config.ResourceGroup, config.AccountName, outputAssetName);

            var transformName = Guid.NewGuid().ToString();

            _ = await GetOrCreateTransformAsync(config.ResourceGroup, config.AccountName, transformName);

            _ = await SubmitJobAsync(config.ResourceGroup, config.AccountName, transformName, jobName,
                assetInput.Name, assetOutput.Name);

            var job = await WaitForJobToFinishAsync(config.ResourceGroup, config.AccountName, transformName, jobName);

            if (job.State == JobState.Finished) {
                Console.WriteLine("Job finished.");
            }
            

            var locator = await CreateStreamingLocatorAsync(config.ResourceGroup, config.AccountName, assetOutput.Name, locatorName);


            var urls = await GetStreamingUrlsAsync(config.ResourceGroup, config.AccountName, locator.Name);
            foreach (var url in urls) {
                Console.WriteLine(url);
            }


            //Console.WriteLine("Cleaning up...");

            //await CleanUpAsync(config.ResourceGroup, config.AccountName, transformName, job.Name, new List<string> { assetOutput.Name },
            //    null);

            Console.WriteLine("Press Enter to continue.");
            Console.ReadLine();
        }

        private static async Task<Asset> CreateInputAssetAsync(
            string resourceGroupName,
            string accountName,
            string assetName,
            string fileToUpload)
        {

            var asset = await _client.Assets.CreateOrUpdateAsync(resourceGroupName, accountName, assetName, new Asset());

            var response = await _client.Assets.ListContainerSasAsync(
                resourceGroupName,
                accountName,
                assetName,
                permissions: AssetContainerPermission.ReadWrite,
                expiryTime: DateTime.UtcNow.AddHours(4).ToUniversalTime());

            var sasUri = new Uri(response.AssetContainerSasUrls.First());

            var container = new BlobContainerClient(sasUri);
            var blob = container.GetBlobClient(Path.GetFileName(fileToUpload));

            await blob.UploadAsync(fileToUpload);

            return asset;
        }

        private static async Task<Asset> CreateOutputAssetAsync(string resourceGroupName, string accountName, string assetName)
        {
            // Check if an Asset already exists
            var outputAsset = await _client.Assets.GetAsync(resourceGroupName, accountName, assetName);
            var asset = new Asset();
            var outputAssetName = assetName;

            if (outputAsset != null) {
                // Name collision! In order to get the sample to work, let's just go ahead and create a unique asset name
                // Note that the returned Asset can have a different name than the one specified as an input parameter.
                // You may want to update this part to throw an Exception instead, and handle name collisions differently.
                var uniqueness = $"-{Guid.NewGuid():N}";
                outputAssetName += uniqueness;

                Console.WriteLine("Warning – found an existing Asset with name = " + assetName);
                Console.WriteLine("Creating an Asset with this name instead: " + outputAssetName);
            }

            return await _client.Assets.CreateOrUpdateAsync(resourceGroupName, accountName, outputAssetName, asset);
        }

        private static async Task<Transform> GetOrCreateTransformAsync(
            string resourceGroupName,
            string accountName,
            string transformName)
        {
            // Does a Transform already exist with the desired name? Assume that an existing Transform with the desired name
            // also uses the same recipe or Preset for processing content.
            var transform = await _client.Transforms.GetAsync(resourceGroupName, accountName, transformName);

            if (transform == null) {
                // You need to specify what you want it to produce as an output
                var output = new TransformOutput[]
                {
                    new TransformOutput
                    {
                        // The preset for the Transform is set to one of Media Services built-in sample presets.
                        // You can  customize the encoding settings by changing this to use "StandardEncoderPreset" class.
                        Preset = new BuiltInStandardEncoderPreset()
                        {
                            // This sample uses the built-in encoding preset for Adaptive Bitrate Streaming.
                            PresetName = EncoderNamedPreset.AdaptiveStreaming
                        }
                    }
                };

                // Create the Transform with the output defined above
                transform = await _client.Transforms.CreateOrUpdateAsync(resourceGroupName, accountName, transformName, output);
            }

            return transform;
        }
        private static async Task<Job> WaitForJobToFinishAsync(string resourceGroupName,
            string accountName,
            string transformName,
            string jobName)
        {
            const int SleepIntervalMs = 20 * 1000;

            Job job;
            do {
                job = await _client.Jobs.GetAsync(resourceGroupName, accountName, transformName, jobName);

                Console.WriteLine($"Job is '{job.State}'.");
                for (var i = 0; i < job.Outputs.Count; i++) {
                    var output = job.Outputs[i];
                    Console.Write($"\tJobOutput[{i}] is '{output.State}'.");
                    if (output.State == JobState.Processing) {
                        Console.Write($"  Progress (%): '{output.Progress}'.");
                    }

                    Console.WriteLine();
                }

                if (job.State != JobState.Finished && job.State != JobState.Error && job.State != JobState.Canceled) {
                    await Task.Delay(SleepIntervalMs);
                }
            }
            while (job.State != JobState.Finished && job.State != JobState.Error && job.State != JobState.Canceled);

            return job;
        }
        private static async Task<Job> SubmitJobAsync(
            string resourceGroupName,
            string accountName,
            string transformName,
            string jobName,
            string inputAssetName,
            string outputAssetName)
        {
            JobInput jobInput = new JobInputAsset(assetName: inputAssetName);

            JobOutput[] jobOutputs =
            {
                new JobOutputAsset(outputAssetName),
            };

            var job = await _client.Jobs.GetAsync(resourceGroupName, accountName, transformName, jobName) ?? await _client.Jobs.CreateAsync(
                resourceGroupName,
                accountName,
                transformName,
                jobName,
                new Job { Input = jobInput, Outputs = jobOutputs, });

            return job;
        }

        /// <summary>
        /// Deletes the jobs, assets and potentially the content key policy that were created.
        /// Generally, you should clean up everything except objects 
        /// that you are planning to reuse (typically, you will reuse Transforms, and you will persist output assets and StreamingLocators).
        /// </summary>
        /// <param name="client"></param>
        /// <param name="resourceGroupName"></param>
        /// <param name="accountName"></param>
        /// <param name="transformName"></param>
        /// <param name="jobName"></param>
        /// <param name="assetNames"></param>
        /// <param name="contentKeyPolicyName"></param>
        /// <returns></returns>
        // <CleanUp>
        private static async Task CleanUpAsync(
            string resourceGroupName,
            string accountName,
            string transformName,
            string jobName,
            List<string> assetNames,
            string contentKeyPolicyName = null
        )
        {
            await _client.Jobs.DeleteAsync(resourceGroupName, accountName, transformName, jobName);

            foreach (var assetName in assetNames) {
                await _client.Assets.DeleteAsync(resourceGroupName, accountName, assetName);
            }

            if (contentKeyPolicyName != null) {
                await _client.ContentKeyPolicies.DeleteAsync(resourceGroupName, accountName, contentKeyPolicyName);
            }
        }

        /// <summary>
        /// Creates a StreamingLocator for the specified asset and with the specified streaming policy name.
        /// Once the StreamingLocator is created the output asset is available to clients for playback.
        /// </summary>
        /// <param name="resourceGroup"></param>
        /// <param name="accountName"> The Media Services account name.</param>
        /// <param name="assetName">The name of the output asset.</param>
        /// <param name="locatorName">The StreamingLocator name (unique in this case).</param>
        /// <returns></returns>
        // <CreateStreamingLocator>
        private static async Task<StreamingLocator> CreateStreamingLocatorAsync(
            string resourceGroup,
            string accountName,
            string assetName,
            string locatorName)
        {
            var locator = await _client.StreamingLocators.CreateAsync(
                resourceGroup,
                accountName,
                locatorName,
                new StreamingLocator {
                    AssetName = assetName,
                    StreamingPolicyName = PredefinedStreamingPolicy.ClearStreamingOnly
                });

            return locator;
        }

        private static async Task<IList<string>> GetStreamingUrlsAsync(
            string resourceGroupName,
            string accountName,
            string locatorName)
        {
            const string defaultStreamingEndpointName = "default";

            var streamingEndpoint = await _client.StreamingEndpoints.GetAsync(resourceGroupName, accountName, defaultStreamingEndpointName);

            if (streamingEndpoint != null) {
                if (streamingEndpoint.ResourceState != StreamingEndpointResourceState.Running) {
                    await _client.StreamingEndpoints.StartAsync(resourceGroupName, accountName, defaultStreamingEndpointName);
                }
            }

            var paths = await _client.StreamingLocators.ListPathsAsync(resourceGroupName, accountName, locatorName);

            return paths.StreamingPaths
                .Select(path => new UriBuilder { Scheme = "https", Host = streamingEndpoint.HostName, Path = path.Paths[0] })
                .Select(uriBuilder => uriBuilder.ToString())
                .ToList();
        }

        private static async Task<IAzureMediaServicesClient> CreateMediaServicesClientAsync(ConfigWrapper config)
        {
            var clientCredential = new ClientCredential(config.AadClientId, config.AadSecret);
            var credentials = await ApplicationTokenProvider.LoginSilentAsync(config.AadTenantId, clientCredential,
                ActiveDirectoryServiceSettings.Azure);

            return new AzureMediaServicesClient(config.ArmEndpoint, credentials) {
                SubscriptionId = config.SubscriptionId,
            };
        }

    }
}