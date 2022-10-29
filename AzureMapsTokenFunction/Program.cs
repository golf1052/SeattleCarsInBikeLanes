using Azure.Identity;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

var host = new HostBuilder()
    .ConfigureFunctionsWorkerDefaults()
    .ConfigureServices(services =>
    {
        services.AddSingleton(c =>
        {
            return new DefaultAzureCredential(new DefaultAzureCredentialOptions()
            {
                // Explicitly set the AuthorityHost so local testing works with personal Microsoft accounts (MSA)
                // https://learn.microsoft.com/en-us/azure/active-directory/develop/msal-client-application-configuration
                AuthorityHost = new Uri("https://login.microsoftonline.com/common/")
            });
        });
    })
    .Build();

host.Run();
