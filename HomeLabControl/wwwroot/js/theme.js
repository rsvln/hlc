// Тема интерфейса: auto (как в системе) / light / dark. Подключается синхронно в <head>,
// чтобы data-bs-theme был выставлен до отрисовки — без вспышки светлой темы.
(function () {
    const KEY = 'hlc-theme';
    const media = window.matchMedia('(prefers-color-scheme: dark)');

    function mode() {
        try { return localStorage.getItem(KEY) || 'auto'; } catch { return 'auto'; }
    }

    function resolve(m) {
        return m === 'auto' ? (media.matches ? 'dark' : 'light') : m;
    }

    function apply() {
        const theme = resolve(mode());
        document.documentElement.setAttribute('data-bs-theme', theme);
        document.dispatchEvent(new CustomEvent('hlc-theme', { detail: theme }));
    }

    window.hlcTheme = {
        get: mode,
        resolved: () => resolve(mode()),
        set: function (m) {
            try { localStorage.setItem(KEY, m); } catch { }
            apply();
            return m;
        }
    };

    media.addEventListener('change', () => { if (mode() === 'auto') apply(); });
    apply();
})();
