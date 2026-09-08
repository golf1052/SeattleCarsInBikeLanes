async function adminRequest(path, options = {}, recovery = {}) {
    let response;
    let text;
    try {
        response = await fetch(`api/AdminPage/${path}`, { cache: 'no-store', ...options });
        text = await response.text();
    } catch (cause) {
        if (!(cause instanceof TypeError) && cause?.name !== 'AbortError') throw cause;
        const error = new Error(recovery.network || 'The server could not be reached. Refresh pending reports to check whether the operation completed before retrying.');
        error.cause = cause;
        error.refreshRequired = true;
        throw error;
    }
    let body;
    try {
        body = text ? JSON.parse(text) : null;
    } catch (cause) {
        if (!(cause instanceof SyntaxError)) throw cause;
        if (response.headers.get('content-type')?.includes('json')) {
            const error = new Error(recovery.unreadable || 'The site returned an unreadable response. Refresh pending reports before retrying.');
            error.cause = cause;
            error.refreshRequired = true;
            throw error;
        }
        body = text;
    }
    if (!response.ok) {
        const message = typeof body === 'string' ? body :
            body?.message || body?.detail || (body?.errors && Object.values(body.errors).flat().join(' ')) || body?.title;
        const error = new Error(message || `Request failed (${response.status}). ${recovery.failed || 'Refresh before retrying.'}`);
        error.status = response.status;
        error.retryAllowed = body?.retryAllowed === true && typeof body.reportVersion === 'string' && body.reportVersion.length > 0;
        error.reportVersion = error.retryAllowed ? body.reportVersion : null;
        error.refreshRequired = !error.retryAllowed &&
            (response.status === 409 || response.status === 404 || response.status >= 500);
        throw error;
    }
    if (recovery.expectedStatus && response.status !== recovery.expectedStatus) {
        const error = new Error(recovery.unreadable);
        error.refreshRequired = true;
        throw error;
    }
    return body;
}

function adminJsonRequest(path, method, body, recovery = {}) {
    return adminRequest(path, {
        method,
        body: JSON.stringify(body),
        headers: { 'Content-Type': 'application/json' }
    }, recovery);
}

const deviceBlockRecovery = {
    network: 'The server could not be reached. Refresh blocked devices to check the current status before retrying.',
    unreadable: 'The site returned an unexpected response. Refresh blocked devices to check the current status before retrying.',
    failed: 'Refresh blocked devices before retrying.'
};

function getBlockedDevices() {
    return adminRequest('BlockedDevices', {}, { ...deviceBlockRecovery, expectedStatus: 200 });
}

function blockDevice(deviceId, reason) {
    return adminJsonRequest('BlockedDevices', 'POST', { deviceId, reason },
        { ...deviceBlockRecovery, expectedStatus: 204 });
}

function unblockDevice(deviceId) {
    return adminJsonRequest('BlockedDevices', 'DELETE', { deviceId },
        { ...deviceBlockRecovery, expectedStatus: 204 });
}

function getBlueskySession() {
    return adminRequest('GetBlueskySession');
}

function getPendingPhotos() {
    return adminRequest('PendingPhotos');
}

function uploadTweet(report) {
    return adminJsonRequest('UploadTweet', 'POST', report);
}

function postTweet(link, body, images, tweetLink, quoteTweetLink, blueskyDid, blueskyAccessJwt) {
    return adminJsonRequest('PostTweet', 'POST', {
        postUrl: link,
        tweetBody: body,
        tweetImages: images,
        tweetLink,
        quoteTweetLink,
        blueskyDid,
        blueskyAccessJwt
    });
}

function deletePendingPhoto(report) {
    return adminJsonRequest('DeletePendingPhoto', 'DELETE', report);
}

function deletePost(identifier) {
    return adminJsonRequest('DeletePost', 'DELETE', { postIdentifier: identifier });
}

function postMonthlyStats(link) {
    return adminJsonRequest('PostMonthlyStats', 'POST', { postIdentifier: link });
}

function displayError(text) {
    document.getElementById('alertDiv')?.remove();
    const alertDiv = document.createElement('div');
    alertDiv.className = 'alert alert-danger';
    alertDiv.setAttribute('role', 'alert');
    alertDiv.id = 'alertDiv';
    alertDiv.append(text);
    document.body.prepend(alertDiv);
}
