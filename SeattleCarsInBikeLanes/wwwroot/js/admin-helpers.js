function getStatus() {
    return fetch('api/Status')
    .then(response => {
        return response.json();
    });
}

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
    return fetch('api/AdminPage/DeletePendingPhoto',  {
        method: 'DELETE',
        body: JSON.stringify(metadata),
        headers: {
            'Content-Type': 'application/json'
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
