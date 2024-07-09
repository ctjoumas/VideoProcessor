namespace VideoProcessor
{
    using Azure.Storage.Blobs;
    using Azure.Storage.Blobs.Models;
    using Azure.Storage.Sas;
    using Microsoft.AspNetCore.Http;
    using Microsoft.Azure.Functions.Worker;
    //using Microsoft.Azure.WebJobs;
    using Microsoft.Extensions.Logging;
    using System.Text.Json.Serialization;
    using System.Web;

    public class VideoProcessorFunction
    {
        private const string AzureResourceManager = "https://management.azure.com";

        private const string ApiUrl = "https://api.videoindexer.ai";

        // Connection string to the storage account
        private static string StorageAccountConnectionString = Environment.GetEnvironmentVariable("AzureWebJobsStorage");

        private static string ContainerName = Environment.GetEnvironmentVariable("ContainerName");

        private static string FunctionCallbackUrl = Environment.GetEnvironmentVariable("FunctionCallbackUrl");

        /// <summary>
        /// This is the callback URL that Video Indexer posts to where we can get the Video ID. We use this in order to avoid having
        /// to continously poll Video Indexer after uploading a video to determine when it has finished processing.
        /// </summary>
        /// <param name="req">POST request from Video Indexer</param>
        /// <param name="log">Logger</param>
        [Function("GetVideoStatus")]
        public static async Task ReceiveVideoIndexerStateUpdate([HttpTrigger(AuthorizationLevel.Function, "post")] HttpRequest req, ILogger log)
        {
            log.LogInformation($"Received Video Indexer status update - Video ID: {req.Query["id"]} \t Processing State: {req.Query["state"]}");

            // If video is processed
            if (req.Query["state"].Equals(ProcessingState.Processed.ToString()))
            {
                await GetVideoCaptions(req.Query["id"], log);
            }
            else if (req.Query["state"].Equals(ProcessingState.Failed.ToString()))
            {
                log.LogInformation($"\nThe video index failed for video ID {req.Query["id"]}.");
            }
        }


        [Function("VideoUploadTrigger")]
        public static async Task Run([BlobTrigger("videos/{name}", Connection = "AzureWebJobsStorage")] Stream videoBlob, string name, Uri uri, ILogger log, BlobProperties properties)
        {
            log.LogInformation("C# HTTP trigger function processed a request.");

            await ProcessBlobTrigger(name, log);
        }

        /// <summary>
        /// Processes the uploaded blob (video) by pulling it from Azure Storage and uploading it to the Video Indexer.
        /// </summary>
        /// <param name="name">The name of the video / blob</param>
        /// <param name="log"></param>
        /// <returns></returns>
        private static async Task ProcessBlobTrigger(string name, ILogger log)
        {
            BlobClient blobClient = new BlobClient(StorageAccountConnectionString, ContainerName, name);

            BlobSasBuilder sasBuilder = new BlobSasBuilder()
            {
                BlobContainerName = ContainerName,
                BlobName = name,
                Resource = "b",
            };

            sasBuilder.ExpiresOn = DateTimeOffset.UtcNow.AddDays(1);
            sasBuilder.SetPermissions(BlobSasPermissions.Read);

            Uri uri = blobClient.GenerateSasUri(sasBuilder);

            log.LogInformation($"SAS URI for blob is {uri}");

            // Create the http client
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = false
            };

            var client = new HttpClient(handler);

            // Build Azure Video Indexer resource provider client that has access token throuhg ARM
            var videoIndexerResourceProviderClient = await VideoIndexerResourceProviderClient.BuildVideoIndexerResourceProviderClient();

            // Get account details
            var account = await videoIndexerResourceProviderClient.GetAccount(log);
            var accountLocation = account.Location;
            var accountId = account.Properties.Id;

            // Get account level access token for Azure Video Indexer 
            var accountAccessToken = await videoIndexerResourceProviderClient.GetAccessToken(ArmAccessTokenPermission.Contributor, ArmAccessTokenScope.Account, log);

            // Upload the video
            await UploadVideo(name, uri, accountLocation, accountId, accountAccessToken, client, log);
        }

        /// <summary>
        /// Uploads the video from a station to the Video Indexer.
        /// </summary>
        /// <param name="videoName"></param>
        /// <param name="videoUri"></param>
        /// <param name="accountLocation"></param>
        /// <param name="accountId"></param>
        /// <param name="accountAccessToken"></param>
        /// <param name="client"></param>
        /// <param name="log"></param>
        /// <returns></returns>
        private static async Task UploadVideo(string videoName, Uri videoUri, string accountLocation, string accountId, string accountAccessToken, HttpClient client, ILogger log)
        {
            log.LogInformation($"Video is starting to upload with video name: {videoName}, videoUri: {videoUri}");

            var content = new MultipartFormDataContent();

            //string functionCallbackUrl = await GetFunctionCallbackUrl();

            try
            {
                var queryParams = CreateQueryString(
                    new Dictionary<string, string>()
                    {
                        {"accessToken", accountAccessToken},
                        {"name", videoName},
                        {"privacy", "Private"},
                        {"videoUrl", videoUri.ToString()},
                        {"callbackUrl", FunctionCallbackUrl },
                    });

                log.LogInformation($"API Call: {ApiUrl}/{accountLocation}/Accounts/{accountId}/Videos?{queryParams}");

                var uploadRequestResult = await client.PostAsync($"{ApiUrl}/{accountLocation}/Accounts/{accountId}/Videos?{queryParams}", content);

                VerifyStatus(uploadRequestResult, System.Net.HttpStatusCode.OK);

                var uploadResult = await uploadRequestResult.Content.ReadAsStringAsync();

                // Get the video ID from the upload result
                var videoId = System.Text.Json.JsonSerializer.Deserialize<Video>(uploadResult).Id;
                log.LogInformation($"\nVideo ID {videoId} was uploaded successfully");
            }
            catch (Exception ex)
            {
                log.LogInformation(ex.ToString());
                throw;
            }
        }

        static string CreateQueryString(IDictionary<string, string> parameters)
        {
            var queryParameters = HttpUtility.ParseQueryString(string.Empty);
            foreach (var parameter in parameters)
            {
                queryParameters[parameter.Key] = parameter.Value;
            }

            return queryParameters.ToString();
        }

        public static void VerifyStatus(HttpResponseMessage response, System.Net.HttpStatusCode expectedStatusCode)
        {
            if (response.StatusCode != expectedStatusCode)
            {
                throw new Exception(response.ToString());
            }
        }

        /// <summary>
        /// Gets the captions for the given video in the provided format (VTT, TTML, SRT, TXT, or CSV). See
        /// https://api-portal.videoindexer.ai/api-details#api=Operations&operation=Get-Video-Captions
        /// </summary>
        /// <param name="videoId"></param>
        /// <param name="log"></param>
        /// <returns></returns>
        private static async Task GetVideoCaptions(string videoId, ILogger log)
        {
            // we don't have the video name and will need to get it from Video Indexer, so let's do that first
            // Build Azure Video Indexer resource provider client that has access token throuhg ARM
            var videoIndexerResourceProviderClient = await VideoIndexerResourceProviderClient.BuildVideoIndexerResourceProviderClient();

            // Get account details
            var account = await videoIndexerResourceProviderClient.GetAccount(log);
            var accountLocation = account.Location;
            var accountId = account.Properties.Id;

            // Get account level access token for Azure Video Indexer 
            var accountAccessToken = await videoIndexerResourceProviderClient.GetAccessToken(ArmAccessTokenPermission.Contributor, ArmAccessTokenScope.Account, log);

            string queryParams = CreateQueryString(
                new Dictionary<string, string>()
                {
                    { "accessToken", accountAccessToken },
                    // Allowed values: Vtt / Ttml / Srt / Txt / Csv
                    { "format", "Vtt" },
                    { "language", "English" },
                });

            // Create the http client in order to get the JSON Insights of the video
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = false
            };

            var client = new HttpClient(handler);

            var videoCaptionsRequestResult = await client.GetAsync($"{ApiUrl}/{accountLocation}/Accounts/{accountId}/Videos/{videoId}/Captions?{queryParams}");

            VerifyStatus(videoCaptionsRequestResult, System.Net.HttpStatusCode.OK);

            var videoCaptionsResult = await videoCaptionsRequestResult.Content.ReadAsStringAsync();

            log.LogInformation($"Captions of the video for video ID {videoId}: \n{videoCaptionsRequestResult}");
        }
    }

    public class AccessTokenRequest
    {
        [JsonPropertyName("permissionType")]
        public ArmAccessTokenPermission PermissionType { get; set; }

        [JsonPropertyName("scope")]
        public ArmAccessTokenScope Scope { get; set; }

        /*[JsonPropertyName("projectId")]
        public string ProjectId { get; set; }

        [JsonPropertyName("videoId")]
        public string VideoId { get; set; }*/
    }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum ArmAccessTokenPermission
    {
        Reader,
        Contributor,
        MyAccessAdministrator,
        Owner,
    }

    [JsonConverter(typeof(JsonStringEnumConverter))]
    public enum ArmAccessTokenScope
    {
        Account,
        Project,
        Video
    }

    public class Video
    {
        [JsonPropertyName("id")]
        public string Id { get; set; }

        [JsonPropertyName("state")]
        public string State { get; set; }

        [JsonPropertyName("name")]
        public string Name { get; set; }
    }

    public enum ProcessingState
    {
        Uploaded,
        Processing,
        Processed,
        Failed
    }
}