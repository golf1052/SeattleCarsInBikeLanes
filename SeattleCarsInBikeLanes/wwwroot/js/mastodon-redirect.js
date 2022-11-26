function displayError(errorMessage) {
    const errorElement = document.createElement('h1');
    errorElement.append(errorMessage);
    document.getElementsByTagName('body')[0].append(errorElement);
}

const url = new URL(window.location.href);
if (url.searchParams.has('code')) {
    fetch(`api/Mastodon/Redirect?code=${url.searchParams.get('code')}`, {
        method: 'POST',
        body: JSON.stringify({ serverUrl: localStorage.getItem('mastodonEndpoint') }),
        headers: {
            'Content-Type': 'application/json'
        }
    })
    .then(response => {
        if (!response.ok) {
            throw new Error(`Failed to get access token from Mastodon ${response}`);
        }

        return response.json();
    })
    .then(response => {
        localStorage.setItem('mastodonAccessToken', response.accessToken);
        window.location.href = '/';
    })
    .catch(error => {
        displayError(error.message);
    });
} else {
   displayError('Redirect failed, no code from Mastodon');
}
