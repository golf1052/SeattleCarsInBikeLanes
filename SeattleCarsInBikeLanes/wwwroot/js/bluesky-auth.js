// Sign in with Bluesky.
//
// The server is the OAuth client, so nothing here ever handles a Bluesky token. We ask the server
// to start an authorization request, hand the browser off to Bluesky, and the server sets a session
// cookie when the user comes back. All this file does is drive the UI and remember who is signed in.

window.blueskyHandle = null;
window.blueskyUserDid = null;

const blueskySignInButton = document.getElementById('blueskySignInButton');
const blueskyLogoutButton = document.getElementById('blueskyLogoutButton');
const blueskyHandleInput = document.getElementById('blueskyHandleInput');
const blueskyNextButton = document.getElementById('blueskyNextButton');
const blueskyModalAlertDiv = document.getElementById('blueskyModalAlertDiv');

function setBlueskyLoggedIn(handle, did) {
    window.blueskyHandle = handle;
    window.blueskyUserDid = did;

    blueskySignInButton.setAttribute('disabled', '');
    blueskySignInButton.innerText = `Signed in as @${handle}`;
    blueskyLogoutButton.className = 'dropdown-item';
}

function setBlueskyLoggedOut() {
    window.blueskyHandle = null;
    window.blueskyUserDid = null;

    blueskySignInButton.removeAttribute('disabled');
    blueskySignInButton.innerText = 'Sign in with Bluesky';
    blueskyLogoutButton.className = 'dropdown-item disabled';
}

function showBlueskyModalError(message) {
    blueskyModalAlertDiv.innerHTML = '';
    const alert = document.createElement('div');
    alert.className = 'alert alert-danger';
    alert.setAttribute('role', 'alert');
    alert.innerText = message;
    blueskyModalAlertDiv.append(alert);
}

function clearBlueskyModalError() {
    blueskyModalAlertDiv.innerHTML = '';
}

// The callback redirects here with an error message when sign in doesn't complete.
function showBlueskyRedirectError() {
    const url = new URL(window.location.href);
    const error = url.searchParams.get('blueskyError');
    if (!error) {
        return;
    }

    showBlueskyModalError(error);
    const modal = new bootstrap.Modal(document.getElementById('blueskyHandleModal'));
    modal.show();

    // Don't leave the error in the address bar for a refresh to replay.
    url.searchParams.delete('blueskyError');
    window.history.replaceState({}, '', url);
}

function checkBlueskyAuth() {
    return fetch('/api/BlueskyAuth/me')
    .then(response => response.ok ? response.json() : { loggedIn: false })
    .then(response => {
        if (response.loggedIn) {
            setBlueskyLoggedIn(response.handle, response.did);
        } else {
            setBlueskyLoggedOut();
        }
        return response;
    })
    .catch(() => {
        setBlueskyLoggedOut();
        return { loggedIn: false };
    });
}

function loginWithBluesky() {
    const handle = blueskyHandleInput.value.trim();
    if (!handle) {
        showBlueskyModalError('Enter your Bluesky handle.');
        return;
    }

    clearBlueskyModalError();
    changeButtonToLoadingButton(blueskyNextButton, 'Login');

    fetch('/api/BlueskyAuth/login', {
        method: 'POST',
        headers: { 'Content-Type': 'application/json' },
        body: JSON.stringify({ handle: handle })
    })
    .then(response => response.json().then(body => ({ ok: response.ok, body: body })))
    .then(result => {
        if (!result.ok) {
            throw new Error(result.body?.message ?? 'Could not start Bluesky login.');
        }
        window.location.href = result.body.authUrl;
    })
    .catch(error => {
        showBlueskyModalError(error.message);
        changeLoadingButtonToRegularButton(blueskyNextButton, 'Login');
    });
}

function clearBlueskyAuth(notifyNative = true) {
    return fetch('/api/BlueskyAuth/logout', { method: 'POST' })
    .catch(() => { /* Signing out locally is what matters. */ })
    .then(() => {
        setBlueskyLoggedOut();
        if (notifyNative) {
            window.dispatchEvent(new CustomEvent('carsInBikeLanesAuthChanged', {
                detail: { provider: 'bluesky', signedIn: false }
            }));
        }
    });
}

blueskyNextButton.addEventListener('click', function() {
    loginWithBluesky();
});

blueskyHandleInput.addEventListener('keydown', function(event) {
    if (event.key === 'Enter') {
        loginWithBluesky();
    }
});

blueskyLogoutButton.addEventListener('click', function() {
    clearBlueskyAuth();
});

setBlueskyLoggedOut();
showBlueskyRedirectError();
window.blueskyAuthReady = checkBlueskyAuth();
