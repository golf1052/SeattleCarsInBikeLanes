export function changeButtonToLoadingButton(button: HTMLButtonElement, loadingText: string) {
    button.innerHTML = '';
    button.setAttribute('disabled', '');
    const spinner = document.createElement('span');
    spinner.className = 'spinner-border spinner-border-sm';
    spinner.setAttribute('role', 'status');
    spinner.setAttribute('aria-hidden', 'true');
    button.append(spinner);
    button.append(` ${loadingText}`);
}

export function changeLoadingButtonToRegularButton(button: HTMLButtonElement, regularText: string) {
    button.innerHTML = '';
    button.append(regularText);
    button.removeAttribute('disabled');
}
