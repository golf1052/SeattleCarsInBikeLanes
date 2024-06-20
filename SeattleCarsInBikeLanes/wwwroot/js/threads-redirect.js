function displayError(errorMessage) {
    const errorElement = document.createElement('h1');
    errorElement.append(errorMessage);
    document.getElementsByTagName('body')[0].append(errorElement);
}

const url = new URL(window.location.href);
if (url.searchParams.has('code')) {
    fetch(`api/Threads/Redirect?code=${url.searchParams.get('code')}`, {
        method: 'POST'
    })
    .then(response => {
        if (!response.ok) {
            throw new Error(`Failed to get access token from Threads ${response}`);
        }

        return response.json();
    })
    .then(response => {
        localStorage.setItem('threadsAccessToken', response.accessToken);
        localStorage.setItem('threadsRefreshToken', response.refreshToken);
        localStorage.setItem('threadsExpiresAt', response.expiresAt);
        window.location.href = '/';
    })
    .catch(error => {
        displayError(error.message);
    });
} else {
    displayError('Redirect failed, no code from Threads');
}
