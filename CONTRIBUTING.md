# Contributing

## Things You'll Need

- [.NET 10](https://dotnet.microsoft.com/en-us/download/dotnet/10.0)
- Optional but recommended: [Visual Studio 2022 Community](https://visualstudio.microsoft.com/vs/) or [Visual Studio Code](https://code.visualstudio.com/)
- Either [Azure Powershell](https://learn.microsoft.com/en-us/powershell/azure/install-az-ps?view=azps-8.3.0) or [Azure CLI](https://learn.microsoft.com/en-us/cli/azure/install-azure-cli)
  - Used for authenticating to Azure to connect to Azure resources locally if not logging into Visual Studio or Visual Studio Code.

## Running Locally

**[NOTE]**: Unless you have permissions (you probably don't) to the Azure resources referenced in the codebase many things will not work. If you want to run things locally you'll most likely need to create your own Azure resources.
- No Azure Maps tiles and search.
- No Cosmos DB. You can use the [emulator](https://learn.microsoft.com/en-us/azure/cosmos-db/local-emulator) and import the [sample data](./sampledbdata.json) into it using the [Cosmos DB data migration tool](https://github.com/azure/azure-documentdb-datamigrationtool).

### Signing in with Bluesky locally

Bluesky sign in uses the [atproto profile of OAuth](https://atproto.com/specs/oauth). The server is the
OAuth client, so no Bluesky tokens ever reach the browser.

Locally it uses the spec's localhost development exception, which means no publicly reachable client
metadata document is needed. Two things to know:

- **Browse to `http://127.0.0.1:5152`, not `localhost`.** The exception requires a loopback callback
  on `127.0.0.1`, and cookies are scoped per host, so starting at `localhost` would leave the login
  state cookie on a different host than the callback and sign in would fail.
- HTTPS redirection is disabled in Development for the same reason, since the callback is plain HTTP.

Production configuration lives under `BlueskyOAuth` in `appsettings.json`, and the client metadata
document is generated at `/client-metadata.json` so its `client_id` always matches the URL it is
served from. In Development that endpoint returns 404 on purpose, because the localhost exception
means the authorization server synthesizes the metadata instead of fetching it.

#### What the 127.0.0.1 dev origin does and doesn't affect

Verified working over `http://127.0.0.1:5152`: the map and Azure Maps tiles, Bluesky sign in, report
submission, `/AdminPage`, the guessing game including its SignalR hub, and the clipboard buttons.
`127.0.0.1` counts as a
[potentially trustworthy origin](https://w3c.github.io/webappsec-secure-contexts/#is-origin-trustworthy),
so browser APIs that require a secure context still work even though the connection is plain HTTP.

**Mastodon sign in follows whichever origin you browse from.** In Development the OAuth redirect URL
is derived from the incoming request rather than hardcoded, so it stays on the same origin as the
page that started the login. This matters because the redirect page reads `mastodonEndpoint` back out
of `localStorage`, which is scoped per origin, so being bounced to a different origin loses it. Both
`https://localhost:7152/mastodonredirect` and `http://127.0.0.1:5152/mastodonredirect` need to be
registered as redirect URIs on the Mastodon application, one per line. New instance registrations
request both automatically. Production keeps a fixed redirect URL so it cannot be influenced by a
forged `Host` header.

The Twitter and Threads redirects are still hardcoded to `https://localhost:7152`, so those flows
would need the same treatment plus new redirect URIs registered in their developer consoles. Neither
has a sign in entry point in the UI today.

### Secrets List

May or may not be up to date. Do a find all on "GetSecret" to confirm.

- admin-password: Password to access the admin page (`/AdminPage`). You can configure this to whatever you want.
- admin-username: Username to access the admin page (`/AdminPage`). You can configure this to whatever you want.
- computervision: Api key for Computer Vision service. Used for extracting tags from uploaded images. Create your own Computer Vision service and enter your key.
- imgur-access-token: Access token for Imgur API. Used for uploading images to Imgur. Create your own Imgur application using the docs [here](https://apidocs.imgur.com/).
- imgur-client-id: Client ID for Imgur API. Used for uploading images to Imgur.
- imgur-client-secret: Client secret for Imgur API. Used for uploading images to Imgur.
- imgur-refresh-token: Refresh token for Imgur API. Used for uploading images to Imgur.
- threads-access-token: Access token for Threads API. Needs to be refreshed every ~90 days.
- slack-user-id: User ID of Slack member who finalized uploaded messages are sent to.
- slackbot-token: Legacy Slack bot token used for sending Slackbot messages.
- social-ridetransit-access-token: Access token for Mastodon server client. Used for posting to https://social.ridetrans.it

#### Get New Threads API Key

1. Go to https://developers.facebook.com
2. Open the "Cars In Bike Lanes Seattle" app
3. Go to "Use cases"
4. On "Access the Threads API" click "Customize"
5. Click "Settings" under "Access the Threads API"
6. Under "User Token Generator" and "carbikelanesea" click "Generate Access Token"
7. Log in if needed and copy the new access token
8. Log in to Azure portal, go to the Key Vault, open Secrets
9. Look for "threads-access-token"
10. Create a new version of the secret, paste in the key, and click create

### Running Profiler in Visual Studio

1. Ensure VSStandardCollectorService150 (Visual Studio Standard Collector Service 150) service is running
2. In Program.cs update the AuthorityHost URL to use the tenant ID instead of common
3. Finally exclude all credential types except for InteractiveBrowserCredentials. That must be specifically included. 

### Extracting Bluesky Key from Local Client

In console

```javascript
const openRequest = indexedDB.open('@atproto-oauth-client');
```

```javscript
const sessionRequest = openRequest.result.transaction('session').objectStore('session').get('did:plc:penphldurhndgdxxn3ezvmoi');
```

```javascript
let privateKey = sessionRequest.result.value.dpopKey.keyPair.privateKey;
```

## Useful Links

- [Azure Maps Samples](https://samples.azuremaps.com/)
  - [Azure Maps Layer & Legend Control module](https://github.com/Azure-Samples/azure-maps-layer-legend)
    - [Legend Control documentation](https://github.com/Azure-Samples/azure-maps-layer-legend/blob/main/docs/legend_control.md)
  - [Azure Maps Spider Cluster module (forked)](https://github.com/golf1052/azure-maps-spider-clusters)

# Publishing NuGet Packages

1. `dotnet build -c Release`
2. `dotnet pack -c Release`
3. `dotnet nuget push <path to .nupkg> -k <NuGet API key>`

# Mobile App

## Identifier

com.golf1052.SeattleCarsInBikeLanes.Mobile

## Mobile platform parity

Mobile features must be delivered for iOS and Android together. A mobile change is complete only when:

- equivalent user outcomes work on both platforms, using platform-native APIs where their UX differs;
- both `net10.0-ios` and `net10.0-android` compile;
- shared behavior has automated coverage and each platform-specific path has been exercised on a simulator or device;
- equivalent Sentry telemetry uses the same metric names, units, and bounded attributes on both platforms, with `platform` identifying `ios` or `android`; and
- any intentional platform exception is documented in the mobile README and in the pull request.

Do not register a placeholder or no-op implementation for one mobile platform as a way to ship the other. If an operating-system limitation prevents exact behavior, implement the closest safe equivalent and document the difference.
