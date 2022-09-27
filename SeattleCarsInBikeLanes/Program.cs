using Azure.Identity;
using Azure.Maps.Search;
using Azure.Security.KeyVault.Secrets;
using LinqToTwitter;
using LinqToTwitter.OAuth;
using Microsoft.Azure.Cosmos;
using SeattleCarsInBikeLanes.Database;

namespace SeattleCarsInBikeLanes
{
    public class Program
    {
        public static void Main(string[] args)
        {
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
            
            // Setup services
            var services = builder.Services;
            services.AddSingleton<HelperMethods>();
            services.AddSingleton<DefaultAzureCredential>();
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

            var app = builder.Build();

            using (var serviceScope = app.Services.CreateScope())
            {
                var serviceProvider = serviceScope.ServiceProvider;
                TweetProcessor tweetProcessor = new TweetProcessor(
                    serviceProvider.GetRequiredService<ILogger<TweetProcessor>>(),
                    serviceProvider.GetRequiredService<TwitterContext>(),
                    serviceProvider.GetRequiredService<MapsSearchClient>(),
                    serviceProvider.GetRequiredService<ReportedItemsDatabase>(),
                    TimeSpan.FromHours(1),
                    serviceProvider.GetRequiredService<HelperMethods>());
            }

            // Configure the HTTP request pipeline.

            app.UseHttpsRedirection();

            app.UseDefaultFiles();

            app.UseStaticFiles();

            app.UseCors();

            app.UseAuthorization();

            app.MapControllers();

            app.Run();
        }
    }
}


