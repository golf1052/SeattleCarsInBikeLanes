const TwitterUsername = 'carbikelanesea';

// Taken from https://stackoverflow.com/a/9039885/6681022
function isiOS() {
    return [
        'iPad Simulator',
        'iPhone Simulator',
        'iPod Simulator',
        'iPad',
        'iPhone',
        'iPod'
      ].includes(navigator.platform)
      // iPad on iOS 13 detection
      || (navigator.userAgent.includes("Mac") && "ontouchend" in document);
}

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

function uploadImage(files) {
    const data = new FormData();
    for (const file of files) {
        data.append('files', file);
    }
    return fetch(`api/Upload/Initial`, {
        method: 'POST',
        body: data
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

function getMastodonOEmbed(mastodonLink) {
    const width = window.innerWidth < 576 ? 220 : 400;
    const height = window.innerWidth < 576 ? 200 : 400;
    return fetch(`api/Mastodon/oembed?url=${mastodonLink}&width=${width}&height=${height}`)
    .then(response => {
        if (!response.ok) {
            throw new Error(`Error when fetching Mastodon oEmbed. ${response}`);
        }

        return response.text();
    })
    .then(response => {
        if (response) {
            return response;
        } else {
            return null;
        }
    });
}

function getOEmbed(properties) {
    if (properties.mastodonLink) {
        return getMastodonOEmbed(properties.mastodonLink);
    } else {
        return getTwitterOEmbed(getTweetId(properties));
    }
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

function getTweetId(properties) {
    if (!properties.twitterLink) {
        return properties.tweetId.split('.')[0];
    } else {
        const splitPathname = new URL(properties.twitterLink).pathname.split('/');
        return splitPathname[splitPathname.length - 1];
    }
}

function getMastodonOAuthUrl(endpoint) {
    return fetch('api/Mastodon/GetOAuthUrl', {
        method: 'POST',
        body: JSON.stringify({ serverUrl: endpoint }),
        headers: {
            'Content-Type': 'application/json'
        }
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

        return response.authUrl;
    });
}

function getMastodonUsername() {
    return fetch('api/Mastodon/GetMastodonUsername', {
        method: 'POST',
        body: JSON.stringify({
            serverUrl: localStorage.getItem('mastodonEndpoint'),
            accessToken: localStorage.getItem('mastodonAccessToken')
        }),
        headers: {
            'Content-Type': 'application/json'
        }
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

function showReportedItemPopup(properties, popup, map) {
    const position = atlas.data.Position.fromLatLng(properties.location.position);
    popup.setOptions({
        position: position,
        content: ''
    });
    popup.open(map);
    getOEmbed(properties)
    .then(html => {
        if (html) {
            popup.setOptions({
                content: `<div><div style="height: 20px; width: 100%;"></div>${html}</div>`
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
    element.innerHTML = text;
    return element;
}
