(function() {
    const mobileAuthStorageKey = 'carsInBikeLanesMobileAuth';
    const mobileAuthRequested =
        new URLSearchParams(window.location.search).get('mobileAuth') === '1';
    if (mobileAuthRequested) {
        sessionStorage.setItem(mobileAuthStorageKey, '1');
    }

    const nativeNotificationsEnabled =
        mobileAuthRequested || sessionStorage.getItem(mobileAuthStorageKey) === '1';

    function getProviderConfig(provider) {
        if (provider === 'bluesky') {
            return {
                modalId: 'blueskyHandleModal',
                applySignedOut: function() {
                    setBlueskyLoggedOut();
                }
            };
        }

        if (provider === 'mastodon') {
            return {
                modalId: 'mastodonServerModal',
                applySignedOut: function() {
                    clearMastodonAuth(false);
                }
            };
        }

        return null;
    }

    function openSignIn(provider) {
        const config = getProviderConfig(provider);
        const modal = config && document.getElementById(config.modalId);
        if (!modal || !window.bootstrap || !bootstrap.Modal) {
            return false;
        }

        bootstrap.Modal.getOrCreateInstance(modal).show();
        return true;
    }

    function applySignedOut(provider) {
        const config = getProviderConfig(provider);
        if (!config) {
            return false;
        }

        try {
            config.applySignedOut();
            return true;
        } catch {
            return false;
        }
    }

    function notifyNativeSignedOut(provider) {
        if (!nativeNotificationsEnabled) {
            return;
        }

        window.location.href = `cibl-mobile://auth/signed-out?provider=${provider}`;
    }

    window.addEventListener('carsInBikeLanesAuthChanged', function(event) {
        const detail = event.detail;
        const provider = detail && detail.provider;
        if (!detail ||
            detail.signedIn !== false ||
            (provider !== 'bluesky' && provider !== 'mastodon')) {
            return;
        }

        notifyNativeSignedOut(provider);
    });

    window.carsInBikeLanesMobileAuth = Object.freeze({
        openSignIn: openSignIn,
        applySignedOut: applySignedOut
    });

    for (const provider of ['bluesky', 'mastodon']) {
        const modal = document.getElementById(getProviderConfig(provider).modalId);
        modal?.addEventListener('shown.bs.modal', function() {
            if (nativeNotificationsEnabled) {
                window.location.href = `cibl-mobile://auth/sign-in-started?provider=${provider}`;
            }
        });
    }
})();
