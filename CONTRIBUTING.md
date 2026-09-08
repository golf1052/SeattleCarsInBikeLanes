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

## Shared report uploads

The website and mobile app use the same two-stage API and report store. Photos
remain ordinary JPEG blobs; the server does not embed image bytes in report JSON.
There is no `FinalizeMobile` endpoint.

| Request | Purpose |
| --- | --- |
| `POST /api/Upload/Initial` | Analyze multipart `files` and return ordered prepared-photo metadata and preview URLs. |
| `POST /api/Upload/Finalize` | Accept a complete report and return its durable receipt. |
| `GET /api/Upload/Reports/{reportId}` | Reconcile an uncertain submission using its original receipt. |
| `GET /api/Upload/Limits` | Return shared location, photo-count, and processed-photo size limits. |

Create a random 32-character lowercase hexadecimal report ID before the first
upload, and send it as `X-Report-Id` on both POSTs. Keep it for every retry of that
logical report, even when the photos must be prepared again. Native callers also
send `X-Device-Id`; website callers do not need a device identity or social login.
Neither an installation ID nor a client-supplied attribution identity proves who
a person is.

Initial upload returns an array of `InitialPhotoUpload` objects. Their
`submissionId` identifies a preparation set, not the final accepted report. The
server reads EXIF capture metadata and falls back to bounded, entity-disabled
XMP parsing for missing date/GPS fields. Capture dates retain their camera
wall-clock time rather than being converted to the web server's time zone. The
shared finalization body is an envelope:

```json
{
  "photos": [
    {
      "photoId": "returned-prepared-photo-id",
      "submissionId": "returned-preparation-id",
      "photoNumber": 0,
      "photoDateTime": "2026-09-07T12:00:00",
      "photoLatitude": "47.60621",
      "photoLongitude": "-122.33207",
      "photoCrossStreet": "Pike St",
      "numberOfCars": 1
    }
  ],
  "attribution": {}
}
```

An empty attribution object explicitly requests anonymity, even if the request
also contains signed-in cookies. Attributed requests include `blueskyDid` and/or
`mastodonServer` plus `mastodonAccountId`. The server verifies the actual cookie,
bearer, or Mastodon credential against that selection. An explicitly supplied
native bearer cannot fall back to a different cookie identity. Credential
rejection and temporary provider unavailability are different failures.
Mastodon identity verification is limited to 64 KiB and a 15-second deadline
covering both response headers and body reads. A stalled provider is a retryable
outage, not a reason to drop attribution.

Success returns a `SubmissionReceipt` containing `reportId`, `submissionId`,
`submittedAt`, and the accepted `attribution`. The same logical report always
returns its original receipt once accepted, including after moderation/deletion.
A receipt is acceptance into moderation, not confirmation that social posts exist.
Do not infer acceptance from the completion of `Initial` or individual blob writes.

The website retains its files, report ID, and submitted values in the open page
while an outcome is uncertain. It checks the receipt before resending, and does
not adopt edits or a different account during that recovery. A definitively
rejected request can be corrected. Confirmed expired credentials let the website
user sign in again or explicitly turn attribution off; the mobile queue retains
its separately approved automatic anonymous-fallback behavior.

### Storage and recovery

Temporary analyzed images stay under `initialupload/`. All permanent report data
stays under the existing `finalizedupload/` prefix. New report records use
`finalizedupload/reports/<report-id>.json`, with separate immutable JPEGs under
`finalizedupload/photos/<report-id>/<attempt-id>/`. A conditional transition of
the report record is the acceptance boundary: moderation never discovers new
reports by listing individual JPEGs.

Preparing work, retry takeover, acceptance, and abandonment are fenced through
the same versioned report record. An old worker cannot commit after its attempt
is superseded. Prepared source versions and completed destination lengths/versions
are checked before acceptance. Credentials and administrator tokens must never
appear in durable report or recovery metadata.

The initial-upload pruner must not scan permanent photo storage. The report
cleanup service separately fences expired preparations before deleting their
files, collects superseded attempts, and retries cleanup after retirement.
Accepted and unresolved moderation records retain their photos. Retired records
retain their original receipt permanently, so late client retries cannot recreate
moderated reports. Do not manually age-delete report receipts.
Corrupt report records are logged and skipped during cleanup, together with any
photos they might reference; they are not treated as missing reports. Repair the
record before expecting its cleanup or moderation to proceed.

### Existing moderation data

Existing `finalizedupload/<photo-id>.jpeg/.json` submissions remain available
without a destructive bulk migration. The Admin Panel groups their stored
metadata by original submission ID. Before the first moderation action, it
conditionally adopts the complete server-loaded group into the shared report
model, retaining the existing JPEGs and original submission identity.
The per-photo reader only examines flat JSON files directly under
`finalizedupload/`; nested report records are read by the shared report store.

Shared records, including retired tombstones, suppress legacy entries in the
pending list. Legacy files are removed only through retirement cleanup; a cleanup
failure must not make a previously moderated submission appear again. Missing or
unreadable legacy files require operator attention rather than silent omission.
This stored-data adapter is not compatibility support for old API request bodies.

### Blocking device submissions

On `/AdminPage`, choose **Block device** next to a pending report's device ID.
The confirmation modal requires a reason (1-1,000 characters after trimming).
Confirming saves the device and reason in Cosmos DB. The **Blocked devices**
section lists those reasons and provides a confirmed **Unblock** action, even
after the source report has been published or deleted. Unblocking removes the
active record and its reason; this is not a historical audit log.

The reason is administrator-only plain text. It is never sent to the mobile app,
included in receipts, or published with reports. Device IDs are case-sensitive
installation identifiers supplied by clients, not verified identities. Reports
without a device ID have no block action.

Blocking stops new preparations and new report finalizations with the existing
HTTP 403 response. It does not remove accepted reports, prevent publishing or
deletion, or stop a device from recovering an already accepted receipt. Requests
that already passed their device check are not canceled by a subsequent block.

Submission checks use an uncached point read against the exact device ID and
partition key. A normal new mobile report makes up to two logical reads, plus any
retries; website requests without a device ID make none. Caching could reduce
request units for repeated IDs, but is deliberately omitted for simplicity and to
avoid a cache delay after administrative changes. Actual cost depends on request
volume and the Cosmos account's capacity model, not how often devices are blocked.
Visibility still follows the account's consistency configuration.

Blocklist read failures are logged and **fail open only for submission checks**.
The admin list and mutations do not fail open: they show errors instead of claiming
that the list is empty or that an unconfirmed change succeeded. Refresh blocked
devices after an uncertain response to reconcile the saved state. Blocking and
unblocking do not discard edits to pending reports.

#### Cosmos setup before deployment

1. In the existing `seattle-carsinbikelanes-db` Cosmos account, create an empty
   `blocked-devices` container under the `seattle` database, with partition-key
   path `/id` and TTL disabled. This is a new container, not a database or account.
2. Use the account's appropriate existing capacity model. Check whether database
   throughput is shared before selecting container throughput; do not assume shared
   throughput or allocate a new fixed RU/s budget by default.
3. Grant the deployed managed identity and authorized local developers Cosmos
   data-plane access to read/query/upsert/delete items in this container (for
   example, Cosmos DB Built-in Data Contributor scoped to the container).
   Existing `items`-scoped permissions do not automatically cover it. Reuse
   `DefaultAzureCredential`; no new keys or secrets are needed.
4. Confirm that the admin list and a temporary block/unblock work against the
   intended nonproduction container before rollout. Successful uploads alone
   cannot confirm configuration because submission checks fail open. Verify the
   deployed application's list and mutation access before relying on blocking.

The application obtains the container with `GetContainer`; it does not create
Azure resources at startup. Items have this shape, with the ID also serving as the
partition-key value:

```json
{
  "id": "example-installation-id",
  "reason": "Repeated unrelated photo submissions."
}
```

There are no existing blocked devices to import. `blockeddevices.json` is no longer
read, and there is no blob fallback or dual-write mode. Photos and report receipts
still use their existing blob storage. A rollback to the blob-based implementation
would not enforce blocks added in Cosmos; preserve those records and account for
them explicitly before rolling back. This storage change does not require mobile
API changes; retain the Cosmos implementation when integrating the mobile branch.

### Retrying failed publication

Imgur uploads finish first. Mastodon, Bluesky, and Threads then publish
concurrently; Mastodon and Bluesky receive independent read-only streams over
the same JPEG buffers. All three tasks finish before any database/feed writes
or failed-attempt release, so a failed provider cannot leave another provider
still posting after the report becomes available for retry.

Publication and deletion acquire conditional ownership of the whole report.
Stale edits or an overlapping request must reload the current state. Metadata edits
are a separate moderation snapshot and do not rewrite the submission receipt.

When a publishing request fails, it releases ownership and keeps the report and
its photos pending. We check Bluesky, Mastodon and Threads, delete any posts that
succeeded, check the logs and fix the cause, then click Upload again. The panel
keeps our edited form and receives the current report version for the retry.
Social publishing is never retried automatically.

Publication writes use the stable report ID for the database item and each feed
entry. A retry replaces earlier database/feed results rather than conflicting
with an existing database row or duplicating RSS after an Atom write failure.
Photo retirement happens only after publication, database, and feed work
complete. If storage is unavailable, the panel cannot safely confirm the report
state and requires a refresh after that storage error is fixed.

Submission deduplication is separate from social posting: we check and remove
any posts that succeeded before retrying publication.

### Validation and deployment order

Use the existing server and shared Core test projects directly; building the
website does not require mobile workloads. `SeattleCarsInBikeLanes.Tests/TestFiles/UploadFlow.html`
also exercises the production browser upload helpers against an in-memory fake
server. Serve it and its relative JavaScript dependencies from the repository
root on a local HTTP server; it never submits real reports.
`SeattleCarsInBikeLanes.Tests/TestFiles/AdminDeviceBlocks.html` similarly exercises
the production admin page, confirmation modals, and block/unblock recovery against
an in-memory fake API. It does not use Azure or submit real moderation actions.

The standalone server PR updates the website and API contract together. There is
no old array-body adapter, `FinalizeMobile` alias, or browser offline queue.
Pause moderation during deployment so old server instances cannot publish legacy
reports outside the new ownership protocol. Refresh website/admin pages after
the deployment has completed on all instances.
PR workflow runs build and test but do not deploy. Merge/deploy the shared backend
before updating the mobile branch to use it; a green PR workflow with a skipped
deploy job is not evidence that the server is live.

After deployment, merge main into the existing mobile branch and update its
upload URLs, preparation report-ID propagation, shared request/error types, and
tests. Remove the abandoned mobile-only server/bundle code during that merge
without discarding queued reports, retained credentials, receipt-first local
acknowledgement, or either platform's photo recovery. Exercise the combined flow
against a matching test backend before using it for real reports.
