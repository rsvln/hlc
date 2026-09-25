// Связка страницы Config (Blazor) с YAML-редактором CodeMirror (yaml-editor.js).
// Оба файла подключаются с ?v=<версия HLC>: после обновления браузер не возьмёт старую копию из кэша.

let editor = null;

function isDark() {
    return document.documentElement.getAttribute('data-bs-theme') === 'dark';
}

// Тема страницы переключилась (theme.js) — перекрашиваем редактор
document.addEventListener('hlc-theme', () => editor?.setDark(isDark()));

// true — редактор создан; false — не загрузился, страница покажет обычный textarea
export async function init(host, text, version) {
    try {
        const { createYamlEditor } = await import(`./yaml-editor.js?v=${encodeURIComponent(version)}`);
        host.replaceChildren();
        editor = createYamlEditor(host, text, null, { dark: isDark() });
        return true;
    } catch (e) {
        console.warn('YAML editor is not available, using the plain text area', e);
        editor = null;
        return false;
    }
}

export function getValue() {
    return editor ? editor.getValue() : null;
}

export function setValue(text) {
    editor?.setValue(text);
}

export function dispose() {
    editor = null;
}
