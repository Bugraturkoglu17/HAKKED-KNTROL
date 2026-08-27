using SogutmaHakedisKontrol.Application.Interfaces;
using SogutmaHakedisKontrol.Domain.Enums;

namespace SogutmaHakedisKontrol.Infrastructure.Services;

/// <summary>Ortak taban — DisplayName, kategori enum'unun kendi Türkçe adını kullanır.</summary>
public abstract class CategoryControlProfileBase : ICategoryControlProfile
{
    public abstract HakedisCategory Category { get; }
    public string DisplayName => Category.DisplayName();
    public abstract string AiInstructionSupplement { get; }
}

public sealed class CompressorReplacementProfile : CategoryControlProfileBase
{
    public override HakedisCategory Category => HakedisCategory.CompressorReplacement;
    public override string AiInstructionSupplement =>
        "Bu bir KOMPRESÖR DEĞİŞİM hakedişi kontrolüdür. Servis formunda öncelikle şunları ara: " +
        "kompresör değişimi gerçekten yapılmış mı, kompresör adedi, kompresör modeli, kapasite/model bilgisi, " +
        "değişim açıklaması, sökülen/eski kompresör bilgisi (varsa), yeni takılan kompresör bilgisi (varsa), " +
        "işçilik/adam-saat (varsa). Formda açıkça yazmayan bir adet/model bilgisini ASLA tahmin etme — " +
        "belirsizse requires_manual_review=true işaretle ve warnings alanına net şekilde yaz.";
}

public sealed class GlycolUsageProfile : CategoryControlProfileBase
{
    public override HakedisCategory Category => HakedisCategory.GlycolUsage;
    public override string AiInstructionSupplement =>
        "Bu bir GLİKOL KULLANIM hakedişi kontrolüdür. Servis formunun ortasındaki \"KULLANILAN MALZEME/MALZEMELER\" " +
        "tablosunda ÖNCELİKLE \"GLİKOL\" (veya açıkça glikolü ifade eden \"antifriz\" gibi kayıtları) ara. " +
        "Glikol miktarını malzeme listesine \"glikol\" adıyla ve kg biriminde ekle ki otomatik karşılaştırma " +
        "yapılabilsin — formda kg değil litre yazıyorsa birimi olduğu gibi (litre) belirt, kendiliğinden kg'a " +
        "çevirme. Diğer ilgisiz malzemeleri (gaz, filtre, dryer, vana, boru, flex, sensör, yağ vb.) de listele " +
        "ama glikol miktarı alanını asla boş bırakma — formda yoksa materials listesine ekleme, tahmin etme.";
}

public sealed class EvapReplacementProfile : CategoryControlProfileBase
{
    public override HakedisCategory Category => HakedisCategory.EvapReplacement;
    public override string AiInstructionSupplement =>
        "Bu bir EVAP TEMİN VE DEĞİŞİM hakedişi kontrolüdür. Servis formunda öncelikle şunları ara: " +
        "evap temini mi değişimi mi, adet, model, kapasite, hangi bölüm/dolap/soğuk oda için yapıldığı, " +
        "değişimin servis formunda gerçekten belirtilip belirtilmediği.";
}

public sealed class PartialRenovationProfile : CategoryControlProfileBase
{
    public override HakedisCategory Category => HakedisCategory.PartialRenovation;
    public override string AiInstructionSupplement =>
        "Bu bir KISMİ TADİLAT hakedişi kontrolüdür. Servis formunda öncelikle şunları ara: " +
        "yapılan iş, kullanılan malzemeler ve adetleri, işçilik (ekip sayısı, çalışma süresi), " +
        "adam × saat hesabına esas olacak başlangıç/bitiş saatleri. Çalışma saatlerini olabildiğince " +
        "kişi bazında (her personelin kendi başlangıç/bitiş saati) çıkar.";
}

public sealed class GasUsageProfile : CategoryControlProfileBase
{
    public override HakedisCategory Category => HakedisCategory.GasUsage;
    public override string AiInstructionSupplement =>
        "Bu bir GAZ KULLANIM hakedişi kontrolüdür. Servis formunda ÖNCELİKLE şunları ara: " +
        "kullanılan gaz miktarı (kg, sayısal ve net), kaçak yeri, kaçak tespiti, kaçak onarımı, " +
        "servis açıklaması. Gaz miktarını malzeme listesine \"gaz\" adıyla ve kg biriminde ekle ki " +
        "otomatik karşılaştırma yapılabilsin. Diğer ilgisiz malzemeleri (yedek parça vb.) da listele " +
        "ama gaz miktarı alanını asla boş bırakma — formda yoksa materials listesine ekleme, tahmin etme.";
}

public sealed class MonitoringProfile : CategoryControlProfileBase
{
    public override HakedisCategory Category => HakedisCategory.Monitoring;
    public override string AiInstructionSupplement =>
        "Bu bir İZLEME BEDELLERİ hakedişi kontrolüdür. Servis formunda öncelikle şunları ara: " +
        "izleme hizmeti, dönem/adet bilgisi, hizmeti doğrulayan kayıt. Bu kategori için detaylı iş kuralları " +
        "henüz tanımlanmadı — formda gördüğün bilgileri olduğu gibi, yorum katmadan çıkar.";
}

public sealed class PeriodicMaintenanceProfile : CategoryControlProfileBase
{
    public override HakedisCategory Category => HakedisCategory.PeriodicMaintenance;
    public override string AiInstructionSupplement =>
        "Bu bir PERİYODİK BAKIM hakedişi kontrolüdür. Belgenin başlığından bunun \"Periyodik Bakım\" veya " +
        "\"Soğutma Ağır Bakım\" formu olup olmadığını anla. Öncelikle şunları ara: bakım tarihi, " +
        "bakımın gerçekten yapılıp yapılmadığı, mağaza bilgisi, dönem bilgisi.";
}

public sealed class AdditionalWorkProfile : CategoryControlProfileBase
{
    public override HakedisCategory Category => HakedisCategory.AdditionalWork;
    public override string AiInstructionSupplement =>
        "Bu bir İLAVE İŞLER hakedişi kontrolüdür. Servis formunda öncelikle şunları ara: " +
        "yapılan ilave iş, malzeme, miktar, birim, adam × saat, çalışma açıklaması. " +
        "Ayrıca formda servis ziyaretinin ŞEHİRİÇİ mi ŞEHİRDIŞI mı olduğuna dair bir işaret varsa " +
        "(mesafe, açıklama, form başlığı) onu work_performed_raw veya description_raw içinde belirt.";
}

/// <summary>Kategori seçilmemiş (eski kayıt) veya bilinmeyen durumlarda kullanılan boş/nötr profil — AI'ye ek yönerge eklemez.</summary>
public sealed class GenericControlProfile : ICategoryControlProfile
{
    public HakedisCategory Category => default;
    public string DisplayName => "Genel";
    public string AiInstructionSupplement => string.Empty;
}

/// <summary>Kategoriye göre profili seçer (dağınık if/else yerine tek dispatch noktası).</summary>
public class CategoryControlProfileRegistry : ICategoryControlProfileRegistry
{
    private readonly Dictionary<HakedisCategory, ICategoryControlProfile> _byCategory;
    private readonly ICategoryControlProfile _generic = new GenericControlProfile();

    public CategoryControlProfileRegistry(IEnumerable<ICategoryControlProfile> profiles)
        => _byCategory = profiles.ToDictionary(p => p.Category);

    public ICategoryControlProfile Get(HakedisCategory? category) =>
        category.HasValue && _byCategory.TryGetValue(category.Value, out var profile) ? profile : _generic;
}
