# Contributing

## Things You'll Need

- [.NET 7](https://dotnet.microsoft.com/en-us/download/dotnet/7.0) (currently on Release Candidate 1 (RC1))
  - The DateOnly and TimeOnly serializers were only introduced in .NET 7.
- Optional but recommended: [Visual Studio 2022 Preview](https://visualstudio.microsoft.com/vs/preview/) or [Visual Studio Code](https://code.visualstudio.com/)
- Either [Azure Powershell](https://learn.microsoft.com/en-us/powershell/azure/install-az-ps?view=azps-8.3.0) or [Azure CLI](https://learn.microsoft.com/en-us/cli/azure/install-azure-cli)
  - Used for authenticating to Azure to connect to Azure resources locally if not logging into Visual Studio or Visual Studio Code.

## Running Locally

**[NOTE]**: Unless you have permissions (you probably don't) to the Azure resources referenced in the codebase many things will not work. If you want to run things locally you'll most likely need to create your own Azure resources.
  - No `twitter-bearer-token` which is needed to pull tweets from Twitter. You can get your own token at https://developer.twitter.com and hack it in.
  - No Azure Maps tiles and search.
  - No Cosmos DB. You can use the [emulator](https://learn.microsoft.com/en-us/azure/cosmos-db/local-emulator) and import the [sample data](./sampledbdata.json) into it using the [Cosmos DB data migration tool](https://github.com/azure/azure-documentdb-datamigrationtool).

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
- twitter-bearer-token: Twitter bearer token. Used for pulling tweets from Twitter.
- twitter-consumer-key: Twitter consumer key. Used for pulling tweets from Twitter.
- twitter-consumer-key-secret: Twitter consumer key secret. Used for pulling tweets from Twitter.
- twitter-oauth1-access-token: Twitter OAuth 1.0a access token. Used for uploading images to Twitter.
- twitter-oauth1-access-token-secret: Twitter OAuth 1.0a access token secret. Used for uploading images to Twitter.
- twitter-oauth2-client-id: Twitter OAuth 2.0 client id. Used for logging users into Twitter.
- twitter-oauth2-client-secret: Twitter OAuth 2.0 client secret. Used for logging users into Twitter.

### Getting OAuth 1.0a Write Token for Twitter Photo/Tweet Uploads

This token pair lasts forever or until it is revoked.

1. [POST oauth/request_token](https://developer.twitter.com/en/docs/authentication/api-reference/request_token) with `x_auth_access_type` query parameter set to `write`.
2. [GET oauth/authorize](https://developer.twitter.com/en/docs/authentication/api-reference/authorize). Login with Twitter account
3. [POST oauth/access_token](https://developer.twitter.com/en/docs/authentication/api-reference/access_token). `oauth_token` is from step 1. `oauth_verifier` is PIN returned from step 2.

### Running Profiler in Visual Studio

1. Ensure VSStandardCollectorService150 (Visual Studio Standard Collector Service 150) service is running
2. In Program.cs update the AuthorityHost URL to use the tenant ID instead of common
3. Finally exclude all credential types except for InteractiveBrowserCredentials. That must be specifically included. 

## Useful Links

- [Azure Maps Samples](https://samples.azuremaps.com/)
  - [Azure Maps Layer & Legend Control module](https://github.com/Azure-Samples/azure-maps-layer-legend)
    - [Legend Control documentation](https://github.com/Azure-Samples/azure-maps-layer-legend/blob/main/docs/legend_control.md)
  - [Azure Maps Spider Cluster module (forked)](https://github.com/golf1052/azure-maps-spider-clusters)
