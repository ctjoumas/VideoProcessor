namespace VideoProcessor
{
    using Azure.Core;
    using Azure.Identity;
    using Microsoft.Extensions.Logging;
    using System;
    using System.Collections.Generic;
    using System.Linq;
    using System.Net.Http.Headers;
    using System.Text;
    using System.Text.Json.Serialization;
    using System.Threading.Tasks;

    internal class VideoIndexerResourceProviderClient
    {
        private const string AzureResourceManager = "https://management.azure.com";
        private const string ApiVersion = "2022-08-01";
        private string ResourceGroup = Environment.GetEnvironmentVariable("ResourceGroup");
        private string SubscriptionId = Environment.GetEnvironmentVariable("SubscriptionId");
        private string AccountName = Environment.GetEnvironmentVariable("AccountName");
        private readonly string armAccessToken;

        /// <summary>
        /// Builds the Video Indexer Resource Provider Client with the proper token for authorization.
        /// </summary>
        /// <returns></returns>
        async public static Task<VideoIndexerResourceProviderClient> BuildVideoIndexerResourceProviderClient()
        {
            var tokenRequestContext = new TokenRequestContext(new[] { $"{AzureResourceManager}/.default" });
            var tokenRequestResult = await new DefaultAzureCredential(new DefaultAzureCredentialOptions { ExcludeEnvironmentCredential = true }).GetTokenAsync(tokenRequestContext);

            return new VideoIndexerResourceProviderClient(tokenRequestResult.Token);
        }

        public VideoIndexerResourceProviderClient(string armAaccessToken)
        {
            this.armAccessToken = armAaccessToken;
        }

        /// <summary>
        /// Generates an access token. Calls the generateAccessToken API  (https://github.com/Azure/azure-rest-api-specs/blob/main/specification/vi/resource-manager/Microsoft.VideoIndexer/stable/2022-08-01/vi.json#:~:text=%22/subscriptions/%7BsubscriptionId%7D/resourceGroups/%7BresourceGroupName%7D/providers/Microsoft.VideoIndexer/accounts/%7BaccountName%7D/generateAccessToken%22%3A%20%7B)
        /// </summary>
        /// <param name="permission"> The permission for the access token</param>
        /// <param name="scope"> The scope of the access token </param>
        /// <param name="videoId"> if the scope is video, this is the video Id </param>
        /// <param name="projectId"> If the scope is project, this is the project Id </param>
        /// <returns> The access token, otherwise throws an exception</returns>
        public async Task<string> GetAccessToken(ArmAccessTokenPermission permission, ArmAccessTokenScope scope, ILogger log)
        {
            var accessTokenRequest = new AccessTokenRequest
            {
                PermissionType = permission,
                Scope = scope
            };

            log.LogInformation($"\nGetting access token: {System.Text.Json.JsonSerializer.Serialize(accessTokenRequest)}");

            // Set the generateAccessToken (from video indexer) http request content
            try
            {
                var jsonRequestBody = System.Text.Json.JsonSerializer.Serialize(accessTokenRequest);
                var httpContent = new StringContent(jsonRequestBody, Encoding.UTF8, "application/json");

                // Set request uri
                var requestUri = $"{AzureResourceManager}/subscriptions/{SubscriptionId}/resourcegroups/{ResourceGroup}/providers/Microsoft.VideoIndexer/accounts/{AccountName}/generateAccessToken?api-version={ApiVersion}";
                var client = new HttpClient(new HttpClientHandler());
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", armAccessToken);

                var result = await client.PostAsync(requestUri, httpContent);

                VerifyStatus(result, System.Net.HttpStatusCode.OK);
                var jsonResponseBody = await result.Content.ReadAsStringAsync();

                log.LogInformation($"Got access token: {scope}, {permission}");

                return System.Text.Json.JsonSerializer.Deserialize<GenerateAccessTokenResponse>(jsonResponseBody).AccessToken;
            }
            catch (Exception ex)
            {
                log.LogInformation(ex.ToString());
                throw;
            }
        }

        /// <summary>
        /// Gets an account. Calls the getAccount API (https://github.com/Azure/azure-rest-api-specs/blob/main/specification/vi/resource-manager/Microsoft.VideoIndexer/stable/2022-08-01/vi.json#:~:text=%22/subscriptions/%7BsubscriptionId%7D/resourceGroups/%7BresourceGroupName%7D/providers/Microsoft.VideoIndexer/accounts/%7BaccountName%7D%22%3A%20%7B)
        /// </summary>
        /// <returns>The Account, otherwise throws an exception</returns>
        public async Task<Account> GetAccount(ILogger log)
        {
            log.LogInformation($"Getting account {AccountName}.");
            
            Account account;
            
            try
            {
                // Set request uri
                var requestUri = $"{AzureResourceManager}/subscriptions/{SubscriptionId}/resourcegroups/{ResourceGroup}/providers/Microsoft.VideoIndexer/accounts/{AccountName}?api-version={ApiVersion}";
                log.LogInformation($"Requesting Video Indexer Account Name: {requestUri}");
                var client = new HttpClient(new HttpClientHandler());
                client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", armAccessToken);

                var result = await client.GetAsync(requestUri);

                VerifyStatus(result, System.Net.HttpStatusCode.OK);
                
                var jsonResponseBody = await result.Content.ReadAsStringAsync();
                account = System.Text.Json.JsonSerializer.Deserialize<Account>(jsonResponseBody);
                
                VerifyValidAccount(account, log);
                
                log.LogInformation($"The account ID is {account.Properties.Id}");
                log.LogInformation($"The account location is {account.Location}");

                return account;
            }
            catch (Exception ex)
            {
                log.LogInformation(ex.ToString());
                throw;
            }
        }

        private void VerifyValidAccount(Account account, ILogger log)
        {
            if (string.IsNullOrWhiteSpace(account.Location) || account.Properties == null || string.IsNullOrWhiteSpace(account.Properties.Id))
            {
                log.LogInformation($"{nameof(AccountName)} {AccountName} not found. Check {nameof(SubscriptionId)}, {nameof(ResourceGroup)}, {nameof(AccountName)} ar valid.");

                throw new Exception($"Account {AccountName} not found.");
            }
        }

        public static void VerifyStatus(HttpResponseMessage response, System.Net.HttpStatusCode excpectedStatusCode)
        {
            if (response.StatusCode != excpectedStatusCode)
            {
                throw new Exception(response.ToString());
            }
        }
    }

    public class AccountProperties
    {
        [JsonPropertyName("accountId")]
        public string Id { get; set; }
    }

    public class Account
    {
        [JsonPropertyName("properties")]
        public AccountProperties Properties { get; set; }

        [JsonPropertyName("location")]
        public string Location { get; set; }
    }

    public class GenerateAccessTokenResponse
    {
        [JsonPropertyName("accessToken")]
        public string AccessToken { get; set; }
    }
}
