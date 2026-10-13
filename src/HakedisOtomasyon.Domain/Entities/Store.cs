using HakedisOtomasyon.Domain.Enums;

namespace HakedisOtomasyon.Domain.Entities;

public class Store
{
    public int Id { get; set; }
    public string Code { get; set; } = string.Empty;
    public string Name { get; set; } = string.Empty;
    public bool IsActive { get; set; } = true;
    public DateTime CreatedAt { get; set; } = DateTime.Now;
    public MechanicalDiscipline Discipline { get; set; } = MechanicalDiscipline.Fire;

    /// <summary>Mağaza formatı (MMM, MM, M-JET, M ANADOLU ...) — Soğutma Glikol hakediş çıktısında gösterilir.</summary>
    public string? Format { get; set; }

    /// <summary>Mağazanın bölgesi (İÇ ANADOLU, BATI MARMARA ...) — hakediş çıktısı başlığında kullanılır.</summary>
    public string? Region { get; set; }

    /// <summary>Mağazanın ili (ANKARA, ESKİŞEHİR ...) — Soğutma Glikol hakedişinin GENEL İCMAL sayfasında gösterilir.</summary>
    public string? City { get; set; }

    public ICollection<ServiceForm> ServiceForms { get; set; } = new List<ServiceForm>();
}
