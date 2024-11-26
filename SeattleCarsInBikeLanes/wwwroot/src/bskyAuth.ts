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
        getAuthInfo(result.session.did)
        .then((authInfo) => {
            (window as any).blueskyAuthInfo = authInfo;
            return getPlcDirectoryResponse(result.session.did);
        })
        .then((resolveDidResponse: ResolveDidResponse) => {
            const handle = getHandleFromResolveDidResponse(resolveDidResponse);
            if (handle.startsWith('did:plc:')) {
                // Don't login the Bluesky user
                delete (window as any).blueskyAuthInfo;
            } else {
                (window as any).blueskyHandle = handle;
                (window as any).blueskyUserDid = result.session.did;
                (window as any).blueskyPds = getPdsFromResolveDidResponse(resolveDidResponse);
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

interface BlueskyAuthInfo {
    keyId: string;
    privateKey: string;
    accessToken: string;
}

function getAuthInfo(did: string): Promise<BlueskyAuthInfo> {
    const openRequest = indexedDB.open('@atproto-oauth-client');
    let authInfo = {} as BlueskyAuthInfo;
    return new Promise((resolve, reject) => {
        openRequest.onsuccess = function() {
            resolve(openRequest.result);
        };
        openRequest.onerror = function() {
            reject(openRequest.error);
        };
    })
    .then((db: IDBDatabase) => {
        const transaction = db.transaction('session');
        const objectStore = transaction.objectStore('session');
        const sessionRequest = objectStore.get(did);
        return new Promise((resolve, reject) => {
            sessionRequest.onsuccess = function() {
                resolve(sessionRequest.result);
            };
            sessionRequest.onerror = function() {
                reject(sessionRequest.error);
            };
        });
    })
    .then((session: any) => {
        authInfo.keyId = session.value.dpopKey.keyId;
        authInfo.accessToken = session.value.tokenSet.access_token;
        const signingKey: CryptoKey = session.value.dpopKey.keyPair.privateKey;
        return crypto.subtle.exportKey('pkcs8', signingKey);
    })
    .then((keyData: ArrayBuffer) => {
        const stringVersion = String.fromCharCode.apply(null, new Uint8Array(keyData));
        const base64Version = btoa(stringVersion);
        authInfo.privateKey = `-----BEGIN PRIVATE KEY-----\n${base64Version}\n-----END PRIVATE KEY-----`;
        return authInfo;
    });
}

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

function getPlcDirectoryResponse(did: string): Promise<ResolveDidResponse> {
    return fetch(`https://plc.directory/${did}`)
    .then((response) => {
        return response.json();
    })
    .then((response: ResolveDidResponse) => {
        return response;
    });
}

function getHandleFromResolveDidResponse(response: ResolveDidResponse): string {
    if (response.alsoKnownAs.length > 0) {
        if (response.alsoKnownAs.length === 1) {
            return response.alsoKnownAs[0].substring(5);
        }
        return response.alsoKnownAs.find(h => h.startsWith('at://')).substring(5);
    }
    return response.id;
}

function getPdsFromResolveDidResponse(response: ResolveDidResponse): string {
    if (response.service.length > 0) {
        return response.service[0].serviceEndpoint;
    } else {
        throw new Error('No PDS found in DID document');
    }
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
