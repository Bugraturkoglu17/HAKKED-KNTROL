"""
Servis Hakediş ve İcmal Otomasyon — Başlatıcı
============================================
Bu dosyayı çift tıklayarak veya Python ile çalıştırarak uygulamayı başlatabilirsiniz.
Uygulama kendi penceresinde açılır, tarayıcı kullanılmaz.
"""

import subprocess
import sys
import os
import winreg
import tkinter as tk
from tkinter import messagebox
from pathlib import Path


# --- Ayarlar ---
APP_NAME = "Servis Hakediş ve İcmal Otomasyon"

# Bu Python dosyasının bulunduğu klasörün yanındaki 'publish' klasörü
SCRIPT_DIR = Path(__file__).parent
EXE_PATHS = [
    SCRIPT_DIR / "ServisHakedis" / "ServisHakedis.exe",
    SCRIPT_DIR / "publish" / "ServisHakedis" / "ServisHakedis.exe",
    SCRIPT_DIR / "publish" / "HakedisOtomasyon.Web.exe",
    SCRIPT_DIR / "HakedisOtomasyon.Web.exe",
    Path("C:/HakedisOtomasyon/publish/HakedisOtomasyon.Web.exe"),
    Path("C:/HakedisOtomasyon/src/HakedisOtomasyon.Web/bin/Release/net8.0-windows/win-x64/publish/HakedisOtomasyon.Web.exe"),
]

def check_webview2_installed() -> bool:
    """WebView2 Runtime kurulu mu kontrol et (Windows Registry)."""
    keys = [
        r"SOFTWARE\WOW6432Node\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}",
        r"SOFTWARE\Microsoft\EdgeUpdate\Clients\{F3017226-FE2A-4295-8BDF-00C3A9A7E4C5}",
    ]
    for key_path in keys:
        try:
            with winreg.OpenKey(winreg.HKEY_LOCAL_MACHINE, key_path):
                return True
        except OSError:
            pass
    return False


def find_exe() -> Path | None:
    for path in EXE_PATHS:
        if path.exists():
            return path
    return None


def show_error(title: str, message: str):
    root = tk.Tk()
    root.withdraw()
    messagebox.showerror(title, message)
    root.destroy()


def main():
    exe = find_exe()

    if exe is None:
        show_error(
            "Uygulama Bulunamadı",
            f"'{APP_NAME}' çalıştırılabilir dosyası bulunamadı.\n\n"
            "Beklenen konum:\n"
            f"  {SCRIPT_DIR / 'publish' / 'HakedisOtomasyon.Web.exe'}\n\n"
            "Lütfen önce uygulamayı derleyin:\n"
            "  publish.bat  (veya README'e bakın)"
        )
        sys.exit(1)

    if not check_webview2_installed():
        root = tk.Tk()
        root.withdraw()
        answer = messagebox.askyesno(
            "WebView2 Runtime Gerekli",
            "Uygulama için Microsoft Edge WebView2 Runtime gereklidir.\n\n"
            "Bu bileşen modern Windows'larda zaten yüklüdür.\n"
            "Yüklü değilse indirmek ister misiniz?\n\n"
            "(Hayır seçerseniz uygulama başlatmayı dener)",
        )
        root.destroy()
        if answer:
            import webbrowser
            webbrowser.open("https://go.microsoft.com/fwlink/p/?LinkId=2124703")
            sys.exit(0)

    # Uygulamayı başlat (ayrı process, Python kapansa bile çalışmaya devam eder)
    try:
        subprocess.Popen(
            [str(exe)],
            cwd=str(exe.parent),
            creationflags=subprocess.CREATE_NO_WINDOW if sys.platform == "win32" else 0,
        )
    except Exception as e:
        show_error("Başlatma Hatası", f"Uygulama başlatılamadı:\n\n{e}")
        sys.exit(1)


if __name__ == "__main__":
    main()
