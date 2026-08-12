using System.Security.Claims;
using System.Security.Cryptography;
using Azure;
using Azure.AI.ContentSafety;
using Azure.AI.Vision.ImageAnalysis;
using Azure.Identity;
using Azure.Maps.Search;
using Azure.Security.KeyVault.Secrets;
using Azure.Storage.Blobs;
using golf1052.Mastodon;
using golf1052.ThreadsAPI;
using idunno.Authentication.Basic;
using ImageMagick;
using Imgur.API.Authentication;
using Imgur.API.Endpoints;
using Imgur.API.Models;
using LinqToTwitter;
using LinqToTwitter.OAuth;
using Microsoft.AspNetCore.Authentication;
using Microsoft.AspNetCore.Authentication.Cookies;
using Microsoft.AspNetCore.DataProtection;
using Microsoft.Azure.Cosmos;
using Microsoft.Azure.Cosmos.Spatial;
using Microsoft.Extensions.Caching.Memory;
using SeattleCarsInBikeLanes.Database;
using SeattleCarsInBikeLanes.GuessGame;
using SeattleCarsInBikeLanes.Models;
using SeattleCarsInBikeLanes.Models.TypeConverters;
using SeattleCarsInBikeLanes.Providers;

namespace SeattleCarsInBikeLanes
{
    public class Program
    {
        public static ILogger? Logger = null;

        public static void Main(string[] args)
        {
            MagickNET.Initialize();
            System.ComponentModel.TypeDescriptor
                .AddAttributes(typeof(Position), new System.ComponentModel.TypeConverterAttribute(typeof(PositionConverter)));

            var builder = WebApplication.CreateBuilder(args);

            // Add services to the container.

            // Add Application Insights (required for App Service logging)
            builder.Services.AddApplicationInsightsTelemetry();

            builder.Services.AddMemoryCache();

            builder.Services.AddCors(options =>
            {
                options.AddDefaultPolicy(policy =>
                {
                    policy.WithOrigins("https://localhost:7152",
                        "http://localhost:5152",
                        // Bluesky OAuth development uses the atproto localhost exception, which
                        // requires a 127.0.0.1 loopback callback, so the dev site is browsed there.
                        "http://127.0.0.1:5152",
                        "https://seattle.carinbikelane.com");
                });
            });

            builder.Services.AddControllers();
            builder.Services.AddSignalR();

            builder.Services.Configure<BlueskyOAuthOptions>(
                builder.Configuration.GetSection(BlueskyOAuthOptions.SectionName));

            // Persist the data protection key ring across restarts. It protects the Bluesky session
            // cookie and the in flight OAuth state, so ephemeral keys would sign everyone out on
            // every restart and break logins that are mid redirect. On App Service the framework
            // already persists keys to %HOME%\ASP.NET\DataProtection-Keys, which is durable and
            // shared by every instance of the app; setting the application name keeps the ring
            // stable across slot swaps.
            builder.Services.AddDataProtection()
                .SetApplicationName("SeattleCarsInBikeLanes");

            builder.Services.AddAuthentication(BasicAuthenticationDefaults.AuthenticationScheme)
                .AddBasic(options =>
                {
                    options.Realm = "Admin page";

                    // The handler refuses to respond over plain HTTP by default, returning 421 with
                    // no WWW-Authenticate header, so the browser never prompts and the admin page
                    // looks broken. Development runs on http://127.0.0.1 because the atproto
                    // localhost OAuth exception requires a plain HTTP loopback callback, so allow it
                    // there. Loopback traffic never leaves the machine. Production stays HTTPS only.
                    options.AllowInsecureProtocol = builder.Environment.IsDevelopment();

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
                })
                .AddCookie(BlueskyAuthDefaults.CookieScheme, cookieOptions =>
                {
                    cookieOptions.Cookie.Name = BlueskyAuthDefaults.SessionCookie;
                    cookieOptions.Cookie.HttpOnly = true;
                    cookieOptions.Cookie.SameSite = SameSiteMode.Lax;
                    cookieOptions.Cookie.SecurePolicy = CookieSecurePolicy.SameAsRequest;
                    cookieOptions.ExpireTimeSpan = TimeSpan.FromDays(90);
                    cookieOptions.SlidingExpiration = true;

                    // This scheme only ever guards API calls, so answer with status codes instead
                    // of redirecting to a login page that does not exist.
                    cookieOptions.Events = new CookieAuthenticationEvents()
                    {
                        OnRedirectToLogin = context =>
                        {
                            context.Response.StatusCode = StatusCodes.Status401Unauthorized;
                            return Task.CompletedTask;
                        },
                        OnRedirectToAccessDenied = context =>
                        {
                            context.Response.StatusCode = StatusCodes.Status403Forbidden;
                            return Task.CompletedTask;
                        }
                    };
                })
                .AddScheme<AuthenticationSchemeOptions, BlueskyBearerAuthenticationHandler>(
                    BlueskyAuthDefaults.BearerScheme, _ => { })
                .AddPolicyScheme(BlueskyAuthDefaults.AnyScheme, BlueskyAuthDefaults.AnyScheme, policyOptions =>
                {
                    // Browsers send a cookie, the mobile app sends a bearer token.
                    policyOptions.ForwardDefaultSelector = context =>
                    {
                        string? authorization = context.Request.Headers.Authorization.FirstOrDefault();
                        return !string.IsNullOrWhiteSpace(authorization) &&
                            authorization.StartsWith("Bearer ", StringComparison.OrdinalIgnoreCase)
                            ? BlueskyAuthDefaults.BearerScheme
                            : BlueskyAuthDefaults.CookieScheme;
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
                ILogger<ReportedItemsDatabase> logger = c.GetRequiredService<ILogger<ReportedItemsDatabase>>();
                Container container = c.GetRequiredService<Microsoft.Azure.Cosmos.Database>().GetContainer("items");
                return new ReportedItemsDatabase(logger, container);
            });
            services.AddSingleton(c =>
            {
                ILogger<MastodonOAuthMappingDatabase> logger = c.GetRequiredService<ILogger<MastodonOAuthMappingDatabase>>();
                Container container = c.GetRequiredService<Microsoft.Azure.Cosmos.Database>().GetContainer("mastodon-oauth-mapping");
                return new MastodonOAuthMappingDatabase(logger, container);
            });
            services.AddSingleton(c =>
            {
                SecretClient client = c.GetRequiredService<SecretClient>();
                KeyVaultSecret imageAnalysisTokenSecret = client.GetSecret("computervision");
                string imageAnalysisToken = imageAnalysisTokenSecret.Value;
                return new ImageAnalysisClient(new Uri("https://seattlecarsinbikelanesvision.cognitiveservices.azure.com/"), new AzureKeyCredential(imageAnalysisToken));
            });
            services.AddSingleton(c =>
            {
                SecretClient client = c.GetRequiredService<SecretClient>();
                KeyVaultSecret contentSafetyTokenSecret = client.GetSecret("contentsafety");
                string contentSafetyToken = contentSafetyTokenSecret.Value;
                return new ContentSafetyClient(new Uri("https://carsinbikelanes-content-safety.cognitiveservices.azure.com/"), new AzureKeyCredential(contentSafetyToken));
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
            services.AddSingleton<IImageEndpoint>(c =>
            {
                return new ImageEndpoint(c.GetRequiredService<ApiClient>(),
                    new HttpClient());
            });
            services.AddSingleton(c =>
            {
                return builder.Environment;
            });
            services.AddSingleton(c =>
            {
                return new MastodonClientProvider(c.GetRequiredService<ILogger<MastodonClientProvider>>(),
                    c.GetRequiredService<IWebHostEnvironment>(),
                    c.GetRequiredService<MastodonOAuthMappingDatabase>(),
                    c.GetRequiredService<SecretClient>(),
                    c.GetRequiredService<ILogger<MastodonClient>>(),
                    c.GetRequiredService<HttpClient>());
            });
            services.AddSingleton(c =>
            {
                return new FeedProvider(c.GetRequiredService<ILogger<FeedProvider>>(),
                    c.GetRequiredService<ReportedItemsDatabase>(),
                    c.GetRequiredService<BlobContainerClient>());
            });
            services.AddSingleton(c =>
            {
                return new SlackbotProvider(c.GetRequiredService<ILogger<SlackbotProvider>>(),
                    c.GetRequiredService<HttpClient>(),
                    c.GetRequiredService<SecretClient>());
            });
            services.AddSingleton(c =>
            {
                return new DeviceBlocklistProvider(c.GetRequiredService<ILogger<DeviceBlocklistProvider>>(),
                    c.GetRequiredService<BlobContainerClient>(),
                    c.GetRequiredService<IMemoryCache>());
            });
            services.AddSingleton(c =>
            {
                return new BlueskyClientProvider(c.GetRequiredService<ILogger<BlueskyClientProvider>>(),
                    c.GetRequiredService<SecretClient>(),
                    c.GetRequiredService<HttpClient>());
            });
            services.AddSingleton<GuessGameManager>();
            services.AddSingleton<BlueskyOAuthProvider>();
            services.AddSingleton<BlueskyLoginStateStore>();
            services.AddSingleton<BlueskyTokenIssuer>();
            services.AddSingleton(c =>
            {
                SecretClient secretClient = c.GetRequiredService<SecretClient>();

                ThreadsClient threadsClient = new ThreadsClient(secretClient.GetSecret("threads-client-id").Value.Value,
                    secretClient.GetSecret("threads-client-secret").Value.Value,
                    c.GetRequiredService<HttpClient>())
                {
                    LongLivedAccessToken = secretClient.GetSecret("threads-access-token").Value.Value,
                    UserId = secretClient.GetSecret("threads-userid").Value.Value
                };
                return threadsClient;
            });

            var app = builder.Build();
            Logger = app.Logger;

            using (var serviceScope = app.Services.CreateScope())
            {
                var serviceProvider = serviceScope.ServiceProvider;
                InitialUploadPruner initialUploadPruner = new InitialUploadPruner(
                    serviceProvider.GetRequiredService<ILogger<InitialUploadPruner>>(),
                    serviceProvider.GetRequiredService<BlobContainerClient>(),
                    TimeSpan.FromMinutes(10));
            }

            // Configure the HTTP request pipeline.

            // The atproto localhost development exception requires a plain http loopback callback,
            // so redirecting to https locally would break sign in. Production is unaffected.
            if (!app.Environment.IsDevelopment())
            {
                app.UseHttpsRedirection();
            }

            app.UseDefaultFiles();

            app.UseStaticFiles();

            app.UseCors();

            app.UseAuthentication();

            app.UseAuthorization();

            app.MapControllers();

            app.MapHub<GuessGameHub>("/GuessGameHub");

            app.Run();
        }
    }
}


