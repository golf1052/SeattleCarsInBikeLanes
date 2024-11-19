import { BrowserOAuthClient, OAuthSession, TokenInvalidError, TokenRefreshError, TokenRevokedError } from '@atproto/oauth-client-browser';
import * as elementHelpers from './element-helpers';

const blueskySignInButton = document.getElementById('blueskySignInButton') as HTMLButtonElement;
const blueskyLogoutButton: HTMLAnchorElement = document.getElementById('blueskyLogoutButton') as HTMLAnchorElement;
const blueskyNextButton: HTMLButtonElement = document.getElementById('blueskyNextButton') as HTMLButtonElement;

let bskySub: string | null = null;

const client = new BrowserOAuthClient({
    clientMetadata: {
        "client_id": "https://seattle.carinbikelane.com/client-metadata.json",
        "client_name": "Seattle Cars in Bike Lanes",
        "client_uri": "https://seattle.carinbikelane.com",
        "redirect_uris": ["https://seattle.carinbikelane.com/"],
        "scope": "atproto transition:generic",
        "grant_types": ["authorization_code", "refresh_token"],
        "response_types": ["code"],
        "token_endpoint_auth_method": "none",
        "application_type": "web",
        "dpop_bound_access_tokens": true
    },
    handleResolver: "https://bsky.social"
});

client.addEventListener('deleted', (event: CustomEvent<{ sub: string, cause: TokenRefreshError | TokenRevokedError | TokenInvalidError }>) => {
    const { sub, cause } = event.detail;
    clearBlueskyAuth();
});

const clientInitPromise = client.init();

const sessionStatePromise = clientInitPromise.then((result: undefined | { session: OAuthSession, state?: string }) => {
    if (result) {
        if (result.state !== null) {
            // console.log("Logged in as " + result.session.sub);
        } else {
            // console.log(`Restored session for ${result.session.sub}`);
        }
        getHandleFromDid(result.session.did)
        .then((handle) => {
            if (handle.startsWith('did:plc:')) {
                // Don't login the Bluesky user
            } else {
                (window as any).blueskyHandle = handle;
                (window as any).blueskyUserDid = result.session.did;
                bskySub = result.session.sub;

                blueskySignInButton.setAttribute('disabled', '');
                blueskySignInButton.innerText = 'Logged in with Bluesky';

                blueskyLogoutButton.className = 'dropdown-item';
            }
        });
    } else {
        blueskyLogoutButton.className = 'dropdown-item disabled';
    }
});

interface ResolveDidResponse {
    id: string;
    alsoKnownAs: string[];
    verificationMethods: {
        id: string;
        type: string;
        controller: string;
        publicKeyMultibase: string;
    }[];
    service: {
        id: string;
        type: string;
        serviceEndpoint: string;
    }[];
};

function getHandleFromDid(did: string): Promise<string> {
    return fetch(`https://plc.directory/${did}`)
    .then((response) => {
        return response.json();
    })
    .then((response: ResolveDidResponse) => {
        if (response.alsoKnownAs.length > 0) {
            if (response.alsoKnownAs.length === 1) {
                return response.alsoKnownAs[0].substring(5);
            }
            return response.alsoKnownAs.find(h => h.startsWith('at://')).substring(5);
        }
        return did;
    });
}

function login(handle: string) {
    try {
        client.signIn(handle)
        .catch((error: any) => {
            console.error(error);
            elementHelpers.changeLoadingButtonToRegularButton(blueskyNextButton, 'Login');
        });
    } catch (error) {
        console.error(error);
        elementHelpers.changeLoadingButtonToRegularButton(blueskyNextButton, 'Login');
    }
}

function loginWithBluesky() {
    const blueskyHandle = document.getElementById('blueskyHandleInput') as HTMLInputElement;
    const handle = blueskyHandle.value;
    elementHelpers.changeButtonToLoadingButton(blueskyNextButton, 'Login');
    login(handle);
    elementHelpers.changeLoadingButtonToRegularButton(blueskyNextButton, 'Login');
}

function clearBlueskyAuth(): void {
    client.revoke(bskySub)
    .then(() => {
        delete (window as any).blueskyHandle;
        delete (window as any).blueskyUserDid;
        bskySub = null;

        blueskySignInButton.removeAttribute('disabled');
        blueskySignInButton.innerText = 'Sign in with Bluesky';

        blueskyLogoutButton.className = 'dropdown-item disabled';
    });
}

blueskyNextButton.addEventListener('click', () => {
    loginWithBluesky();
});

blueskyNextButton.addEventListener('keydown', function(event) {
    if (event.key === 'Enter') {
        loginWithBluesky();
    }
});

blueskyLogoutButton.addEventListener('click', () => {
    clearBlueskyAuth();
});
