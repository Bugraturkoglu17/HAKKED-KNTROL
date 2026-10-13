# Kaynak veriler

Uygulamanın ana verisinin (birim fiyatlar, mağazalar) kaynak dosyaları. Kayıtlar kaybolursa buradan yeniden kurulabilir.

| Dosya | İçerik |
| --- | --- |
| `2026-Sogutma-Malzeme-Birim-Fiyatlar-Yayim.xls` | Soğutma malzeme birim fiyat listesi (malzeme kodu, malzeme, tip, 2026 baz fiyat). `src/HakedisOtomasyon.Web/SeedData/cooling_price_items.json` bu dosyadan üretilir. |
| `Magaza-Listesi.xlsx` | Mağaza listesi (işyeri no, ad, bölge, marka, format, il, ilçe, durum). `SeedData/cooling_stores.json` bu dosyadan üretilir. |
| `../hakedis.db` | Tüm disiplinlerin mağaza ve birim fiyat kayıtlarını içeren veritabanı (hakediş, servis formu ve fatura kaydı içermez). |

Mağaza listesinde yalnızca uygulamanın kullandığı sütunlar vardır; telefon, e-posta, adres, koordinat ve yönetici adı gibi
iletişim/kişisel sütunlar bu depoya konmaz.

`hakedis.db` canlı veritabanından üretilir: mağaza ve fiyat tabloları aynen alınır; hakediş, servis formu, kalem, fatura,
dışa aktarım ve kur önbelleği tabloları boşaltılır.
