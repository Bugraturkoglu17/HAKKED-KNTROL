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

// ─── Çok sayfalı PDF görüntüleyici (PdfPagesViewer) ─────────────────────────────
// Sayfalar ekrana yaklaştıkça .NET'ten görsel adresi istenir (lazy). Kullanıcı kaydırdıkça EN ÇOK görünen sayfa
// .NET'e bildirilir (alttaki "Sayfa x / y" ve Otomatik doldur bu sayfayı kullanır). Programla yapılan kaydırma
// (oklar, satıra tıklama) bildirilmez — kullanıcı tekerlek/klavye/fare ile kaydırana kadar.
window.pdfPages = {
    init: function (container, dotNetRef) {
        if (!container) return;
        const state = { ref: dotNetRef, programmatic: false, last: 0, timer: null };
        container._pdfPages = state;

        // Sayfa görseli istenir; gelmezse/hata olursa sayfa boş KALMAZ: birkaç saniye sonra yeniden istenir.
        const load = (pg) => {
            if (pg.dataset.loading || pg.dataset.loaded) return;
            pg.dataset.loading = '1';
            const tries = (parseInt(pg.dataset.tries || '0', 10)) + 1;
            pg.dataset.tries = tries;
            let settled = false;
            const retry = () => {
                if (settled) return;
                settled = true;
                delete pg.dataset.loading;
                if (tries < 6) setTimeout(() => { if (state.ref && pg.isConnected) load(pg); }, 1500 * tries);
                else pg.classList.add('pdfpg-error');
            };
            const watchdog = setTimeout(retry, 20000);   // yanıt hiç gelmezse
            dotNetRef.invokeMethodAsync('GetPageUrl', parseInt(pg.dataset.page, 10))
                .then(url => {
                    clearTimeout(watchdog);
                    const img = pg.querySelector('img');
                    if (!url || !img) { retry(); return; }
                    img.onload = () => { settled = true; pg.dataset.loaded = '1'; delete pg.dataset.loading; pg.classList.remove('pdfpg-error'); state.observer.unobserve(pg); };
                    img.onerror = () => { img.removeAttribute('src'); retry(); };
                    img.src = url + (tries > 1 ? '?r=' + tries : '');
                })
                .catch(() => { clearTimeout(watchdog); retry(); });
        };
        state.load = load;
        state.observer = new IntersectionObserver((entries) => {
            for (const e of entries) if (e.isIntersecting) load(e.target);
        }, { root: container, rootMargin: '1200px 0px' });
        container.querySelectorAll('.pdfpg').forEach(pg => state.observer.observe(pg));

        const userScroll = () => { state.programmatic = false; };
        ['wheel', 'keydown', 'mousedown', 'touchstart'].forEach(ev => container.addEventListener(ev, userScroll, { passive: true }));

        container.addEventListener('scroll', () => {
            if (state.programmatic) return;
            clearTimeout(state.timer);
            state.timer = setTimeout(() => {
                const page = window.pdfPages.visiblePage(container);
                if (page && page !== state.last) {
                    state.last = page;
                    dotNetRef.invokeMethodAsync('OnVisiblePage', page);
                }
            }, 120);
        }, { passive: true });
    },

    // En çok görünen sayfa (1 tabanlı); en alta inildiyse son sayfa
    visiblePage: function (container) {
        const pages = container.querySelectorAll('.pdfpg');
        if (!pages.length) return 0;
        if (container.scrollTop + container.clientHeight >= container.scrollHeight - 2) return pages.length;
        const top = container.scrollTop, bottom = top + container.clientHeight;
        let best = 0, bestVisible = -1;
        pages.forEach((pg, i) => {
            const pTop = pg.offsetTop, pBottom = pTop + pg.offsetHeight;
            const visible = Math.min(bottom, pBottom) - Math.max(top, pTop);
            if (visible > bestVisible) { bestVisible = visible; best = i + 1; }
        });
        return best;
    },

    scrollToPage: function (container, page) {
        if (!container) return;
        const pg = container.querySelector('.pdfpg[data-page="' + (page - 1) + '"]');
        if (!pg) return;
        const state = container._pdfPages;
        if (state) { state.programmatic = true; state.last = page; }
        container.scrollTop = pg.offsetTop - 6;
    },

    // yakınlaştırma sonrası: açık sayfa yerinde kalsın
    keepPage: function (container, page) { window.pdfPages.scrollToPage(container, page); },

    dispose: function (container) {
        const state = container && container._pdfPages;
        if (state) { state.observer.disconnect(); clearTimeout(state.timer); state.ref = null; container._pdfPages = null; }
    }
};

// ─── Tarih alanı: noktalar kendiliğinden ───────────────────────────────────────
// Yer tutucusu "gg.aa.yyyy" olan tarih kutularında kullanıcı yalnız rakam yazar: 20042026 → 20.04.2026.
// Kendisi nokta yazarsa tek haneli gün/ay sıfırla tamamlanır (2.4. → 02.04.). Yıl 2 haneli yazılırsa (20.04.26)
// kutudan çıkınca 2026 olur. Silme tuşlarına karışılmaz.
(function () {
    const isDateBox = el => el && el.tagName === 'INPUT' && el.placeholder === 'gg.aa.yyyy';
    const format = (raw) => {
        const parts = raw.split('.');
        let digits = '';
        parts.forEach((p, i) => {
            const d = p.replace(/\D/g, '');
            // kullanıcının noktayla bitirdiği tek haneli gün/ay → başına 0
            digits += (i < parts.length - 1 && i < 2 && d.length === 1) ? '0' + d : d;
        });
        digits = digits.slice(0, 8);
        let out = digits.slice(0, 2);
        if (digits.length >= 2) out += '.';
        out += digits.slice(2, 4);
        if (digits.length >= 4) out += '.';
        out += digits.slice(4, 8);
        return out;
    };
    document.addEventListener('input', (e) => {
        const el = e.target;
        if (!isDateBox(el) || (e.inputType || '').startsWith('delete')) return;
        const v = format(el.value);
        if (v !== el.value) { el.value = v; el.setSelectionRange(v.length, v.length); }
    }, true);
    document.addEventListener('change', (e) => {
        const el = e.target;
        if (!isDateBox(el)) return;
        const m = /^(\d{2})\.(\d{2})\.(\d{2})$/.exec(el.value);
        if (m) el.value = `${m[1]}.${m[2]}.20${m[3]}`;
    }, true);
})();
