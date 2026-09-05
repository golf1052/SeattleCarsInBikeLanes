(function() {
    let nativeNotificationsEnabled = false;
    const pendingNativeSignOuts = new Set();

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
            pendingNativeSignOuts.add(provider);
            return;
        }

        window.location.href = `cibl-mobile://auth/signed-out?provider=${provider}`;
    }

    function enableNativeNotifications() {
        nativeNotificationsEnabled = true;
        Array.from(pendingNativeSignOuts).forEach(function(provider, index) {
            setTimeout(function() {
                notifyNativeSignedOut(provider);
            }, index * 50);
        });
        pendingNativeSignOuts.clear();
        return true;
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
        applySignedOut: applySignedOut,
        enableNativeNotifications: enableNativeNotifications
    });
})();
