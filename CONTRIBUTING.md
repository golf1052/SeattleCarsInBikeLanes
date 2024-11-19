# Contributing

## Things You'll Need

- [.NET 8](https://dotnet.microsoft.com/en-us/download/dotnet/8.0)
- Optional but recommended: [Visual Studio 2022 Community](https://visualstudio.microsoft.com/vs/) or [Visual Studio Code](https://code.visualstudio.com/)
- Either [Azure Powershell](https://learn.microsoft.com/en-us/powershell/azure/install-az-ps?view=azps-8.3.0) or [Azure CLI](https://learn.microsoft.com/en-us/cli/azure/install-azure-cli)
  - Used for authenticating to Azure to connect to Azure resources locally if not logging into Visual Studio or Visual Studio Code.
- [Node.js](https://nodejs.org/)
  - Tested with Node.js 18.19

## Running Locally

**[NOTE]**: Unless you have permissions (you probably don't) to the Azure resources referenced in the codebase many things will not work. If you want to run things locally you'll most likely need to create your own Azure resources.
- No Azure Maps tiles and search.
- No Cosmos DB. You can use the [emulator](https://learn.microsoft.com/en-us/azure/cosmos-db/local-emulator) and import the [sample data](./sampledbdata.json) into it using the [Cosmos DB data migration tool](https://github.com/azure/azure-documentdb-datamigrationtool).

### Building Typescript

Currently only the Bluesky portion of the front-end requires Typescript building. Run all `npx` commands from `SeattleCarsInBikeLanes` directory.

When making changes
- `npx webpack --watch`

Before deploy
- `npx webpack`

### Secrets List

May or may not be up to date. Do a find all on "GetSecret" to confirm.

- admin-password: Password to access the admin page (`/AdminPage`). You can configure this to whatever you want.
- admin-username: Username to access the admin page (`/AdminPage`). You can configure this to whatever you want.
- computervision: Api key for Computer Vision service. Used for extracting tags from uploaded images. Create your own Computer Vision service and enter your key.
- imgur-access-token: Access token for Imgur API. Used for uploading images to Imgur. Create your own Imgur application using the docs [here](https://apidocs.imgur.com/).
- imgur-client-id: Client ID for Imgur API. Used for uploading images to Imgur.
- imgur-client-secret: Client secret for Imgur API. Used for uploading images to Imgur.
- imgur-refresh-token: Refresh token for Imgur API. Used for uploading images to Imgur.
- slack-user-id: User ID of Slack member who finalized uploaded messages are sent to.
- slackbot-token: Legacy Slack bot token used for sending Slackbot messages.
- social-ridetransit-access-token: Access token for Mastodon server client. Used for posting to https://social.ridetrans.it

### Running Profiler in Visual Studio

1. Ensure VSStandardCollectorService150 (Visual Studio Standard Collector Service 150) service is running
2. In Program.cs update the AuthorityHost URL to use the tenant ID instead of common
3. Finally exclude all credential types except for InteractiveBrowserCredentials. That must be specifically included. 

## Useful Links

- [Azure Maps Samples](https://samples.azuremaps.com/)
  - [Azure Maps Layer & Legend Control module](https://github.com/Azure-Samples/azure-maps-layer-legend)
    - [Legend Control documentation](https://github.com/Azure-Samples/azure-maps-layer-legend/blob/main/docs/legend_control.md)
  - [Azure Maps Spider Cluster module (forked)](https://github.com/golf1052/azure-maps-spider-clusters)

# Publishing NuGet Packages

1. `dotnet build -c Release`
2. `dotnet pack -c Release`
3. `dotnet nuget push <path to .nupkg> -k <NuGet API key>`
