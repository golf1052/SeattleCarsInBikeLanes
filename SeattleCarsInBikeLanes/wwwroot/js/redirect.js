function displayError(errorMessage) {
    const errorElement = document.createElement('h1');
    errorElement.append(errorMessage);
    document.getElementsByTagName('body')[0].appendChild(errorElement);
}

const url = new URL(window.location.href);
if (url.searchParams.has('code')) {
    fetch(`api/Twitter/Redirect?code=${url.searchParams.get('code')}`, {
        method: 'POST'
    })
    .then(response => {
        if (!response.ok) {
            throw new Error(`Failed to get access token from Twitter ${response}`);
        }

        return response.json();
    })
    .then(response => {
        localStorage.setItem('twitterAccessToken', response.accessToken);
        localStorage.setItem('twitterRefreshToken', response.refreshToken);
        localStorage.setItem('twitterExpiresAt', response.expiresAt);
        window.location.href = '/';
    })
    .catch(error => {
        displayError(error.message);
    });
} else {
    displayError('Redirect failed, no code from Twitter');
}
