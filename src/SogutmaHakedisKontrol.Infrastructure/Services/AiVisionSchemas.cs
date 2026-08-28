namespace SogutmaHakedisKontrol.Infrastructure.Services;

/// <summary>
/// GPT-5.5 çağrılarında kullanılan sabit sistem talimatı ve Structured Outputs (JSON Schema) tanımı.
/// Şema OpenAI "strict" moduyla uyumludur: her obje additionalProperties:false ve tüm alanlar
/// required'dır (opsiyonel alanlar null birleşimiyle ["type","null"] olarak ifade edilir).
/// </summary>
public static class AiVisionSchemas
{
    public const string SystemInstruction =
        """
        Sen Türkçe teknik servis ve soğutma bakım formlarını analiz eden bir belge analiz sistemisin.
        Belgelerde basılı metin ile Türkçe el yazısını birlikte değerlendir.
        El yazısından emin olmadığın hiçbir sayısal miktarı kesin değer olarak üretme. Özellikle malzeme
        miktarı, tarih, mağaza kodu ve personel saatlerinde hata yapmaktansa null döndür ve
        requires_manual_review alanını true yap.

        ── FORM NUMARASI (EN ÖNEMLİ ALAN) ──────────────────────────────────
        form_number, bu formun hakediş Excel'indeki karşılığını bulmakta kullanılan ANA ANAHTARDIR —
        diğer her alandan daha önceliklidir. SERVICE_FORM ve PERIODIC_MAINTENANCE_FORM sayfalarında
        form numarasını bulmak için ÖZELLİKLE şu alanlara bak: sağ üst köşe, başlık bölümü, "Form No",
        "Servis No", "Sıra No", "Belge No" etiketli kutular. ÇOK ÖNEMLİ: form numarası HER ZAMAN bir
        etiketle ("Form No:" gibi) birlikte yazılmaz — birçok formda sağ üst köşede, adres/iletişim
        bilgilerinin hemen altında veya yanında, ETİKETSİZ, genellikle firma logosunun/başlığının rengiyle
        UYUŞMAYAN farklı bir renkte (ör. turuncu, kırmızı — matbu/damgalı sıra numarası) tek başına duran
        bir sayı da form numarasıdır. Sayfanın geri kalanından farklı renkte/fontta, bağımsız duran ve
        başka hiçbir etikete bağlı olmayan bir sayı gördüğünde bunu ATLAMA — bu genellikle form numarasının
        ta kendisidir, "etiketi yok" diye göz ardı etme veya null bırakma. Emin olduğun kadarıyla oku ve
        form_number_confidence alanına 0-1 arası bir güven değeri yaz (net okunuyorsa yüksek, tahminiyse
        düşük). Form numarasını ASLA tahmin etme veya uydurma — gerçekten hiçbir sayı bulamıyorsan
        form_number'ı null bırak, form_number_confidence'ı düşük (0-0.3) yap ve requires_manual_review'i
        true yap.

        ── MAĞAZA KODU VE MAĞAZA ADI ────────────────────────────────────────
        Mağaza kodu veya mağaza adı sayfanın HERHANGİ BİR BÖLÜMÜNDE bulunabilir — sağ üst, sol üst, orta,
        "İŞİN YERİ"/"ÇAĞRI BİLDİREN" alanı, sayfanın kenarında/logonun yanında dönük veya küçük yazılmış,
        etiketsiz bir köşe. Sabit koordinatlara güvenme — tüm sayfayı köşe köşe görsel olarak tara; form
        numarasının hemen yanında farklı, ayrı bir el yazısı/damga sayı varsa bu genellikle mağaza kodudur.
        Mağaza ADI genelde "İŞİN YERİ" gibi etiketli bir alanda serbest metin olarak yazılır (Excel'deki
        resmi/tam adıyla birebir aynı olmak zorunda değildir, kısaltılmış olabilir) — bu alanı ayrıca
        code_raw'dan bağımsız olarak name_raw'a da mutlaka çıkar; ikisi de bulunabiliyorsa ikisini de yaz.
        Mağaza kodu rakamlarını TEK TEK dikkatle oku — el yazısı rakamlarda (özellikle 6/9/8, 0/6, 3/8 gibi
        birbirine benzeyen rakamlarda) karışıklık sık görülür; emin olmadığın bir rakam varsa code_raw'ı
        yine de en iyi tahminle doldur ama confidence alanını düşük tut (asıl güvenlik store adı ve form
        numarası eşleşmesinden gelir, kod OCR hatasına karşı ayrıca toleranslıdır). Ne kod ne ad hiç
        okunamıyorsa store alanını null bırak.
        store alanını null bırakmak SON ÇAREDİR — yalnızca sayfanın TAMAMINI (üst, alt, kenarlar, damga,
        logo çevresi dahil) köşe köşe taradıktan sonra hiçbir mağaza ipucu bulamazsan null yaz. Kısmi bir
        ipucu bile bulduysan (yalnızca kod, yalnızca ad, ya da belirsiz/kısaltılmış bir ad) bunu düşük
        confidence ile mutlaka code_raw/name_raw'a yaz — "emin değilim" gerekçesiyle tüm store objesini
        null'a düşürme; belirsizlik confidence alanıyla ifade edilir, store'u null yapmakla değil.

        ── KULLANILAN MALZEME TABLOSU — BOŞ SATIRLARI ASLA MALZEME SANMA ────
        Malzeme tablosu genelde önceden numaralanmış 1-20 arası sabit satırlardan oluşur (basılı sıra
        numarası her satırda vardır). Bu satırların ÇOĞU BOŞTUR — yalnızca teknisyenin gerçekten el
        yazısıyla malzeme adı YAZDIĞI satırlar gerçek veridir. Bir satırda yalnızca basılı sıra numarası
        (ör. "11", "12", "13"...) var ama malzeme adı hanesi boşsa, o satırı materials dizisine KESİNLİKLE
        EKLEME — sıra numarasını raw_name olarak yazmak ciddi bir hatadır. Yalnızca malzeme adı hanesinde
        gerçek el yazısı içerik gördüğün satırları çıkar; boş/numarasız-içeriksiz satırları tamamen atla.

        ── SAYFA SINIFLANDIRMA ─────────────────────────────────────────────
        Yüklenen PDF tek tip belge değildir; üç farklı sayfa şablonu karışık sırayla bulunabilir:

        1. İCMAL / HAKEDİŞ ÖZET SAYFASI → document_type = SUMMARY
           Çok sayıda satır/kolon içeren, Excel benzeri büyük bir tablo. Servis veya bakım formu DEĞİLDİR.
           Bu tür sayfalarda el yazısı analizi, malzeme veya personel çıkarımı YAPMA — materials ve
           employees dizilerini boş bırak, description_raw/work_performed_raw alanlarını null bırak.

        2. SOĞUTMA MALZEME FORMU / SERVİS FORMU → document_type = SERVICE_FORM
           Başlığı genellikle "SOĞUTMA MALZEME FORMU" veya "SERVİS FORMU"dur. Karakteristik yapı:
           üstte çağrı/iş yeri/çağrı açıklaması, ortada büyük YAPILAN İŞLER/NOTLAR alanı, personel adı +
           tarih + başlangıç/bitiş saati tablosu, altta KULLANILAN MALZEME tablosu, en altta servis
           elemanı/tarih/mağaza onayı. Bu bir arıza/servis ziyaretidir — malzemeleri, miktarları,
           birimlerini, servis tarihini, personelleri ve çalışma saatlerini (başlangıç/bitiş) çıkar.
           Adam-saat matematiğini SEN yapma — yalnızca okuduğun saatleri ham veri olarak ver.

        3. SOĞUTMA AĞIR BAKIM FORMU / PERİYODİK BAKIM FORMU → document_type = PERIODIC_MAINTENANCE_FORM
           Başlığı genellikle "SOĞUTMA AĞIR BAKIM FORMU"dur. Servis formundan tamamen farklıdır:
           sayfanın büyük bölümü bakım kontrol maddeleri, checkbox/tik alanları, kompresör ve soğutma
           sistemi ölçümleri, basınç/sıcaklık/elektriksel değerler ve bir bakım kontrol tablosudur.
           Bunlarca ölçüm/madde okumana gerek yok. Yalnızca öncelikli bilgileri çıkar: mağaza kodu,
           mağaza adı ve sayfanın EN ALTINDA bulunan bakım tarihi.

        ÇOK ÖNEMLİ: Sayfaları PDF içerisindeki SIRASINA göre asla sınıflandırma ("ilk 20 sayfa servis
        formudur" gibi pozisyona bağlı varsayım YAPMA). Belge türleri PDF içinde rastgele karışık sırayla
        ilerleyebilir (ör. Servis, Servis, Bakım, Bakım, Servis, Servis, Bakım). HER SAYFAYI KENDİ GÖRSEL
        İÇERİĞİNE BAKARAK BAĞIMSIZ SINIFLANDIR. Öncelik sırası: (1) görsel şablon/tablo yapısı,
        (2) form başlığı, (3) sayfa içindeki karakteristik alan isimleri. Başlık kötü taranmış veya
        kısmen okunamıyorsa bile form yapısından belge tipini anlamaya çalış.
        Hiçbir sayfa sınıflandırmasız kalamaz: document_type mutlaka SUMMARY, SERVICE_FORM,
        PERIODIC_MAINTENANCE_FORM veya UNKNOWN değerlerinden biri olmalı. Sayfa yapısı bu üç şablondan
        hiçbirine uymuyorsa veya emin değilsen UNKNOWN döndür ve requires_manual_review'i true yap —
        asla zorlama tahmin yapma.

        Belgede bulunmayan hiçbir bilgiyi uydurma. Ham el yazısı metnini (raw) ve senin normalize ettiğin
        hâli (normalized) ayrı tut — ikisini birbirine karıştırma.
        Yanıtını yalnızca verilen JSON şemasına uygun, kısa ve yapılandırılmış üret; uzun açıklama yazma.
        """;

    public const string UserPromptTemplate =
        "Bu görsel, bir soğutma servis/bakım formu sayfasıdır (sayfa {0}/{1}, kaynak: {2}). " +
        "Yukarıdaki sistem talimatına göre analiz et ve yalnızca JSON şemasına uygun sonuç döndür.";

    public const string SchemaName = "page_analysis_result";

    public const string JsonSchema =
        """
        {
          "type": "object",
          "properties": {
            "document_type": { "type": "string", "enum": ["SUMMARY", "SERVICE_FORM", "PERIODIC_MAINTENANCE_FORM", "UNKNOWN"] },
            "form_number": { "type": ["string", "null"], "description": "Hakediş Excel'i ile eşleştirmede kullanılan ana anahtar" },
            "form_number_confidence": { "type": "number", "description": "0-1, form_number okumasına ne kadar güvenildiği" },
            "store": {
              "type": ["object", "null"],
              "properties": {
                "code_raw": { "type": ["string", "null"] },
                "name_raw": { "type": ["string", "null"] },
                "confidence": { "type": "number" }
              },
              "required": ["code_raw", "name_raw", "confidence"],
              "additionalProperties": false
            },
            "service_date": { "type": ["string", "null"], "description": "ISO 8601 yyyy-MM-dd, yalnızca SERVICE_FORM" },
            "maintenance_date": { "type": ["string", "null"], "description": "ISO 8601 yyyy-MM-dd, formun en altındaki tarih, yalnızca PERIODIC_MAINTENANCE_FORM" },
            "description_raw": { "type": ["string", "null"] },
            "work_performed_raw": { "type": ["string", "null"] },
            "form_total_hours": { "type": ["number", "null"], "description": "Formda yazan toplam saat, varsa" },
            "employees": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "name_raw": { "type": ["string", "null"] },
                  "start_time": { "type": ["string", "null"], "description": "HH:mm" },
                  "end_time": { "type": ["string", "null"], "description": "HH:mm" },
                  "confidence": { "type": "number" }
                },
                "required": ["name_raw", "start_time", "end_time", "confidence"],
                "additionalProperties": false
              }
            },
            "materials": {
              "type": "array",
              "items": {
                "type": "object",
                "properties": {
                  "raw_name": { "type": "string" },
                  "normalized_name": { "type": ["string", "null"] },
                  "quantity": { "type": ["number", "null"] },
                  "unit": { "type": ["string", "null"] },
                  "confidence": { "type": "number" },
                  "requires_manual_review": { "type": "boolean" }
                },
                "required": ["raw_name", "normalized_name", "quantity", "unit", "confidence", "requires_manual_review"],
                "additionalProperties": false
              }
            },
            "warnings": { "type": "array", "items": { "type": "string" } },
            "requires_manual_review": { "type": "boolean" }
          },
          "required": ["document_type", "form_number", "form_number_confidence", "store", "service_date", "maintenance_date", "description_raw", "work_performed_raw", "form_total_hours", "employees", "materials", "warnings", "requires_manual_review"],
          "additionalProperties": false
        }
        """;
}
