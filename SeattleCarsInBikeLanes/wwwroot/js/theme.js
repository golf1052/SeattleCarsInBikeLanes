(function () {
    const colorScheme = window.matchMedia('(prefers-color-scheme: dark)');
    const listeners = new Set();

    function getTheme() {
        return colorScheme.matches ? 'dark' : 'light';
    }

    function applyTheme() {
        const theme = getTheme();
        document.documentElement.setAttribute('data-bs-theme', theme);
        document.documentElement.style.colorScheme = theme;
        return theme;
    }

    function onColorSchemeChanged() {
        const theme = applyTheme();
        listeners.forEach(listener => listener(theme));
    }

    if (colorScheme.addEventListener) {
        colorScheme.addEventListener('change', onColorSchemeChanged);
    } else {
        colorScheme.addListener(onColorSchemeChanged);
    }

    window.siteTheme = {
        getTheme: getTheme,
        isDark: function () {
            return getTheme() === 'dark';
        },
        subscribe: function (listener) {
            listeners.add(listener);
            return function () {
                listeners.delete(listener);
            };
        }
    };

    applyTheme();
})();
