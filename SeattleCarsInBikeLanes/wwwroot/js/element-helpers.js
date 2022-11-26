function changeButtonToLoadingButton(button, loadingText) {
    button.innerHTML = '';
    button.setAttribute('disabled', '');
    const spinner = document.createElement('span');
    spinner.className = 'spinner-border spinner-border-sm';
    spinner.setAttribute('role', 'status');
    spinner.setAttribute('aria-hidden', 'true');
    button.append(spinner);
    button.append(` ${loadingText}`);
}

function changeLoadingButtonToRegularButton(button, regularText) {
    button.innerHTML = '';
    button.append(regularText);
    button.removeAttribute('disabled');
}
