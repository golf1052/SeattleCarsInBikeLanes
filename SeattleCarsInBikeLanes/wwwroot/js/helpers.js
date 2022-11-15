const TwitterUsername = 'carbikelanesea';

function getAllReportedItems() {
    const url = 'api/reporteditems/all';
    return fetch(url)
    .then(response => {
        if (!response.ok) {
            throw new Error(`Error when fetching all reported items. ${response}`);
        }

        return response.json();
    })
    .then(response => {
        return response;
    });
}

function searchReportedItems(searchParams) {
    return fetch(`api/reporteditems/search?${searchParams.toString()}`)
    .then(response => {
        if (!response.ok) {
            throw new Error(`Error when fetching searched reported items. ${response}`);
        }

        return response.json();
    })
    .then(response => {
        return response;
    });
}

function uploadImage(file) {
    return fetch(`api/Upload/Initial`, {
        method: 'POST',
        body: file
    })
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

function finalizeUploadImage(metadata) {
    return fetch('api/Upload/Finalize', {
        method: 'POST',
        body: JSON.stringify(metadata),
        headers: {
            'Content-Type': 'application/json'
        }
    })
    .then(response => {
        if (!response.ok) {
            throw new Error(`Error when finalizing image upload. ${response}`);
        }
    });
}

function createReportedItemFeatureCollection(reportedItems) {
    const features = reportedItems.map(i => {
        const position = atlas.data.Position.fromLatLng(i.location.position);
        const point = new atlas.data.Point(position);
        return new atlas.data.Feature(point, i);
    });

    return new atlas.data.FeatureCollection(features);
}

function createDataSource(reportedItems) {
    const dataSourceOptions = {
        cluster: true,
        clusterRadius: 40,
        maxZoom: 25,
        clusterMaxZoom: 25
    };
    const source = new atlas.source.DataSource(null, dataSourceOptions);
    source.add(createReportedItemFeatureCollection(reportedItems));
    return source;
}

function getTwitterOEmbed(tweetId) {
    return fetch(`api/Twitter/oembed?tweetId=${tweetId}`)
    .then(response => {
        if (!response.ok) {
            throw new Error(`Error when fetching Twitter oEmbed. ${response}`);
        }

        return response.json();
    })
    .then(response => {
        if (response) {
            return response.html;
        } else {
            return null;
        }
    });
}

function refreshTwitterToken(refreshToken) {
    return fetch('api/Twitter/RefreshToken', {
        method: 'POST',
        body: JSON.stringify({ refreshToken: refreshToken }),
        headers: {
            'Content-Type': 'application/json'
        }
    })
    .then(response => {
        if (!response.ok) {
            throw new Error(`Failed to refresh Twitter access token ${response}`);
        }

        return response.json();
    })
    .then(response => {
        localStorage.setItem('twitterAccessToken', response.accessToken);
        localStorage.setItem('twitterRefreshToken', response.refreshToken);
        localStorage.setItem('twitterExpiresAt', response.expiresAt);
    });
}

function getTwitterUsername() {
    return fetch('api/Twitter/GetTwitterUsername', {
        method: 'POST',
        body: JSON.stringify({ accessToken: localStorage.getItem('twitterAccessToken') }),
        headers: {
            'Content-Type': 'application/json'
        }
    })
    .then(response => {
        if (!response.ok) {
            throw new Error(`Failed to get logged in Twitter username ${response}`);
        }

        return response.json();
    })
    .then(response => {
        return response;
    });
}

function getTweetId(databaseTweetId) {
    return databaseTweetId.split('.')[0];
}

function showReportedItemPopup(properties, popup, map) {
    const position = atlas.data.Position.fromLatLng(properties.location.position);
    popup.setOptions({
        position: position,
        content: ''
    });
    popup.open(map);
    const tweetId = getTweetId(properties.tweetId);
    getTwitterOEmbed(tweetId)
    .then(html => {
        if (html) {
            popup.setOptions({
                content: html
            });
            twttr.ready(twttr => {
                twttr.widgets.load();
            });
        }
    });
}

function createAlertBanner(text) {
    const element = document.createElement('div');
    element.className = 'alert alert-danger';
    element.setAttribute('role', 'alert');
    element.append(text);
    return element;
}
