using System.Text.Json;
using HakedisOtomasyon.Application;
using HakedisOtomasyon.Application.DTOs;
using Xunit;

namespace SogutmaHakedisKontrol.Tests;

/// <summary>
/// El yazısından (Gemini) okunan mağaza bilgisinin mağaza listesiyle eşleşmesi. Gerçek Soğutma mağaza listesi (4141 mağaza,
/// kurulum verisi) ve gerçek servis formlarından Gemini Flash Lite'ın okuduğu değerlerle sınanır.
/// </summary>
public class StoreMatcherTests
{
    private static readonly Lazy<List<StoreDto>> Stores = new(() =>
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "src", "HakedisOtomasyon.Web", "SeedData", "cooling_stores.json"))) dir = dir.Parent;
        var json = File.ReadAllText(Path.Combine(dir!.FullName, "src", "HakedisOtomasyon.Web", "SeedData", "cooling_stores.json"));
        return JsonSerializer.Deserialize<List<SeedStore>>(json, new JsonSerializerOptions { PropertyNameCaseInsensitive = true })!
            .Select((s, i) => new StoreDto { Id = i + 1, Code = s.Code, Name = s.Name, Format = s.Format, Region = s.Region, City = s.City })
            .ToList();
    });

    private sealed class SeedStore
    {
        public string Code { get; set; } = "";
        public string Name { get; set; } = "";
        public string? Format { get; set; }
        public string? Region { get; set; }
        public string? City { get; set; }
    }

    // ---------------------------------------------------------------- kod
    [Theory]
    [InlineData("383", "GOP MM MİGROS", "383")]
    [InlineData("(5159)", "Novada Migros Mm", "5159")]
    [InlineData("6671", null, "6671")]
    [InlineData("6955", "Eryaman Evleri MM Migros", "6955")]
    public void KodOkunduysa_LisetekiMagazaSecilir(string code, string? name, string expected)
    {
        var r = StoreMatcher.Match(code, name, Stores.Value);
        Assert.Equal(expected, r.Selected?.Code);
        Assert.Equal("kod", r.Method);
    }

    [Fact]
    public void ListedeOlmayanKod_AdaGoreDenenir()
    {
        var r = StoreMatcher.Match("99999", "Demircioğlu Migros Derecikler", Stores.Value);
        Assert.Equal("6671", r.Selected?.Code);
        Assert.Equal("ad", r.Method);
    }

    /// <summary>Mağaza adındaki kısa kelime ("DERE"), formdaki uzun kelimenin ("DERECİKLER") kısaltması SAYILMAZ —
    /// aksi halde "Demircioğlu ... Derecikler" yanlışlıkla "DERE MAHALLESİ YALOVA" ile eşleşiyordu.</summary>
    [Fact]
    public void MagazaninKisaKelimesi_FormdakiUzunKelimeyleEslesmez()
    {
        var r = StoreMatcher.Match(null, "Demircioğlu Migros Derecikler", Stores.Value);
        Assert.NotEqual("9825", r.Selected?.Code);
        Assert.Equal("6671", r.Selected?.Code);
    }

    /// <summary>Bölge verilirse ad önce o bölgede aranır: "GOP" İstanbul ve Tokat'taki mağazalarda da geçer; İÇ ANADOLU'da
    /// G.O.PAŞA ANKARA (383) öne çıkar. Kod eşleşmesi bölgeden bağımsızdır.</summary>
    [Fact]
    public void BolgeVerilirse_AdOnceOBolgedeAranir()
    {
        // bölgesiz: GOP birden çok mağazada geçer → hiçbiri seçilmez
        Assert.Null(StoreMatcher.Match(null, "GOP MM MİGROS", Stores.Value).Selected);
        var regional = StoreMatcher.Match(null, "GOP MM MİGROS", Stores.Value, "İÇ ANADOLU");
        Assert.True(regional.Selected?.Code == "383" || regional.Suggestions.Any(s => s.Code == "383"));
        Assert.All(regional.Suggestions, s => Assert.Equal("İÇ ANADOLU", s.Region));
        // kod her zaman bölgeden bağımsız
        Assert.Equal("6926", StoreMatcher.Match("6926", null, Stores.Value, "İÇ ANADOLU").Selected?.Code);
    }

    /// <summary>"Parksite" (bitişik) ↔ "PARK SİTE"; formdaki "2M" = MM. Gerçek formda "Aksaray" yanlış okunup "Aksu" olmuştu:
    /// bölgede iki PARK SİTE MM var (EREĞLİ 6894, AKSARAY 7699) → hiçbiri seçilmez, ikisi de önerilir.</summary>
    [Fact]
    public void BitisikYazilmisKelime_VeIkiM_MMSayilir()
    {
        var r = StoreMatcher.Match(null, "Aksu Parksite 2M Migros", Stores.Value, "İÇ ANADOLU");
        Assert.Null(r.Selected);
        Assert.Equal(new[] { "6894", "7699" }, r.Suggestions.Take(2).Select(s => s.Code).OrderBy(c => c).ToArray());
    }

    // ---------------------------------------------------------------- ad (gerçek formlardan Gemini'nin okuduğu adlar)
    /// <summary>Ad ve formda yazan biçim/Migros ibaresi tek mağazayı açıkça gösteriyorsa seçilir.</summary>
    [Theory]
    [InlineData("5 M AFYON", "2890")]                           // 5M AFYON — Afyon'da 22 mağaza var, "5M" biçimi ayırt eder
    [InlineData("BİNEVLER M MİGROS ÇORUM", "4321")]             // BİNEVLER ÇORUM M MİGROS (Gaziantep'te de BİNEVLER var)
    [InlineData("Demircioğlu Migros Derecikler", "6671")]       // K. DEMİRCİOĞLU CAD.KASTAMONU M (adda yalnız bir mağaza)
    public void AdVeBicimTekMagazayiGosteriyorsa_Secilir(string name, string expected)
    {
        var r = StoreMatcher.Match(null, name, Stores.Value);
        Assert.Equal(expected, r.Selected?.Code);
    }

    /// <summary>Aynı kelime birden çok mağazada geçiyorsa seçilmez; doğru mağaza İLK öneri olur.</summary>
    [Theory]
    [InlineData("Kastamall AVM Migros", "6926")]               // KASTAMALL AVM MM MİGROS (ayrıca MION KASTAMALL var)
    [InlineData("Cide MM Migros", "5923")]                      // CİDE KASTAMONU M MİGROS (ayrıca MJET ... CİDE var)
    public void BelirsizseSecilmez_DogruMagazaIlkOneri(string name, string expected)
    {
        var r = StoreMatcher.Match(null, name, Stores.Value);
        if (r.Selected is not null) Assert.Equal(expected, r.Selected.Code);   // seçildiyse yalnız doğrusu olabilir
        else Assert.Equal(expected, r.Suggestions.FirstOrDefault()?.Code);
    }

    /// <summary>Emin olunamayan ya da formla çelişen adlarda mağaza SEÇİLMEZ (yanlış mağaza sessizce seçilmesin).</summary>
    [Theory]
    [InlineData("Akay 2M Migros")]                 // aslında AKSARAY MM MİGROS — yanlış okunmuş
    [InlineData("MM MİGROS BAHABEY")]              // aslında ÇORUM MM MİGROS; listedeki "MJET BAHABEY ÇORUM" MM değil → seçilmemeli
    [InlineData("ESAS 67 MM MİGROS, ZONGULDAK")]   // ZONGULDAK'ta birden çok mağaza var
    public void EminOlunamayanAd_SecilmezEnFazla3Oneri(string name)
    {
        var r = StoreMatcher.Match(null, name, Stores.Value);
        Assert.Null(r.Selected);
        Assert.True(r.Suggestions.Count <= 3);
    }

    [Fact]
    public void ZonguldakIcinOneriler_ZonguldakMagazalariOlur()
    {
        var r = StoreMatcher.Match(null, "ESAS 67 MM MİGROS, ZONGULDAK", Stores.Value);
        Assert.NotEmpty(r.Suggestions);
        Assert.All(r.Suggestions, s => Assert.Contains("ZONGULDAK", (s.Name + " " + s.City).ToUpperInvariant()));
    }

    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData(null, "MİGROS MM")]               // yalnız genel kelimeler
    public void BilgiYoksa_SecimVeOneriYok(string? code, string? name)
    {
        var r = StoreMatcher.Match(code, name, Stores.Value);
        Assert.Null(r.Selected);
        Assert.Empty(r.Suggestions);
    }

    /// <summary>El yazısı tarih: gün/ay formdan, yıl verilen (içinde bulunulan) yıl. "20.4.26" → 20 Nisan.</summary>
    [Theory]
    [InlineData(20, 4, 2026, "2026-04-20")]
    [InlineData(29, 2, 2028, "2028-02-29")]     // artık yıl
    [InlineData(29, 2, 2026, null)]             // 2026'da 29 Şubat yok
    [InlineData(31, 4, 2026, null)]             // Nisan 30 çeker
    [InlineData(17, 13, 2026, null)]
    [InlineData(0, 4, 2026, null)]
    [InlineData(null, 4, 2026, null)]
    [InlineData(20, null, 2026, null)]
    public void ElYazisiTarih_YilSabit_GecersizGunAyAlinmaz(int? day, int? month, int year, string? expected)
        => Assert.Equal(expected is null ? null : DateTime.Parse(expected), HandwrittenDate.Compose(day, month, year));

    [Fact]
    public void Kelimeler_TurkceKarakterVeNoktaliKisaltmaSadelesir()
    {
        Assert.Equal(new[] { "GOPASA", "ANKARA", "MM", "MIGROS" }, StoreMatcher.Tokens("G.O.PAŞA ANKARA MM MİGROS").ToArray());
        Assert.Equal(new[] { "K", "DEMIRCIOGLU", "CAD", "KASTAMONU", "M" }, StoreMatcher.Tokens("K. DEMİRCİOĞLU CAD.KASTAMONU M").ToArray());
    }
}
