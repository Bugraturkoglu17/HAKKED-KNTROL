using SogutmaHakedisKontrol.Domain.Enums;

namespace SogutmaHakedisKontrol.Application.Interfaces;

/// <summary>
/// Bir hakediş kategorisinin AI'ye ne aratacağını tanımlar. Kategori seçimine göre farklı
/// davranış, kod içinde dağınık if/else yerine bu profillerin registry üzerinden seçilmesiyle sağlanır.
/// </summary>
public interface ICategoryControlProfile
{
    HakedisCategory Category { get; }
    string DisplayName { get; }

    /// <summary>Servis formu görsel analizinde genel sistem talimatına eklenecek, kategoriye özel
    /// Türkçe yönerge — AI'nin bu kategori için öncelikli olarak hangi alanları aradığını belirtir.</summary>
    string AiInstructionSupplement { get; }
}

public interface ICategoryControlProfileRegistry
{
    /// <summary>Kategoriye ait profili döner; null verilirse (eski kayıt/kategori seçilmemiş) genel amaçlı boş bir profil döner.</summary>
    ICategoryControlProfile Get(HakedisCategory? category);
}
