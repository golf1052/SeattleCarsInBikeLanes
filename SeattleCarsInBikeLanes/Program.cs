using System.Security.Claims;
using System.Security.Cryptography;
using Azure.Identity;
using Azure.Maps.Search;
using Azure.Security.KeyVault.Secrets;
using Azure.Storage.Blobs;
using idunno.Authentication.Basic;
using ImageMagick;
using Imgur.API.Authentication;
using Imgur.API.Endpoints;
using Imgur.API.Models;
using LinqToTwitter;
using LinqToTwitter.OAuth;
using Microsoft.Azure.CognitiveServices.Vision.ComputerVision;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Cosmos.Spatial;
using SeattleCarsInBikeLanes.Database;
using SeattleCarsInBikeLanes.Models;
using SeattleCarsInBikeLanes.Models.TypeConverters;

namespace SeattleCarsInBikeLanes
{
    public class Program
    {
        public static void Main(string[] args)
        {
            MagickNET.Initialize();
            System.ComponentModel.TypeDescriptor
                .AddAttributes(typeof(Position), new System.ComponentModel.TypeConverterAttribute(typeof(PositionConverter)));

            var builder = WebApplication.CreateBuilder(args);

            // Add services to the container.

            builder.Services.AddCors(options =>
            {
                options.AddDefaultPolicy(policy =>
                {
                    policy.WithOrigins("https://localhost:7152",
                        "http://localhost:5152",
                        "https://seattle.carinbikelane.com");
                });
            });

            builder.Services.AddControllers();

            builder.Services.AddAuthentication(BasicAuthenticationDefaults.AuthenticationScheme)
                .AddBasic(options =>
                {
                    options.Realm = "Admin page";
                    options.Events = new BasicAuthenticationEvents()
                    {
                        OnValidateCredentials = context =>
                        {
                            var secretClient = context.HttpContext.RequestServices.GetRequiredService<SecretClient>();
                            var helperMethods = context.HttpContext.RequestServices.GetRequiredService<HelperMethods>();
                            bool usernameMatch = helperMethods.IsAuthorized("admin-username", context.Username, secretClient);
                            bool passwordMatch = helperMethods.IsAuthorized("admin-password", context.Password, secretClient);
                            if (usernameMatch && passwordMatch)
                            {
                                var claims = new[]
                                {
                                    new Claim(ClaimTypes.NameIdentifier,
                                        context.Username,
                                        ClaimValueTypes.String,
                                        context.Options.ClaimsIssuer),
                                    new Claim(ClaimTypes.Name,
                                        context.Username,
                                        ClaimValueTypes.String,
                                        context.Options.ClaimsIssuer)
                                };

                                context.Principal = new ClaimsPrincipal(
                                    new ClaimsIdentity(claims, context.Scheme.Name));
                                context.Success();
                            }

                            return Task.CompletedTask;
                        }
                    };
                });

            builder.Services.AddAuthorization();

            // Setup services
            var services = builder.Services;
            services.AddSingleton<HttpClient>();
            services.AddSingleton<HelperMethods>();
            services.AddSingleton<StatusResponse>();
            services.AddSingleton<DefaultAzureCredential>(c =>
            {
                return new DefaultAzureCredential(new DefaultAzureCredentialOptions()
                {
                    // Explicitly set the AuthorityHost so local testing works with personal Microsoft accounts (MSA)
                    // https://learn.microsoft.com/en-us/azure/active-directory/develop/msal-client-application-configuration
                    AuthorityHost = new Uri("https://login.microsoftonline.com/common/"),
                    ExcludeVisualStudioCredential = true
                });
            });
            services.AddSingleton(c =>
            {
                return new SecretClient(new Uri("https://seattle-carsinbikelanes.vault.azure.net/"),
                    c.GetRequiredService<DefaultAzureCredential>());
            });
            services.AddSingleton(c =>
            {
                SecretClient client = c.GetRequiredService<SecretClient>();
                KeyVaultSecret twitterBearerTokenSecret = client.GetSecret("twitter-bearer-token");
                string twitterBearerToken = twitterBearerTokenSecret.Value;
                var twitterAuth = new ApplicationOnlyAuthorizer()
                {
                    BearerToken = twitterBearerToken
                };
                return new TwitterContext(twitterAuth);
            });
            services.AddSingleton(c =>
            {
                return new MapsSearchClient(c.GetRequiredService<DefaultAzureCredential>(),
                    "df857d2c-3805-4793-90e4-63e84a499756");
            });
            services.AddSingleton(c =>
            {
                return new CosmosClient("https://seattle-carsinbikelanes-db.documents.azure.com:443/",
                    c.GetRequiredService<DefaultAzureCredential>());
            });
            services.AddSingleton(c =>
            {
                CosmosClient client = c.GetRequiredService<CosmosClient>();
                return client.GetDatabase("seattle");
            });
            services.AddSingleton(c =>
            {
                Microsoft.Azure.Cosmos.Database database = c.GetRequiredService<Microsoft.Azure.Cosmos.Database>();
                return database.GetContainer("items");
            });
            services.AddSingleton(c =>
            {
                ILogger<ReportedItemsDatabase> logger = c.GetRequiredService<ILogger<ReportedItemsDatabase>>();
                Container container = c.GetRequiredService<Container>();
                return new ReportedItemsDatabase(logger, container);
            });
            services.AddSingleton(c =>
            {
                SecretClient client = c.GetRequiredService<SecretClient>();
                KeyVaultSecret computerVisionTokenSecret = client.GetSecret("computervision");
                string computerVisionToken = computerVisionTokenSecret.Value;
                return new ComputerVisionClient(new ApiKeyServiceClientCredentials(computerVisionToken), c.GetRequiredService<HttpClient>(), false)
                {
                    Endpoint = "https://seattlecarsinbikelanesvision.cognitiveservices.azure.com/"
                };
            });
            services.AddSingleton(c =>
            {
                return RandomNumberGenerator.Create();
            });
            services.AddSingleton(c =>
            {
                return new BlobServiceClient(new Uri("https://seacarsinbikelanesfiles.blob.core.windows.net/"),
                    c.GetRequiredService<DefaultAzureCredential>());
            });
            services.AddSingleton(c =>
            {
                var blobServiceClient = c.GetRequiredService<BlobServiceClient>();
                return blobServiceClient.GetBlobContainerClient("files");
            });
            services.AddSingleton(c =>
            {
                SecretClient secretClient = c.GetRequiredService<SecretClient>();
                ApiClient imgurApi = new ApiClient(secretClient.GetSecret("imgur-client-id").Value.Value);
                OAuth2Token token = new OAuth2Token()
                {
                    AccessToken = secretClient.GetSecret("imgur-access-token").Value.Value,
                    RefreshToken = secretClient.GetSecret("imgur-refresh-token").Value.Value,
                    ExpiresIn = 315360000, // Imgur actually gave me a token that expires in 10 years
                    AccountId = 166751609,
                    AccountUsername = "seattlecarsinbikelanes",
                    TokenType = "Bearer"
                };
                imgurApi.SetOAuth2Token(token);
                return imgurApi;
            });
            services.AddSingleton(c =>
            {
                return new ImageEndpoint(c.GetRequiredService<ApiClient>(),
                    new HttpClient());
            });

            var app = builder.Build();

            using (var serviceScope = app.Services.CreateScope())
            {
                var serviceProvider = serviceScope.ServiceProvider;
                TweetProcessor tweetProcessor = new TweetProcessor(
                    serviceProvider.GetRequiredService<ILogger<TweetProcessor>>(),
                    serviceProvider.GetRequiredService<TwitterContext>(),
                    serviceProvider.GetRequiredService<MapsSearchClient>(),
                    serviceProvider.GetRequiredService<ReportedItemsDatabase>(),
                    serviceProvider.GetRequiredService<StatusResponse>(),
                    TimeSpan.FromHours(1),
                    serviceProvider.GetRequiredService<HelperMethods>());

                InitialUploadPruner initialUploadPruner = new InitialUploadPruner(
                    serviceProvider.GetRequiredService<ILogger<InitialUploadPruner>>(),
                    serviceProvider.GetRequiredService<BlobContainerClient>(),
                    TimeSpan.FromMinutes(10));
            }

            // Configure the HTTP request pipeline.

            app.UseHttpsRedirection();

            app.UseDefaultFiles();

            app.UseStaticFiles();

            app.UseCors();

            app.UseAuthentication();

            app.UseAuthorization();

            app.MapControllers();

            app.Run();
        }
    }
}


