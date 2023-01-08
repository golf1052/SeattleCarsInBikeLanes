function getPendingPhotos() {
    return fetch('api/AdminPage/PendingPhotos')
    .then(response => {
        if (!response.ok) {
            return response.text();
        }

        return response.json();
    })
    .then(response => {
        if (typeof response === 'string') {
            throw new Error(response);
        }

        return response;
    });
}

function uploadTweet(metadata) {
    return fetch('api/AdminPage/UploadTweet', {
        method: 'POST',
        body: JSON.stringify(metadata),
        headers: {
            'Content-Type': 'application/json'
        }
    })
    .then(response => {
        if (!response.ok) {
            return response.text();
        }

        return null;
    })
    .then(response => {
        if (typeof response === 'string') {
            throw new Error(response);
        }
    });
}

function postTweet(link) {
    return fetch('api/AdminPage/PostTweet', {
        method: 'POST',
        body: JSON.stringify({ postUrl: link }),
        headers: {
            'Content-Type': 'application/json'
        }
    })
    .then(response => {
        if (!response.ok) {
            return response.text();
        }

        return null;
    })
    .then(response => {
        if (typeof response === 'string') {
            throw new Error(response);
        }
    });
}

function deletePendingPhoto(metadata) {
    return fetch('api/AdminPage/DeletePendingPhoto', {
        method: 'DELETE',
        body: JSON.stringify(metadata),
        headers: {
            'Content-Type': 'application/json'
        }
    });
}

function deletePost(identifier) {
    return fetch('api/AdminPage/DeletePost', {
        method: 'DELETE',
        body: JSON.stringify({ postIdentifier: identifier }),
        headers: {
            'Content-Type': 'application/json'
        }
    });
}

function postMonthlyStats(link) {
    return fetch('api/AdminPage/PostMonthlyStats', {
        method: 'POST',
        body: JSON.stringify({ postIdentifier: identifier }),
        headers: {
            'Content-Type': 'application/json'
        }
    })
    .then(response => {
        if (!response.ok) {
            return response.text();
        }

        return null;
    })
    .then(response => {
        if (typeof response === 'string') {
            throw new Error(response);
        }
    });
}

function displayError(text) {
    const oldAlertDiv = document.getElementById('alertDiv');
    if (oldAlertDiv) {
        oldAlertDiv.remove();
    }

    const alertDiv = document.createElement('div');
    alertDiv.className = 'alert alert-danger';
    alertDiv.setAttribute('role', 'alert');
    alertDiv.setAttribute('id', 'alertDiv');
    alertDiv.append(text);
    document.getElementsByTagName('body')[0].append(alertDiv);
}
