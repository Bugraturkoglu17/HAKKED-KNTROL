// Hakediş Otomasyonu — Yardımcı JavaScript

// InputFile elementini programatik olarak tetikle
window.triggerClick = function (elementId) {
    const el = document.getElementById(elementId);
    if (el) el.click();
};

// Dosya indirme (byte array'den)
window.downloadFileFromBase64 = function (base64, fileName) {
    const link = document.createElement('a');
    link.href = 'data:application/octet-stream;base64,' + base64;
    link.download = fileName;
    document.body.appendChild(link);
    link.click();
    document.body.removeChild(link);
};

// Dosya yolu'ndan aç
window.openFileUrl = function (url) {
    window.open(url, '_blank');
};

// Sürükle-bırak dosya yükleme yardımcısı
window.initDropZone = function (elementId, dotNetRef) {
    const el = document.getElementById(elementId);
    if (!el) return;

    el.addEventListener('dragover', (e) => {
        e.preventDefault();
        el.classList.add('drag-over');
    });

    el.addEventListener('dragleave', () => {
        el.classList.remove('drag-over');
    });

    el.addEventListener('drop', async (e) => {
        e.preventDefault();
        el.classList.remove('drag-over');
        const files = e.dataTransfer.files;
        if (files.length > 0) {
            await dotNetRef.invokeMethodAsync('OnFilesDropped', files.length);
        }
    });
};

// Klavye kısayolları
window.registerKeyboardShortcuts = function (dotNetRef) {
    document.addEventListener('keydown', async (e) => {
        if (e.ctrlKey && e.key === 's') {
            e.preventDefault();
            await dotNetRef.invokeMethodAsync('SaveCurrentForm');
        }
        if (e.ctrlKey && e.key === 'Enter') {
            e.preventDefault();
            await dotNetRef.invokeMethodAsync('SaveAndNext');
        }
    });
};

// Sayfa kaydırma
window.scrollToTop = function () {
    window.scrollTo({ top: 0, behavior: 'smooth' });
};

window.scrollToElement = function (elementId) {
    const el = document.getElementById(elementId);
    if (el) el.scrollIntoView({ behavior: 'smooth', block: 'start' });
};

// Pano kopyalama
window.copyToClipboard = function (text) {
    navigator.clipboard.writeText(text).catch(() => {
        const el = document.createElement('textarea');
        el.value = text;
        document.body.appendChild(el);
        el.select();
        document.execCommand('copy');
        document.body.removeChild(el);
    });
};
